# Análisis de la cadena de cobro — qué viaja, qué se guarda, qué falla

**Fecha:** 10 de agosto de 2026
**Motivo:** hay clientes que abren el link de suscripción y no pueden pagar, y
altas que fallan con un 500 sin cuerpo útil. No había forma de saber en qué
punto se rompía.

Este documento hace el inventario completo del recorrido de los datos y explica
los cuatro defectos que se encontraron. Los arreglos ya están aplicados; lo que
queda pendiente está al final.

---

## 1. El recorrido de los datos

```
Diálogo del vendedor  →  EmpleadoWeb  →  MPAPI  →  MercadoPago
                                          ↓
                                    base TSD
```

### 1.1 Lo que se le pide al vendedor

`EmpleadoWeb2022/EmpleadoWeb/Views/CotizacionAlarma/CotizacionAlarma.cshtml`,
función `abrirCobro`. El diálogo pide **tres** datos:

| Campo | Precargado con | Obligatorio |
|---|---|---|
| Correo del comprador | `contrato.Correo` | sí |
| Teléfono para WhatsApp | `contrato.TelefonoContacto` | no |
| Día de la adhesión (sólo suscripción) | vacío | no |

**No pide documento.** La cédula ya está en el contrato
(`ContratoCotizacionAlarma.Documento`) y hasta ahora no se usaba para nada
relacionado con cobros.

### 1.2 Lo que EmpleadoWeb le manda a MPAPI

`CotizacionAlarmaController.GenerarLinkSuscripcion`:

| Campo | Origen |
|---|---|
| `idCotizacion` | parámetro de la llamada |
| `nombreCliente` | `contrato.NombreCliente` → `cotizacion.Cliente` → `"Cliente"` |
| `payerEmail` | lo que tipeó el vendedor |
| `montoMensual` | `cotizacion.TotalServiciosMensual` |
| `diasPrueba` | siempre `null` — excluyente con `fechaInicio` |
| `fechaInicio` | día de adhesión, o `null` |
| `plazoMeses` | `contrato.PlazoContrato` |
| `usuarioCreacion` | usuario de la sesión |
| `origen` | `"WEBEMPLEADO"` |
| `telefono` | del diálogo, o `contrato.TelefonoContacto` |

`GenerarLinkPagoUnico` manda lo mismo salvo que el importe es
`cotizacion.TotalProductos` y ahora suma **`documento`** (ver §3.4).

### 1.3 Lo que MPAPI le manda a MercadoPago

`POST /preapproval` — suscripción **sin plan asociado**, en `status: "pending"`:

```json
{
  "reason": "TECNISEGUR ALARMAS - {nombre}",
  "external_reference": "COT-{idCotizacion}",
  "payer_email": "...",
  "back_url": "https://.../?cotizacion=N",
  "notification_url": "https://mpapi.tecnisegur.com.uy/api/webhook",
  "status": "pending",
  "auto_recurring": {
    "frequency": 1,
    "frequency_type": "months",
    "transaction_amount": 0.00,
    "currency_id": "UYU",
    "start_date": "...",
    "end_date": "...",
    "free_trial": { "frequency": N, "frequency_type": "days" }
  }
}
```

Más el header `X-Idempotency-Key: COT-{id}-{monto}`.

> **El correo es el único dato del pagador que viaja en una suscripción.** No
> hay nombre, ni documento, ni teléfono, ni dirección: la API de `preapproval`
> no tiene campos para eso. El nombre del cliente aparece sólo como texto dentro
> de `reason`, que es lo que el cliente ve en el checkout.

### 1.4 Lo que se guarda en la base TSD

- **`SuscripcionCotizacion`** — una fila por suscripción: `IdCotizacion`,
  `ExternalReference`, `PreapprovalId`, `InitPoint`, `NombreCliente`,
  `PayerEmail`, `MontoMensual`, `Moneda`, `DiasPrueba`, `FechaInicio`, `Estado`,
  las fechas del ciclo, `MotivoCancelacion`, `UsuarioCreacion`, `Origen` y —
  nuevo — **`PayloadEnvioJson`**.
- **`SuscripcionPago`** — una fila por cuota, con el desglose de liberación
  (`MontoNeto`, `Comision`, `Retenciones`, `EstadoLiberacionMp`).
