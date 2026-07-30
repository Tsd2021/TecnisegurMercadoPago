# Tecnisegur — API MercadoPago

Servicio intermedio entre los sistemas de Tecnisegur y MercadoPago para el cobro
recurrente de las cotizaciones de alarma.

Lo consumen **EmpleadoWeb** (clase `CotizacionAlarma`) y el **form de TSD Desktop**.

```
EmpleadoWeb ──┐
              ├──► TecnisegurMercadoPago.Api ──► api.mercadopago.com
TSD Desktop ──┘         │
                        └──► BD TSD (SuscripcionCotizacion, SuscripcionPago, ...)
```

## Por qué es un proyecto aparte

El webhook de MercadoPago llega como un **POST anónimo**. En EmpleadoWeb el filtro
global `VerificarSession` redirige al login toda request sin sesión, así que MP
recibiría un 302, lo tomaría como fallo y terminaría desistiendo. Además el form
de TSD necesita la misma lógica, y el access token debe estar en un solo lugar.

---

## 1. Puesta en marcha

### 1.1 Base de datos

Ejecutar contra **TSD** (`172.16.10.22`), donde viven las cotizaciones:

```
Database/01_CrearTablasSuscripciones.sql
```

El script es idempotente: se puede correr más de una vez. Crea
`SuscripcionCotizacion`, `SuscripcionPago`, `MercadoPagoNotificacion` y la vista
`vw_SuscripcionesEstado`.

### 1.2 Credenciales

**Nunca** en `appsettings.json` — ese archivo se versiona.

En desarrollo, user-secrets:

```bash
cd src/TecnisegurMercadoPago.Api
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:TSD"        "Server=172.16.10.22;Database=TSD;User ID=...;Password=...;TrustServerCertificate=True"
dotnet user-secrets set "MercadoPago:AccessToken"      "TEST-xxxxxxxx"
dotnet user-secrets set "MercadoPago:WebhookSecret"    "xxxxxxxx"
dotnet user-secrets set "MercadoPago:BackUrl"          "https://empleado.tecnisegur.com.uy/CotizacionAlarma/RetornoSuscripcion"
dotnet user-secrets set "Api:Claves:WEBEMPLEADO"       "$(openssl rand -hex 32)"
dotnet user-secrets set "Api:Claves:TSD"               "$(openssl rand -hex 32)"
```

En el servidor, variables de entorno (doble guion bajo separa niveles):

```
ConnectionStrings__TSD
MercadoPago__AccessToken
MercadoPago__WebhookSecret
Api__Claves__WEBEMPLEADO
```

La aplicación **no arranca** si falta `AccessToken` o `WebhookSecret`
(`ValidateOnStart`). Es a propósito: sin secreto no se puede validar la firma de
los webhooks, y arrancar sin esa validación sería peor que no arrancar.

### 1.3 Compilar y correr

```bash
dotnet build
dotnet run --project src/TecnisegurMercadoPago.Api
```

---

## 2. Endpoints

Todos requieren el header `X-Api-Key`, **salvo** `/api/webhook` y `/health`.

| Método | Ruta | Para qué |
|---|---|---|
| POST | `/api/suscripciones` | Crea la suscripción y devuelve el `initPoint` para el cliente |
| GET | `/api/suscripciones/{id}` | Estado + historial de cuotas |
| GET | `/api/suscripciones/cotizacion/{idCotizacion}` | Suscripciones de una cotización |
| PUT | `/api/suscripciones/{id}/monto` | Cambia el importe mensual |
| POST | `/api/suscripciones/{id}/cancelar` | Cancela en MP y localmente |
| POST | `/api/suscripciones/{id}/sincronizar` | Relee el estado desde MP |
| POST | `/api/webhook` | Notificaciones de MercadoPago (anónimo) |
| GET | `/health` | Estado del servicio |

### Alta de suscripción

```http
POST /api/suscripciones
X-Api-Key: <clave de WEBEMPLEADO>
Content-Type: application/json

{
  "idCotizacion": 123,
  "nombreCliente": "Mario Cortez",
  "payerEmail": "cliente@ejemplo.com",
  "montoMensual": 2525.00,
  "diasPrueba": 15,
  "plazoMeses": 24,
  "usuarioCreacion": "jperez"
}
```

`montoMensual` es `Cotizacion.TotalServiciosMensual`, tal como lo calcula
`CotizacionAlarmaController.Guardar`. `plazoMeses` sale de
`ContratoCotizacionAlarma.PlazoContrato`.

Respuesta `201`:

```json
{
  "idSuscripcion": 1,
  "idCotizacion": 123,
  "preapprovalId": "2c93808...",
  "initPoint": "https://www.mercadopago.com.uy/subscriptions/checkout?preapproval_id=...",
  "estado": "pending",
  "montoMensual": 2525.00,
  "moneda": "UYU",
  "diasPrueba": 15
}
```

