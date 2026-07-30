# Estado de la integración MercadoPago — API

**Última actualización:** 29 de julio de 2026
**Servicio:** `TecnisegurMercadoPago.Api` (.NET 10, ASP.NET Core)
**Entorno probado:** Sandbox de MercadoPago con cuentas de prueba (Uruguay / MLU)

---

## 1. Resumen

La API está **funcionando y validada de punta a punta** contra MercadoPago real
en entorno sandbox. Compila sin advertencias y todo el ciclo de vida de una
suscripción fue ejercitado con éxito.

**Ya está publicada y accesible desde internet** en
`https://mpapi.tecnisegur.com.uy`, sobre IIS
(`C:\inetpub\wwwroot\TecnisegurMP Api`, en la VM de Azure donde también corre
`www.tecnisegur.com.uy`). `/health` responde `Healthy` desde afuera de la red y
el `401` de `X-Api-Key` funciona. El túnel `cloudflared` ya no hace falta.

Queda pendiente que el **DNS interno** resuelva el nombre (ver §6): hoy no lo
hace, y eso bloquea a EmpleadoWeb.

**EmpleadoWeb no fue modificado.** No hay una sola línea de código nuevo ahí; la
integración desde el lado web está pendiente.

---

## 2. Qué se validó

Todas estas pruebas se hicieron contra la API real de MercadoPago, no simuladas:

| Caso | Resultado |
|---|---|
| Alta de suscripción sin plan asociado | `201` con `init_point` |
| `external_reference = COT-{IdCotizacion}` | Presente y devuelto por MP |
| Adhesión del cliente con tarjeta de prueba (titular `APRO`) | Confirmada |
| Cobro de la primera cuota | `processed` / `approved`, $2.500 |
| Transición `pending` → `authorized` | ~12 minutos tras la adhesión |
| Cambio de importe (`PUT /preapproval/{id}`) | Propagado a MP |
| Sincronización manual + recuperación de cuotas históricas | Correcta |
| Webhook: validación de firma HMAC | `FirmaValida = 1` |
| Webhook: procesamiento de evento real | `Procesado = 1` |
| Índice único: segunda suscripción para la misma cotización | `409` |
| Autenticación `X-Api-Key` ausente o inválida | `401` |
| `/api/webhook` accesible sin API key | `200` |

### Datos de la prueba

Primera vuelta (28/07, por túnel `cloudflared`) — **suscripción cancelada** el
29/07 para liberar la cotización:

```
Suscripción local (Id) : 2
PreapprovalId          : bc1ffe9b44bc40d2a06a3459f7d85bb4
Cuota cobrada          : id 7030396008 · $2.500 · processed/approved
```

> Se creó en $2.500 y el importe se modificó después; la cuota ya cobrada quedó
> en $2.500. Es correcto: el cambio de precio se aplica desde la cuota
> siguiente, no reescribe la historia.

Segunda vuelta (29/07, **sobre el servidor publicado**, sin túnel):

```
Cotización             : 14
Suscripción local (Id) : 3
PreapprovalId          : ba520aa92c92432c99e7382a0d0df32b
external_reference     : COT-14
Importe                : $7.770
Autorizada             : 29/07 14:03:45  (~2 min tras la adhesión)
Cuota cobrada          : id 7030424073 · pago 171086544690 · $7.770
                         processed / approved / accredited · 29/07 14:01:30
Próximo cobro          : 29/08/2026
```

> Toda la cadena la disparó MercadoPago por su cuenta contra
> `https://mpapi.tecnisegur.com.uy/api/webhook`: `subscription_preapproval` para
> el alta y la autorización, `subscription_authorized_payment` para la cuota.
> Nunca se llamó a `/sincronizar`.

---

## 3. Arquitectura implementada

```
EmpleadoWeb ──┐
              ├──► TecnisegurMercadoPago.Api ──► api.mercadopago.com
TSD Desktop ──┘         │
                        └──► BD TSD (SuscripcionCotizacion, SuscripcionPago,
                                     MercadoPagoNotificacion, vw_SuscripcionesEstado)
```