- **`MercadoPagoNotificacion`** — log crudo de webhooks, con `FirmaValida`.
- Las lecturas van siempre contra **`vw_SuscripcionesEstado`**, nunca contra las
  tablas.

No se guardan el documento ni el teléfono del cliente: ya viven en
`ContratoCotizacionAlarma` y duplicarlos sólo crearía dos versiones del dato.

---

## 2. Las tres preguntas que estaban abiertas

### ¿El correo tiene que ser real? — **Sí, y es lo más crítico del circuito**

`payer_email` es el identificador del suscriptor. La documentación de
MercadoPago lo dice sin vueltas: *"To create a subscription, it must be linked
to an email address, which allows us to assign a unique identifier to the
subscriber"*. Por ese correo van **todos** los avisos: cobro exitoso, cuota
rechazada, y la cancelación automática que MercadoPago hace sola tras tres
cuotas caídas.

Un relleno tipo `NOTIENE@NOTIENE.COM` tiene dos consecuencias, y la segunda es
peor que la primera:

1. Puede hacer fallar el alta con un 500 sin cuerpo útil.
2. Si el alta pasa, el cliente **nunca se entera de nada**. Para un servicio de
   monitoreo de alarma eso significa que puede quedar sin cobertura contratada
   sin que nadie lo sepa.

Que el link se mande por WhatsApp no lo salva: el WhatsApp entrega el link una
vez, los avisos posteriores son todos por correo.

Hay además una restricción de MercadoPago: **el pagador no puede ser el
cobrador**. Si el correo tipeado es el de la cuenta de Tecnisegur, se rechaza.

### ¿La cédula tiene que ser real? — **Sí, pero no es algo que mandemos nosotros**

Dos cosas distintas que conviene no mezclar:

1. **La API de suscripciones no tiene campo de identificación.** No existe
   `identification` en `POST /preapproval`. No hay nada que corregir del lado
   nuestro para las suscripciones.
2. **La cédula la tipea el cliente en el checkout de MercadoPago**, y ahí sí
   tiene que ser la real: el emisor la valida contra el titular de la tarjeta.
   Además, al autorizar, MercadoPago hace *"un cobro con un importe mínimo"* que
   después reembolsa, para acreditar que la tarjeta sirve. Si la cédula no
   corresponde al titular, el emisor rechaza y el cliente no puede suscribirse.

**Esto explica una parte de "abre el link y no puede pagar" que no depende de
nuestro código.** Lo único que se puede hacer desde acá es precargarle el dato
para que no se equivoque — y eso sólo se puede en el pago único (§3.4).

El `CI 12345678` que figura en el README sirve **sólo** con tarjetas de prueba.

### ¿El número tiene que ser real? — **Depende de cuál**

- **Teléfono:** no viaja a MercadoPago en las suscripciones. Sólo lo usa Twilio
  para mandar el link por WhatsApp. Un teléfono equivocado impide que llegue el
  link, no que se pueda pagar.
- **Tarjeta:** con credenciales de producción, real y con fondos. Las tarjetas
  de prueba del README fallan contra la cuenta productiva.

---

## 2 bis. La causa real: la cuenta no tiene habilitada la facturación

**Reproducido en sandbox el 10/08/2026.** El síntoma que reportó el vendedor era:
el cliente abre el link, carga la tarjeta, y MercadoPago devuelve *"Tuvimos un
problema. Estamos trabajando para solucionarlo, por favor intenta de nuevo"* —
y después la suscripción se cancela sola.

Se armó el experimento que aísla la causa: **el mismo payload exacto que produce
producción** —`back_url` sin barra, `free_trial` 15 días, sin `notification_url`,
$950— pero con el vendedor de prueba en vez del productivo. Se completó el
checkout con tarjeta de prueba y titular `APRO`.

**Autorizó sin problema:** `status: authorized`, `payment_method_id: master`.

O sea que **el payload no tiene nada que ver**. La única diferencia entre las dos
corridas es la cuenta cobradora, y ahí está la asimetría:

```
PRODUCCIÓN  TECNISEGURURUGUAY (3521850855)
   billing.allow = False   codes: address_pending    → falla al autorizar

PRUEBA      TESTUSER8777124058636789264 (3572201273)
   billing.allow = True    codes: (ninguno)          → autoriza
```