`initPoint` es el link que se le envía al cliente (correo o WhatsApp).

Si la cotización ya tiene una suscripción viva devuelve **409** con el motivo:
es la defensa contra el doble cobro.

---

## 3. Probar en sandbox

Sí, hay terreno de prueba completo. **Nada de esto toca dinero real.**

### 3.1 Crear las cuentas de prueba

1. Entrar a [Tus integraciones](https://www.mercadopago.com.uy/developers/panel)
   con la cuenta de Tecnisegur y crear una aplicación.
2. Dentro de la aplicación → **"Crear cuenta de prueba"**. Se necesitan **dos**,
   ambas de Uruguay: **Vendedor** y **Comprador**.
3. Se pueden crear hasta 15 cuentas de prueba y **no se pueden borrar**.

### 3.1.b ⚠️ De qué cuenta sacar el Access Token

Este es el punto que más tiempo hace perder. Hay **dos cosas distintas** que se
llaman "prueba":

| | Qué es | El cobrador es… |
|---|---|---|
| **Credenciales de prueba** (`TEST-...`) | La cuenta **real** de Tecnisegur en modo prueba | Una cuenta **real** |
| **Cuenta de prueba** (test user) | Una cuenta **falsa** completa | Una cuenta **de prueba** |

MercadoPago exige que el pagador y el cobrador sean **los dos reales o los dos
de prueba**. Si se usa el `TEST-...` de la cuenta real junto a un
`payer_email` de tipo `@testuser.com`, el alta falla con:

```json
{"message":"Both payer and collector must be real or test users","status":400}
```

**Cómo saber de qué cuenta es un token:** el último segmento es el ID de la
cuenta dueña. `TEST-...-3521850855` pertenece a la cuenta real
(`TECNISEGURURUGUAY`). Para confirmarlo: `GET https://api.mercadopago.com/users/me`.

Hay **dos combinaciones válidas**. Las dos funcionan; lo que no se puede es
mezclarlas.

#### Opción 1 — Ambos reales (la más rápida, no requiere crear nada)

- Token: el de **Credenciales de prueba** del panel (`TEST-...-3521850855`).
- `payer_email`: **un email real**, distinto de `administracion@tecnisegur.com.uy`
  (no se puede uno suscribir a sí mismo).

Verificado: `POST /preapproval` devuelve `201` con `init_point`. Como el token
es `TEST-`, no se mueve dinero real.

#### Opción 2 — Ambos de prueba (entorno 100% aislado)

1. Ventana privada → `mercadopago.com.uy` → entrar con el usuario y contraseña
   de la cuenta **VENDEDORA de prueba**.
2. **Tus integraciones** → **Crear aplicación** → producto **Suscripciones**.
3. Esa aplicación → **Credenciales de producción** → copiar el Access Token.
   Debe terminar en el ID de esa cuenta.
4. `payer_email` = el email de la cuenta **COMPRADORA de prueba**.

> El token va a empezar con `APP_USR-...`, no con `TEST-`. **No está mal.** Como
> la cuenta entera es falsa, sus credenciales de producción ya son el entorno de
> prueba. Por eso, logueado como usuario de prueba, la sección "Credenciales de
> prueba" ni siquiera aparece.

#### Cuál usar

MercadoPago valida la coherencia **dos veces**: al crear la suscripción y al
autorizar el pago. La Opción 1 pasa la primera validación pero **falla en la
segunda**: al abrir el `init_point` logueado con la cuenta compradora de prueba
aparece

> *"Algo salió mal… Una de las partes con la que intentas hacer el pago es de prueba."*

porque el pagador de esa suscripción es una identidad real.

| | Opción 1 (ambos reales) | Opción 2 (ambos de prueba) |
|---|---|---|
| Crear suscripción | ✅ | ✅ |
| Obtener `init_point` | ✅ | ✅ |
| Autorizar y cobrar con tarjetas de prueba | ❌ | ✅ |

**Para el ciclo completo hay que usar la Opción 2.** Los cuatro elementos tienen
que ser de prueba: token del vendedor, `payer_email` del comprador, el login al
pagar, y la tarjeta. La Opción 1 sólo sirve para validar rápido que el alta
funciona.

En la Opción 2, la URL y la clave secreta del webhook salen de **esa misma
aplicación**, no de la de la cuenta real. Si se mezclan, las firmas no validan.

### 3.2 Configurar el webhook

MercadoPago necesita alcanzar la API por HTTPS público. En desarrollo, un túnel:

```bash
ngrok http 5199
# o: cloudflared tunnel --url http://localhost:5199
```

En el panel → **Webhooks**, configurar la URL `https://<tunel>/api/webhook` y
marcar el evento **"Planes y suscripciones"** (cubre `subscription_preapproval`
y `subscription_authorized_payment`). No marcar "Pagos (legacy)": duplica
notificaciones que ya llegan por el evento de suscripciones.

Copiar la **clave secreta** que muestra el panel a `MercadoPago:WebhookSecret`.
Sin ella toda notificación se registra con `FirmaValida = 0` y nunca se procesa.

#### ⚠️ Modo de prueba vs. Modo productivo — cuál configurar

La pantalla de Webhooks tiene **dos pestañas**, cada una con **su propia URL y su
propia clave secreta**. Elegir la equivocada produce un síntoma muy confuso:
*"Simular notificación" funciona, pero los eventos reales nunca llegan.*

La regla es **de qué credencial salen las operaciones**:

| Access Token en uso | Pestaña a configurar |
|---|---|
| `TEST-...` (credenciales de prueba de la cuenta real) | **Modo de prueba** |
| `APP_USR-...` (credenciales de una cuenta de prueba) | **Modo productivo** |
| `APP_USR-...` (credenciales de producción reales) | **Modo productivo** |

Con la Opción 2 de §3.1.b se usa el `APP_USR-` del vendedor de prueba. Esa cuenta
es falsa, pero **adentro de ella no existe un "modo prueba"**: todo lo que hace
es actividad productiva. Por eso los eventos reales salen por **Modo productivo**.

El botón "Simular notificación" dispara a la pestaña que se esté viendo, así que
puede dar la falsa sensación de que todo está bien.

**Verificado**: con la URL y la clave del Modo productivo, un cambio de importe
generó `subscription_preapproval` con `FirmaValida = 1` y `Procesado = 1`.

#### Diagnóstico cuando no llegan notificaciones

El **Panel de monitoreo** de la aplicación muestra los intentos de entrega:

- **Sin intentos registrados** → MercadoPago nunca intentó enviar. Es problema de
  configuración (casi siempre, la pestaña equivocada).
- **Intentos con error** → MP envía pero no llega, o la API responde mal. Ahí hay
  que mirar el túnel o el endpoint.

MercadoPago reintenta cada 15 minutos, así que una notificación perdida por un
reinicio de la API puede aparecer sola más tarde.

### 3.3 Tarjetas de prueba (Uruguay)

| Tipo | Marca | Número | CVV | Vencimiento |
|---|---|---|---|---|
| Crédito | Mastercard | 5031 7557 3453 0604 | 123 | 11/30 |
| Crédito | Visa | 4509 9535 6623 3704 | 123 | 11/30 |
| Débito | Visa | 4410 1036 7243 6886 | 123 | 11/30 |

El resultado se fuerza con el **nombre del titular**:

| Nombre | Resultado |
|---|---|
| `APRO` | Aprobado |
| `FUND` | Rechazado por fondos insuficientes |
| `OTHE` | Rechazado por error general |
| `SECU` | Código de seguridad inválido |
| `EXPI` | Error de fecha de vencimiento |
| `CONT` | Pendiente |

Documento: CI `12345678`.

### 3.4 Recorrido de prueba

1. `POST /api/suscripciones` con una cotización real → devuelve `initPoint`.
2. Abrir el `initPoint` **en una ventana privada**, logueado con la cuenta de
   prueba **compradora**.
3. Pagar con una tarjeta de prueba, titular `APRO`.
4. Verificar:

```sql
SELECT * FROM dbo.vw_SuscripcionesEstado WHERE IdCotizacion = 123;
SELECT * FROM dbo.MercadoPagoNotificacion ORDER BY FechaRecepcion DESC;
```

El estado debe pasar de `pending` a `authorized`. La primera cuota se cobra
**aproximadamente una hora después** de autorizada (si no hay días de prueba).

5. Para simular morosidad, repetir con titular `FUND`.

> **Ojo con los días de prueba:** con `diasPrueba: 15` no se cobra nada durante
> 15 días, así que no se va a ver ninguna cuota. Para probar el cobro,
> usar `diasPrueba: 0`.

### 3.5 Probar el webhook sin MercadoPago

La firma se puede reproducir a mano:

```bash
TS=1700000000; RID="req-abc"; DID="12345"
HASH=$(printf 'id:%s;request-id:%s;ts:%s;' "$DID" "$RID" "$TS" \
       | openssl dgst -sha256 -hmac "$WEBHOOK_SECRET" | sed 's/.*= //')

curl -X POST "http://localhost:5199/api/webhook?data.id=${DID}" \
  -H "Content-Type: application/json" \
  -H "x-request-id: ${RID}" \
  -H "x-signature: ts=${TS},v1=${HASH}" \
  -d '{"id":99001,"type":"subscription_preapproval","action":"created","data":{"id":"12345"}}'
```

---

## 4. Cómo está armado

### El webhook guarda y responde; no procesa

MercadoPago corta a los **22 segundos** y reintenta cada 15 minutos. Consultar la
API de MP dentro del request pondría esa ventana en riesgo, así que
`WebhookController` sólo persiste en `MercadoPagoNotificacion` y devuelve 200.
`ProcesadorNotificaciones` (BackgroundService, cada 15 s) hace el trabajo real.

Si falla la persistencia sí se devuelve 500: así MP reintenta y la notificación
no se pierde.

### Idempotencia en tres niveles

1. `X-Idempotency-Key` en el POST a MercadoPago — el doble click no crea dos suscripciones.
2. Índice único filtrado sobre `IdCotizacion` — no puede haber dos suscripciones vivas.
3. `MpNotificationId` único — MP reenvía notificaciones y no deben duplicar cuotas.

Si el insert local pierde una carrera, la suscripción recién creada en MP se
cancela automáticamente para no dejarla huérfana.

### Seguridad

- Sistemas internos: header `X-Api-Key`, una clave por sistema, comparación en
  tiempo fijo. Se puede revocar el acceso de uno sin afectar al otro.
- Webhook: firma `x-signature` (HMAC-SHA256). Las notificaciones con firma
  inválida se registran con `FirmaValida = 0` para auditoría, pero el procesador
  nunca las toma.

---

## 4.b Estado de la validación

Probado de punta a punta contra MercadoPago (sandbox, cuentas de prueba):

| Paso | Estado |
|---|---|
| Alta de suscripción + `init_point` | ✅ |
| Adhesión del cliente con tarjeta de prueba | ✅ |
| Cobro de la primera cuota (`processed` / `approved`) | ✅ |
| Cambio de importe (`PUT /monto`) propagado a MP | ✅ |
| Sincronización manual + recuperación de cuotas históricas | ✅ |
| Webhook: firma HMAC validada (`FirmaValida = 1`) | ✅ |
| Webhook: evento real procesado (`Procesado = 1`) | ✅ |
| Defensa contra doble suscripción (índice único → 409) | ✅ |
| Autenticación por `X-Api-Key` (401 sin clave) | ✅ |

Tiempos observados: la suscripción queda en `pending` unos minutos tras la
adhesión y pasa a `authorized` cuando se cobra la primera cuota — en la prueba
tardó **~12 minutos**, no la hora que menciona la documentación.

## 5. Pendientes antes de producción

- [x] ~~Confirmar que la cuenta vendedora es la correcta.~~ **Verificado**: el
      `"TT TRANSPO…"` del panel es `TRANSPORTADORA DE VALORES TECNISEGUR`
      (id `3521850855`, nickname `TECNISEGURURUGUAY`,
      `administracion@tecnisegur.com.uy`). Es la cuenta correcta.
- [ ] Definir qué pasa con el servicio de monitoreo cuando MercadoPago cancela
      una suscripción por 3 cuotas rechazadas consecutivas, y a quién se notifica.
- [ ] Definir si editar una cotización con suscripción viva propaga el nuevo
      importe (`PUT /monto`) o queda bloqueado.
- [ ] Link de pago inicial del equipamiento (Checkout Pro) para cuando
      `SumarProductosMensual = false`.
- [ ] Publicar en IIS con el **ASP.NET Core Hosting Bundle** instalado, con
      subdominio fijo. El túnel de desarrollo cambia de URL cada vez que se
      reinicia y hay que reconfigurarla en el panel.
- [ ] **Migración sandbox → producción.** Las suscripciones quedan atadas a la
      cuenta que las creó. Al cambiar el `AccessToken` por el de producción,
      todas las de prueba van a devolver `403 The caller is not authorized`.
      Antes de migrar, cerrar o eliminar esas filas:
      ```sql
      DELETE p FROM SuscripcionPago p
        JOIN SuscripcionCotizacion s ON s.Id = p.IdSuscripcion
        WHERE s.Origen = 'WEBEMPLEADO' AND s.UsuarioCreacion = 'dchiquiar';
      DELETE FROM SuscripcionCotizacion WHERE UsuarioCreacion = 'dchiquiar';
      DELETE FROM MercadoPagoNotificacion;
      ```
- [ ] Reconfigurar el webhook en la aplicación **real** (pestaña Modo productivo)
      y cargar **esa** clave secreta. La del entorno de prueba no sirve.
- [ ] Decidir el significado de `FechaAutorizacion`: hoy guarda cuándo el sistema
      detectó la autorización, no cuándo ocurrió. MercadoPago no expone un
      timestamp explícito de autorización en el `preapproval`.