### Decisiones clave

- **Suscripción sin plan asociado** (`preapproval` sin `preapproval_plan_id`).
  MercadoPago exige `external_reference` en ese modo, y ahí va el `IdCotizacion`.
  Es lo que da la conciliación automática que el proceso manual no tenía.
- **`status: "pending"`** al crear, para obtener `init_point` y que el cliente
  autorice en el entorno de MercadoPago. Sin `card_token_id` no hay carga PCI.
- **El webhook guarda y responde 200 de inmediato**; el procesamiento corre en
  segundo plano cada 15 s. MercadoPago corta a los 22 segundos.
- **Idempotencia en tres niveles**: `X-Idempotency-Key` hacia MP, índice único
  filtrado por cotización viva, y `MpNotificationId` único.

### Endpoints

| Método | Ruta | Estado |
|---|---|---|
| POST | `/api/suscripciones` | ✅ probado |
| GET | `/api/suscripciones/{id:int}` | ✅ probado |
| GET | `/api/suscripciones/cotizacion/{idCotizacion}` | ✅ probado |
| PUT | `/api/suscripciones/{id:int}/monto` | ✅ probado |
| POST | `/api/suscripciones/{id:int}/cancelar` | ✅ probado |
| POST | `/api/suscripciones/{id:int}/sincronizar` | ✅ probado |
| POST | `/api/webhook` | ✅ probado (anónimo) |
| GET | `/health` | ✅ |

---

## 4. Configuración actual

Todo en **user-secrets** (entorno `Development`). Ningún valor está versionado.

| Clave | Estado |
|---|---|
| `ConnectionStrings:TSD` | Base de producción TSD (172.16.10.22) |
| `MercadoPago:AccessToken` | Cuenta **vendedora de prueba** (`...-3572201273`) |
| `MercadoPago:WebhookSecret` | Clave del **Modo productivo** de la app de prueba |
| `MercadoPago:BackUrl` | Listado de cotizaciones de EmpleadoWeb |
| `Api:Claves:WEBEMPLEADO` | Clave de desarrollo |

> **user-secrets solo se cargan en `Development`.** En el servidor van variables
> de entorno (ver `DEPLOY.md` §3).

### En el servidor

Los mismos valores están cargados como variables de entorno en
`C:\inetpub\wwwroot\TecnisegurMP Api\web.config`, con `ASPNETCORE_ENVIRONMENT=Production`.
Ese archivo tiene credenciales en texto plano: permisos NTFS restringidos a
administradores y a la identidad del pool, y **no se pisa al republicar**
(ver `DEPLOY.md` §6).

Las claves de API de `WEBEMPLEADO` y `TSD` que quedaron ahí son las definitivas
(256 bits, RNG criptográfico). El access token todavía es el de la **cuenta
vendedora de prueba** — el swap a producción es un paso aparte, a propósito, para
no mezclar dos cambios en la misma verificación.

Las credenciales y datos de las cuentas de prueba están en
`src/TecnisegurMercadoPago.Api/TecnisegurMercadoPago.Api.http`, que está
**excluido de git** (`*.http` en `.gitignore`).

### Túnel de desarrollo

> Ya no hace falta para el servidor: queda sólo para probar contra la máquina de
> desarrollo. Fue la razón principal para publicar.

`cloudflared` instalado vía winget. Se levanta con:

```bash
"C:\Program Files (x86)\cloudflared\cloudflared.exe" tunnel --url http://localhost:5274
```

**La URL cambia en cada reinicio** y hay que reconfigurarla en el panel de
MercadoPago. Es la principal razón para publicar en el servidor.

---

## 5. Las tres trampas de MercadoPago

Las tres tienen el mismo origen: MercadoPago usa la palabra "prueba" para cosas
distintas. Las tres van a reaparecer al migrar a producción.

### 5.1 "Credenciales de prueba" ≠ "cuenta de prueba"