Las dos son cuenta `personal`, las dos con `sell.allow: true`, las dos en MLU.
Lo único que las separa es `billing.allow`.

Encaja con todo lo demás que se midió: falla **después** de cargar la tarjeta,
que es cuando MercadoPago constituye el débito recurrente; el error es genérico y
del lado de ellos; `payment_method_id` quedó vacío en las 7 suscripciones reales;
y los pagos sueltos de julio sí entraron, porque ésos no pasan por facturación
recurrente.

### De dónde sale ese `address` vacío

`GET /users/me` no es un recurso de MercadoPago: es el **usuario de MercadoLibre**
—la respuesta trae `permalink: http://perfil.mercadolibre.com.uy/TECNISEGURURUGUAY`—.
Los campos `address` y `phone` son de ese perfil.

La cuenta se creó el 06/07/2026 por el flujo de empresas de MercadoPago
(`context.source: "mercadopago-company"`, `flow: "mp-v3"`), registrándose con un
teléfono. Ese flujo no pide dirección, así que el perfil de MercadoLibre quedó
vacío. Se ve el mismo patrón en el teléfono: el verificado vive en
`registration_identifiers` (lado MercadoPago) y `phone` (lado MercadoLibre) está
en blanco.

La cuenta de prueba tiene dirección sólo porque `POST /users/test_user` genera
una sintética (`"Test Address 123"`, zip `12800`).

### Qué tan firme es esto — leer antes de actuar

**No está confirmado que `billing.allow` sea la causa.** El experimento prueba
que el payload es inocente y que *algo* del entorno productivo bloquea. Cuál es
ese algo sigue abierto. Tres reparos concretos:

- **La prueba no aisló una sola variable.** El vendedor de prueba usa su propia
  aplicación de MercadoPago (`244721644743240`), distinta de la productiva
  (`437871649677590`). Entre las dos corridas cambiaron la cuenta **y** la
  aplicación.
- La documentación de MercadoLibre describe `address_pending` como la
  restricción que impide **publicar artículos en MercadoLibre**. No hay
  documentación que la vincule con la facturación recurrente de MercadoPago.

### Los dos candidatos, descartados (10/08, tarde)

**`address_pending`: corregido, no era.** Se cargó la dirección desde
*Direcciones* de la cuenta —no desde datos fiscales, que viene de la DGI y es de
sólo lectura— y `GET /users/me` pasó a `billing.allow: true` con `codes: []`.
Se generó una suscripción nueva (`8ef74c91585845b3b72e985de1ee0e7a`, COT-36,
$15) con la API ya publicada —`back_url` con barra, `notification_url`, sin
`end_date`— y **falló igual**: el comprador cargó tarjeta, cédula y su propio
correo, el checkout no mostró ningún error, lo redirigió a
`https://www.mercadopago.com` y la suscripción se auto-canceló a los 23 segundos.

**`sandbox_mode`: es residual, no era.** Se creó una aplicación nueva desde el
panel (`4615318147228313`) y nació con `sandbox_mode: true` igual que la
productiva. Toda aplicación del panel viene así; el campo no distingue nada y no
hay forma de cambiarlo (`PUT /applications/{id}` devuelve 403).

### Estado: agotado desde afuera

Todo lo verificable sin acceso interno a MercadoPago quedó descartado: payload,
`back_url`, fechas, coincidencia de correo, webhook, dirección de la cuenta y
modo de la aplicación.

El hecho más filoso, y el que hay que empujar con soporte: **nunca se registra
un intento de cobro**. `GET /v1/payments/search` sigue devolviendo los mismos 7
pagos históricos, el más reciente del 29/07, ninguno de una suscripción. La
tarjeta que carga el comprador jamás se convierte en un `payment`, y el
preapproval queda siempre sin `payment_method_id`. Eso ocurre enteramente dentro
de MercadoPago.

Otro dato para ellos: la redirección va al `callback_url` de la **aplicación**
(`https://www.mercadopago.com`, sin configurar) y no al `back_url` del
preapproval, lo que sugiere que el flujo se corta a nivel de aplicación.

**Tampoco se puede corregir con las credenciales que tenemos.** El
`PUT https://api.mercadolibre.com/users/{id}` con `address/city/state/zip_code`
devuelve **403** (`PA_UNAUTHORIZED_RESULT_FROM_POLICIES`): el token es de una
aplicación de MercadoPago y escribir el perfil de MercadoLibre exige un token
OAuth de ML con permiso de escritura. Por `api.mercadopago.com` la ruta ni
existe (404). El panel de MercadoPago tampoco lo expone: el único domicilio que
muestra es el fiscal, que viene de la DGI y es de sólo lectura.

Quedan dos caminos, en este orden:

1. **Iniciar sesión en `mercadolibre.com.uy`** —no en mercadopago— con la misma
   cuenta y completar la dirección desde el perfil de MercadoLibre, que es donde
   vive el campo.
2. **Soporte de MercadoPago**, con el `preapproval_id` de una suscripción
   fallida y la comparación de las dos cuentas. Es también el camino si al
   cargar la dirección el problema persiste.

Verificación en cualquier caso:

```
GET https://api.mercadopago.com/users/me   →   status.billing.allow, address
```

### El correo del checkout debe coincidir — trampa aparte, verificada

**Verificado en sandbox el 10/08/2026.** Se creó una suscripción con
`payer_email = A` y se completó el checkout escribiendo el correo `B`.
MercadoPago rechaza:

```
URL: .../congrats/recover/subscription-invalid-user/

"El pago falló...
 Tu e-mail no coincide con el de la suscripción
 Intenta nuevamente con el e-mail correcto.
 Si no lo recuerdas, contacta al vendedor."
```

El `payer_email` no le ahorra el paso al cliente: la pantalla de confirmación
tiene un campo *"Ingresá tu e-mail"* **vacío y obligatorio**, y lo que escriba
tiene que coincidir exactamente con lo declarado al crear el preapproval.

**El WhatsApp que se le manda no dice cuál es.** Un cliente que abre el link
escribe el suyo, y el rechazo llega después de haber cargado la tarjeta.

> **Esto NO explica la falla de Pablo del 10/08**, que vio *"Tuvimos un
> problema"* —el mensaje genérico— y no éste. Son dos fallas distintas. Mientras
> la cuenta siga bloqueada, todos chocan primero contra la genérica y ésta queda
> latente. Se arregló igual, para que no sea el próximo misterio apenas se
> destrabe la cuenta.

Arreglo aplicado: el diálogo que muestra el link generado en EmpleadoWeb ahora
muestra el correo que el cliente tiene que usar, para que el vendedor se lo pase
junto con el link. Queda pendiente agregarlo también al mensaje de WhatsApp, que
es una plantilla de Twilio (`ContentSid`) y necesita una variable nueva dada de
alta de ese lado.

### Dos hallazgos laterales de la misma prueba

- **La cédula de prueba del README es inválida.** `README.md:293` documenta
  `CI 12345678`, y el checkout la rechaza con *"Ingresá un documento válido"*: la
  cédula uruguaya lleva dígito verificador y ese número no lo cumple. Para
  `1.234.567` el correcto es **`-2`** (`12345672`).
- **El checkout pide el correo otra vez.** Aunque el preapproval lleva
  `payer_email`, la pantalla de confirmación tiene un campo *"Ingresá tu e-mail"*
  vacío y obligatorio. El `payer_email` sirve para vincular al suscriptor, pero
  no le ahorra el paso al cliente.

---

## 3. Los cuatro defectos

### 3.1 `end_date` se calculaba desde hoy — podía quedar *antes* de `start_date`

Estaba así:

```csharp
EndDate = solicitud.PlazoMeses.HasValue
    ? DateTimeOffset.Now.AddMonths(solicitud.PlazoMeses.Value)   // ← desde hoy
```

Con `PlazoContrato = 1` y adhesión a 45 días, la suscripción **terminaba antes
de empezar**. MercadoPago acepta ese preapproval en `pending` sin chistar —lo
verifica el script de diagnóstico— y recién falla cuando el cliente entra al
`init_point` a autorizar. El peor momento posible para enterarse.

**Corregido:** el plazo se cuenta desde que empieza a cobrarse, incluyendo los
días de prueba:

```csharp
var inicioCobro = (fechaInicio ?? DateTimeOffset.Now).AddDays(diasPrueba);
var fechaFin = solicitud.PlazoMeses.HasValue
    ? inicioCobro.AddMonths(solicitud.PlazoMeses.Value)
    : (DateTimeOffset?)null;
```

Queda además una comprobación explícita de que el fin sea posterior al inicio.
Con el cálculo nuevo no puede fallar —`PlazoMeses` es como mínimo 1— pero deja
el invariante escrito para quien toque estas fechas más adelante.

### 3.2 `start_date` caduca entre que se genera el link y el cliente lo abre

`NormalizarFechaInicio` rechaza fechas pasadas **al crear**, pero la fecha se
congela dentro del preapproval. Si el vendedor pone la adhesión para mañana y el
cliente abre el link una semana después, ese `start_date` **ya es pasado en el
momento de autorizar**. La suscripción queda en `pending` para siempre y nadie
se entera.

Es el que mejor explica el "a veces": depende puramente de cuánto tarda el
cliente en hacer clic.

**No se puede arreglar en el alta** — el problema es el paso del tiempo, no el
payload. Lo que se agregó es la forma de detectarlo: la consulta 1 de
`Database/11_TrazaAltaSuscripcion.sql` lista las suscripciones `pending` con la
adhesión vencida. Esas hay que cancelarlas y volver a generar el link; no se
arreglan solas.

### 3.3 `payer_email` no se validaba de verdad

El DTO tenía `[EmailAddress]`, que valida forma y poco más. El front pedía una
arroba y un punto. Pasaban placeholders, dominios inexistentes y el correo de la
propia cuenta cobradora.

**Corregido** en `SuscripcionServicio.ValidarCorreoPagador`, que rechaza antes
de gastar una llamada a MercadoPago:

- lo que no parsea con `MailAddress` (más estricto que el atributo);
- los dominios de la lista `MercadoPago:DominiosCorreoVetados`, configurable
  porque la lista crece cada vez que alguien inventa un relleno nuevo;
- el correo de la cuenta cobradora, si se configuró `MercadoPago:CorreoCobrador`.

Los tres devuelven **409 con un mensaje redactado para el vendedor**, que
EmpleadoWeb ya sabe mostrar. La validación del front se endureció en paralelo,
para dar la respuesta sin ida y vuelta al servidor.

### 3.4 `back_url` mal compuesta, sin `notification_url`, y el documento sin usar

Tres cosas en el mismo lugar:

- **`back_url` se armaba concatenando texto.** Si la base no terminaba en `/`,
  salía `https://www.tecnisegur.com.uy?cotizacion=31` — sin barra antes del `?`.
  Válido por RFC, rechazado por bastantes validadores. **Corregido** con
  `UriBuilder`, en el helper compartido `Servicios/UrlRetorno.cs`: el mismo
  defecto estaba duplicado en los pagos únicos.
- **`notification_url` no se mandaba en el preapproval**, sólo en las
  preferencias de Checkout Pro. Las suscripciones dependían únicamente del
  webhook configurado en el panel. **Corregido:** ahora va en los dos, así una
  suscripción ya creada sigue notificando aunque el panel cambie.
- **El documento del contrato no se usaba.** En suscripciones no se puede — la
  API no tiene el campo. En **Checkout Pro sí**: la `preference` acepta
  `payer.identification`, y precargar la cédula evita que el cliente la tipee
  mal y el emisor lo rechace. **Corregido:** `PreferenciaPagador` ahora lleva
  `identification` y `phone`, y EmpleadoWeb manda `contrato.Documento`. Los
  dígitos se limpian antes de enviarlos: en el contrato la cédula se carga a
  mano y suele venir como `1.234.567-8`.

### 3.5 Por qué no se sabía: no quedaba rastro del fracaso

No se guardaba el payload enviado, ni la respuesta, ni ninguna señal de que un
pagador hubiera abierto el link sin poder pagar. La única evidencia era una fila
`pending` que envejecía.

**Corregido:** `SuscripcionCotizacion.PayloadEnvioJson` guarda el JSON tal cual
salió. Con eso se puede reconstruir con qué `start_date` y qué `end_date` se
creó cada suscripción, que es donde estuvieron los dos errores de fecha.