Las *Credenciales de prueba* del panel (`TEST-...`) pertenecen a la **cuenta
real** operando en modo prueba. Una *cuenta de prueba* es una cuenta falsa
distinta, con sus propias credenciales (`APP_USR-...`).

**Atajo:** el último segmento de todo Access Token es el ID de la cuenta dueña.
- `...-3521850855` → cuenta real (TECNISEGURURUGUAY)
- `...-3572201273` → vendedora de prueba

### 5.2 Pagador y cobrador deben ser del mismo tipo

MercadoPago valida esto **dos veces**: al crear la suscripción y al pagar.

| Combinación | Crear | Pagar con tarjeta de prueba |
|---|---|---|
| Token real + email real | ✅ | ❌ |
| Token de prueba + email de prueba | ✅ | ✅ |
| Mezcladas | ❌ `Both payer and collector must be real or test users` | — |

### 5.3 El webhook tiene dos pestañas con claves distintas

*Modo de prueba* y *Modo productivo* tienen **URL y clave secreta separadas**.
La pestaña a configurar depende de qué credencial genera las operaciones:

| Token en uso | Pestaña |
|---|---|
| `TEST-...` (cuenta real en modo prueba) | Modo de prueba |
| `APP_USR-...` (cuenta de prueba **o** producción real) | **Modo productivo** |

**Síntoma de elegir mal:** "Simular notificación" funciona, pero los eventos
reales nunca llegan. El botón simular dispara a la pestaña que se está viendo.

**Diagnóstico:** el *Panel de monitoreo* de la aplicación muestra los intentos de
entrega. Sin intentos = problema de configuración; con errores = problema de red
o del endpoint.

---

## 6. Pendientes

### Fase 1 — Deploy y producción ✅ COMPLETA

> La API está publicada, accesible desde internet, operando con las credenciales
> de producción de TECNISEGURURUGUAY y con el webhook verificado. **Todavía no
> se procesó ninguna operación real.**

- [x] Instalar **ASP.NET Core 10 Hosting Bundle** en el servidor IIS.
- [x] Crear el Application Pool (**No Managed Code**) y el sitio en
      `C:\inetpub\wwwroot\TecnisegurMP Api`.
- [x] Binding `https` 443 con el wildcard `*.tecnisegur.com.uy` (Abitab, vence
      10/01/2027) y **SNI marcado** — obligatorio porque el sitio de
      `www.tecnisegur.com.uy` ya ocupa ese IP:443 en la misma VM.
- [x] Cargar los secretos como variables de entorno en el `web.config` del
      servidor (nunca en el versionado).
- [x] Generar claves de API definitivas para `WEBEMPLEADO` y `TSD`.
- [x] Verificar arranque: `/health` → `Healthy`, `/` → `401` de `X-Api-Key`.
- [x] **Registro DNS público** `A mpapi.tecnisegur.com.uy → 191.239.244.138`,
      creado por Pablo en el panel de Antel. Verificado contra el autoritativo y
      contra `8.8.8.8`; `/health` responde `200 Healthy` desde internet.
- [x] **DNS interno resolviendo.** Los resolvers `172.16.10.20/.22` tenían
      cacheado el NXDOMAIN previo a la creación del registro; se destrabó al
      expirar el TTL negativo. Verificado desde la red interna y desde fuera.
      Ver `DEPLOY.md` §2.4 si vuelve a pasar con otro nombre.
- [x] Webhook de la aplicación de **prueba** repuntado a
      `https://mpapi.tecnisegur.com.uy/api/webhook` (pestaña Modo productivo).
      Notificación simulada recibida con **`FirmaValida = 1`**: la URL responde
      y el `WebhookSecret` del servidor coincide con el del panel.
- [x] Línea temporal de `hosts` quitada del servidor.
- [x] **Webhooks reales entregados por el dominio nuevo.** El 29/07 la
      cancelación de la suscripción 2 y el alta de la 3 generaron tres
      notificaciones espontáneas de MercadoPago, las tres con `FirmaValida = 1`,
      `Procesado = 1` e `IntentosProceso = 1`. Circuito completo validado sobre
      `mpapi`: entrega → firma → persistencia → procesamiento en segundo plano.