`NULL` en esa columna significa **"se creó antes de esta instrumentación"**,
nunca "se mandó vacío".

---

## 4. Qué se cambió

**MPAPI**

| Archivo | Cambio |
|---|---|
| `Servicios/SuscripcionServicio.cs` | `end_date` desde la adhesión; validación del correo; verificación de la cuenta; `notification_url`; persiste el payload |
| `Servicios/CacheEstadoCuenta.cs` | **nuevo** — cachea 10 min el estado de facturación |
| `Modelos/MercadoPago/UsuarioCuenta.cs` | **nuevo** — respuesta de `GET /users/me` |
| `Servicios/UrlRetorno.cs` | **nuevo** — arma la URL de retorno con `UriBuilder` |
| `Servicios/PagoServicio.cs` | usa `UrlRetorno`; manda identificación y teléfono |
| `Modelos/MercadoPago/Preapproval.cs` | `notification_url` |
| `Modelos/MercadoPago/Preference.cs` | `identification`, `phone` |
| `Modelos/Contratos/Contratos.cs` | `Documento` en `CrearPagoSolicitud` |
| `Configuracion/MercadoPagoOpciones.cs` | `DominiosCorreoVetados`, `CorreoCobrador` |
| `Datos/SuscripcionRepositorio.cs` | persiste `PayloadEnvioJson` |
| `Database/11_TrazaAltaSuscripcion.sql` | **nuevo** — la columna y tres consultas de higiene |
| `Herramientas/DiagnosticarAltaSuscripcion.ps1` | dos variantes nuevas de fechas incoherentes |

**EmpleadoWeb**

| Archivo | Cambio |
|---|---|
| `Controllers/CotizacionAlarmaController.cs` | manda `contrato.Documento` en el pago único |
| `Models/MercadoPagoApiCliente.cs` | `documento` en el DTO |
| `Views/CotizacionAlarma/CotizacionAlarma.cshtml` | validación de correo con regex y lista de rellenos |

---

## 5. Lo que queda pendiente

- **Ejecutar `Database/11_TrazaAltaSuscripcion.sql`** en TSD. Sin eso el alta
  falla: el `INSERT` referencia la columna nueva.
- **Correr las consultas de higiene** para ver cuántas suscripciones `pending`
  hay con la adhesión vencida. Es lo que confirma cuál de los defectos estuvo
  actuando en producción.
- **Configurar `MercadoPago__CorreoCobrador`** en el `web.config` del servidor.
  Sin él la comprobación de pagador≠cobrador no corre — no rompe nada, pero se
  pierde un mensaje claro.
- **Verificar `MercadoPago__BackUrl`** en el servidor. El valor que apareció en
  el log del 31/07 (`https://www.tecnisegur.com.uy?cotizacion=31`) no coincide
  con el que `DEPLOY.md` dice que debe estar
  (`https://empleado.tecnisegur.com.uy/CotizacionAlarma/RetornoSuscripcion`).
- **Publicar** API y EmpleadoWeb — ver `DEPLOY.md` y `PENDIENTES.md` §1.
- **El MCP de MercadoPago no está conectado.** `.mcp.json` referencia
  `${MERCADOPAGO_MCP_TOKEN}` y esa variable no existe. La documentación citada
  acá salió del sitio público de developers.

Lo que sigue abierto en `PENDIENTES.md` no se tocó: cancelación de pagos únicos
(§2), pool de IIS para el `BackgroundService` (§3bis.1), contracargos tardíos
(§3bis.2), permisos de `CotizacionAlarmaController` (§4) y las definiciones de
morosidad (§5).

---

## Referencias

- [Crear suscripción — API Reference](https://www.mercadopago.com.ar/developers/en/reference/online-payments/subscriptions/create-preapproval/post)
- [Suscripción sin plan asociado, pago pendiente](https://www.mercadopago.com.uy/developers/es/docs/subscriptions/integration-configuration/subscription-no-associated-plan/pending-payments)
- [Suscripción sin plan asociado, pago autorizado](https://www.mercadopago.com.co/developers/en/docs/subscriptions/integration-configuration/subscription-no-associated-plan/authorized-payments)