- [x] **Flujo del cliente final validado sobre el dominio nuevo.** Suscripción 3
      (`ba520aa92c92432c99e7382a0d0df32b`, $7.770): adhesión con la cuenta
      compradora de prueba y transición `pending` → `authorized` en ~2 minutos,
      **sin** ejecutar `/sincronizar`. Autorizada 29/07 14:03:45.
- [x] Suscripciones de prueba canceladas en MercadoPago **antes** de borrar las
      filas. Borrar sólo la base no las apaga en MP: seguirían cobrando y
      mandando webhooks que caerían como *"suscripción sin registro local"*.
- [x] Data de prueba limpiada (`SuscripcionPago`, `SuscripcionCotizacion`,
      `MercadoPagoNotificacion`). Verificado: `/api/suscripciones/cotizacion/14`
      devuelve `[]`.
- [x] `MercadoPago:AccessToken` cambiado por el de **producción de la cuenta
      real** (TECNISEGURURUGUAY, `administracion@tecnisegur.com.uy`).
      Verificado contra `GET /users/me`: `id = 3521850855`, sitio MLU.
- [x] Webhook de la aplicación real configurado → pestaña **Modo productivo**,
      `https://mpapi.tecnisegur.com.uy/api/webhook`, eventos *Planes y
      suscripciones* y *Pagos (legacy)*.

      Esa aplicación es de tipo **Suscripciones**, así que el panel **no ofrece
      el evento de pagos moderno**: la única opción es *Pagos (legacy)*. Se
      verificó que igual se entrega en formato webhook moderno —JSON con
      `type: "payment"` y `data.id`—, así que el endpoint lo procesa sin
      cambios.

- [x] Clave secreta verificada con `FirmaValida = 1`.

      > **Hubo que copiarla dos veces.** La primera quedó una clave que no era
      > la de esta pestaña y *todas* las notificaciones entraban con
      > `FirmaValida = 0`, es decir, se guardaban pero no se procesaban nunca.
      > El síntoma es silencioso: la API responde 200 y MercadoPago queda
      > conforme. **Verificar siempre con una notificación simulada después de
      > tocar el `WebhookSecret`.**

### Fase 2 — Checkout de insumos (compra puntual)

Cuando el cliente compra equipamiento o insumos y **no** quiere financiarlos en
la cuota mensual, hace falta un cobro único. La suscripción no lo cubre: hay que
generar un `preference` de Checkout Pro.

- [x] `POST /api/pagos` — crear preferencia de pago único. Más
      `GET /api/pagos/{id}` y `GET /api/pagos/cotizacion/{idCotizacion}`.
- [x] Tabla `PagoUnico` con `external_reference` propio (`PAGO-{cot}-{id}`,
      columna calculada PERSISTED) y vista `vw_PagosUnicosEstado`.
      DDL en `Database/03_CrearTablaPagoUnico.sql`.
- [x] Webhook: la rama `payment` registra el cobro. Descarta las cuotas de
      suscripción mirando `preapproval_id` y el prefijo de la referencia; sin
      ese filtro el mismo cobro se contaría dos veces.
- [x] Criterio: el botón sólo aparece cuando `SumarProductosMensual = false` y
      `TotalProductos > 0`. Si los productos se financian en la cuota, la API
      devuelve 409 explicando que ya los cobra la suscripción.
- [x] DDL ejecutado en TSD y build desplegado en el servidor. Verificado:
      `GET /api/pagos/cotizacion/14` → `200 []` y `GET /api/pagos/999999` →
      `404` con el mensaje de la API, lo que prueba que la consulta llegó
      hasta `vw_PagosUnicosEstado`.
- [ ] **Ningún cobro real ejercitado todavía.** Falta una compra de prueba
      end-to-end. Ojo: el servidor ya usa credenciales de **producción**, así
      que cualquier link que se genere cobra dinero real.

### Fase 3 — Consumo desde las aplicaciones

- [x] **EmpleadoWeb**: `Models/MercadoPagoApiCliente.cs` (HttpClient estático,
      `X-Api-Key`, TLS 1.2), acciones en `CotizacionAlarmaController`, botones
      *Suscripción* y *Equipo* y columna **Cobro** en el listado. Compila.
      **Sin probar contra el servidor.**
- [ ] **Módulo TSD Desktop**: pantalla para visualizar clientes, sus pagos y el
      estado de la suscripción. Consume `vw_SuscripcionesEstado` o la API.
- [ ] Endpoints por `idCotizacion` (`PUT /api/suscripciones/cotizacion/{id}/monto`,
      `POST .../cancelar`) para que los consumidores no necesiten el Id interno.

### Fase 4 — Envío por WhatsApp (Twilio)

El código está hecho: `TwilioCliente` + `EnvioWhatsAppServicio`, y los endpoints
`POST /api/suscripciones/{id}/enviar-whatsapp` y `POST /api/pagos/{id}/enviar-whatsapp`.
La sección `Twilio` de configuración es **opcional**: sin ella la aplicación
arranca igual y esos endpoints devuelven 409 explicando qué falta.

- [ ] Cuenta de Twilio con WhatsApp habilitado y número de negocio verificado.
- [ ] Cargar `Twilio__AccountSid`, `Twilio__AuthToken`, `Twilio__NumeroOrigen`
      y poner `Twilio__Habilitado = true` en el `web.config` del servidor.
- [ ] **Plantillas aprobadas por Meta** para `ContentSidSuscripcion` y
      `ContentSidPago`.

> **Esto último es el cuello de botella real y no es trabajo de programación.**
> WhatsApp no permite texto libre en mensajes que inicia la empresa fuera de la
> ventana de 24 horas desde el último mensaje del cliente: hace falta una
> plantilla aprobada. Vale igual para Twilio y para Meta Cloud API — Twilio
> revende la misma API. La aprobación demora días y los mensajes con links de
> pago reciben escrutinio extra.
>
> Mientras tanto la interfaz cae de pie: si el envío automático falla, ofrece
> abrir `wa.me` con el mensaje ya armado para que el vendedor lo mande con un
> clic. Ese camino no necesita plantilla, cuenta de negocio ni costo por mensaje.

### Definiciones de negocio (bloquean producción)

- [ ] **Morosidad.** MercadoPago reintenta una cuota rechazada hasta 4 veces en
      10 días, y tras **3 cuotas consecutivas rechazadas cancela la suscripción
      sola**, avisando solo por mail al vendedor. Definir qué pasa con el servicio
      de monitoreo y a quién se notifica dentro de Tecnisegur.
- [ ] **Edición de cotización con suscripción viva.** ¿El cambio de importe se
      propaga automáticamente (`PUT /monto`) o se bloquea la edición?
- [ ] **Borrado de cotizaciones.** La FK impide eliminar una cotización con
      suscripción. Decidir si se deja fallar (con mejor mensaje) o se permite.
- [ ] **`FechaAutorizacion`.** Hoy guarda cuándo el sistema detectó la
      autorización, no cuándo ocurrió. MercadoPago no expone un timestamp
      explícito. Definir si se documenta así o se aproxima con la primera cuota.

---

## 7. Archivos de referencia

| Archivo | Contenido |
|---|---|
| `README.md` | Puesta en marcha, endpoints, guía de sandbox completa |
| `DEPLOY.md` | Publicación en IIS paso a paso |
| `CLAUDE.md` | Convenciones y decisiones de diseño del código |
| `Database/01_CrearTablasSuscripciones.sql` | DDL idempotente (ya ejecutado en TSD) |
| `src/.../TecnisegurMercadoPago.Api.http` | Pruebas listas para ejecutar (no versionado) |
