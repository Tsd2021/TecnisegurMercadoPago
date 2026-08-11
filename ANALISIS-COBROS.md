# Análisis de la cadena de cobro — qué viaja, qué se guarda, qué falla

**Fecha:** 10 de agosto de 2026
**Motivo:** hay clientes que abren el link de suscripción y no pueden pagar, y
altas que fallan con un 500 sin cuerpo útil. No había forma de saber en qué
punto se rompía.

Este documento hace el inventario completo del recorrido de los datos y explica
los cuatro defectos que se encontraron. Los arreglos ya están aplicados; lo que
queda pendiente está al final.

> **Leer §2 ter antes que §2 bis.** El 11/08 se midió dónde falla exactamente y
> el resultado deja desactualizada la sección "Estado: agotado desde afuera".

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

- ~~**La prueba no aisló una sola variable.**~~ El vendedor de prueba usa su
  propia aplicación de MercadoPago (`244721644743240`), distinta de la
  productiva (`437871649677590`), así que entre las dos corridas cambiaron la
  cuenta **y** la aplicación. **Resuelto el 11/08 con el control de §2 quinquies:
  cambiando sólo la aplicación, el resultado no cambia.**
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

### ~~Estado: agotado desde afuera~~

> **Desactualizado al 11/08/2026.** Quedaba una medición que no se había hecho y
> que sí resuelve el punto de falla: ver §2 ter. Lo de abajo se conserva porque
> el inventario de descartes sigue siendo válido.

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

---

## 2 ter. Dónde falla, medido — 11/08/2026

**El medio de pago nunca se asocia.** La suscripción no se cae después de
autorizar: nunca llega a autorizar, porque MercadoPago no completa la
vinculación de la tarjeta. El fallo es **anterior** a la autorización del
preapproval.

### Cómo se midió

`GET /preapproval/{id}` sobre COT-36 (`8ef74c91585845b3b72e985de1ee0e7a`), la
suscripción que falló el 10/08:

```
status               cancelled
payer_id             1858717677
card_id              null
payment_method_id    null
date_created         2026-08-10T20:24:31.155Z
last_modified        2026-08-10T20:24:53.989Z      (22,8 s después)
free_trial           15 días · first_invoice_offset 15
```

Por sí solos esos `null` no prueban nada: un preapproval cancelado **sí** vacía
campos —`init_point` viene en null y sabemos que existió, porque el cliente
abrió el link—. Hacía falta saber si la cancelación también borra la tarjeta.

**No la borra.** En la cuenta de prueba hay tres preapproval cancelados que la
conservan:

| Preapproval | Status | `card_id` | `payment_method_id` |
|---|---|---|---|
| `bc1ffe9b…` | cancelled | 9851793766 | `master` |
| `79f90fd0…` | cancelled | 9835956553 | `master` |
| `ba520aa9…` | cancelled | — | `account_money` |
| `fdc015eb…` | authorized | 9852888026 | `debvisa` |

`bc1ffe9b…` es la suscripción 2 de `ESTADO.md`: autorizó, cobró una cuota de
$2.500 y se canceló el 29/07. Trece días después sigue mostrando la tarjeta.
Los otros seis cancelados de esa cuenta —los que crea y cancela
`DiagnosticarAltaSuscripcion.ps1` sin que nadie los autorice— tienen los dos
campos vacíos. El patrón cierra en las dos direcciones:

```
llegó a authorized  →  card_id / payment_method_id poblados, sobreviven a la baja
nunca autorizó      →  los dos en null
```

Con el control positivo y el negativo medidos, los `null` de COT-36 significan
lo que parecían significar: **nunca hubo tarjeta asociada.**

### El contraste entre cuentas, sin depender de `billing.allow`

| | Preapproval | Con medio de pago asociado |
|---|---|---|
| **Prueba** (3572201273) | 10 | **4** |
| **Producción** (3521850855) | 9 | **0** |

Mismo payload, mismo site MLU, mismo modelo `pending` → `init_point`. En una
cuenta el checkout constituye el débito recurrente; en la otra, nueve de nueve
veces, no.

### Lo que NO se puede concluir de la ausencia de cobros

`authorized_payments: 0` y `payments: 0` **no son indicio de error**. COT-36
tenía `free_trial` de 15 días: la primera cuota no vencía hasta el 25/08. Ese
dato es compatible con una suscripción correctamente autorizada y está además
sobredeterminado —la suscripción murió a los 22 segundos—, así que no distingue
nada. Un análisis anterior lo usó como evidencia; era incorrecto.

Por lo mismo, `FechaAutorizacion IS NULL` en 9 de 9 prueba que **nunca
observamos** `authorized`, no que nunca ocurrió. Lo que lo prueba es la tarjeta.

### Herramientas

- `Herramientas/ForensePreapproval.ps1` — sólo GET; imprime los campos que
  deciden el punto de falla y acumula una línea temporal en JSONL.
- `Database/12_ForenseUnaSuscripcion.sql` — la mitad local: fila, cuotas y
  todas las notificaciones de una suscripción.

### Hallazgos laterales de la misma medición

- **MercadoPago responde `cancelled`, con dos eles**, tanto en el GET como en lo
  que quedó en la base (7 filas con `MotivoCancelacion = 'Cancelada en
  MercadoPago'`, que sólo se escribía comparando contra ese literal). Que
  *acepte* `canceled` en el PUT no está verificado.
- **`GET /preapproval` no devuelve `version` ni `payer_email`.** El `version` de
  los webhooks saltó de 0 a 2: hubo una mutación intermedia que nunca se
  notificó, y no se puede reconstruir. **Medido después sobre los 18 preapproval
  (§2 quater): pasa en 15, pero los 3 que sí la emitieron fallaron igual. No
  sirve como pista.**
- **`payer_id` poblado no indica avance del checkout.** COT-16 está `pending`,
  nunca autorizó, y también lo tiene: MercadoPago lo resuelve del `payer_email`
  al crear.
- **El reloj de `SERVER22\SQLSERVER2022` está 2 min 10 s adelantado.** El alta de
  COT-36 fue a las 17:24:31 de Uruguay y la base anotó 17:26:41. Afecta
  cualquier correlación entre nuestras fechas y las de MercadoPago.

### Qué queda

Todo lo verificable desde afuera está agotado, ahora con el punto de falla
identificado. La pregunta para soporte es concreta: **por qué el checkout de
suscripciones no completa la vinculación del medio de pago en la cuenta
3521850855, si el mismo payload la completa en una cuenta de prueba.**

---

## 2 quater. Lo que se midió con el MCP conectado — 11/08/2026

El MCP quedó conectado por OAuth contra la cuenta productiva. Tres mediciones,
dos hipótesis descartadas y una pregunta nueva para soporte.

### El historial de entregas de MercadoPago no sirve como prueba de ausencia

`notifications_history` sobre la app 437871649677590 informa **25 entregas en el
último mes, 25 exitosas, 0 fallidas, todas HTTP 200**, todas del tópico
`subscription_preapproval`. Como prueba de que nuestro endpoint responde bien es
excelente: es el log de ellos, no el nuestro.

Como prueba de que *no* generaron algo, no sirve. La tabla local tiene **50
notificaciones** en 31 días. El desglose plausible —23 de suscripciones nuestras
y 26 del script de diagnóstico— encierra al 25 sin coincidir con ninguno, así
que el historial no cubre todo lo que efectivamente recibimos. **No se puede
argumentar "MercadoPago nunca lo emitió" apoyándose en él.**

### El salto de `version` se descarta como pista

Medido sobre los 18 preapproval con notificaciones: **en 15 falta la
notificación intermedia** (`0 → 2`). Es el patrón dominante, no una anécdota del
caso del ticket. Pero los tres que **sí** la emitieron terminaron igual:

```
58abfe8c…  COT-35  cancelled   0 → 1 → 2
1141bdb1…  COT-35  cancelled   0 → 1 → 2
```

Las dos son de EmpleadoWeb contra la cuenta productiva. Recibir la notificación
intermedia no cambió el desenlace, así que el hueco **no separa el caso que
funciona del que no**. Se sacó del ticket: ofrecida como pista mandaba a soporte
a investigar el pipeline de notificaciones, que no es donde está el problema.

### La homologación no existe para Suscripciones

`quality_checklist` sobre la app devuelve `Product not homologable`, y la
documentación explica por qué: la herramienta de calidad *"solo está disponible
para integraciones con Checkout Pro, Checkout API, Checkout Bricks y Mercado
Pago Point"*. `form_homologation` con `product_id 28` (Subscription) devuelve
`steps: []`.

**"La aplicación no está homologada" queda descartado**: no es una puerta
cerrada, es una puerta que no existe.

### La pregunta nueva: "Etapa 1 de 5"

El panel muestra la aplicación en *Estado: Etapa 1 de 5*, con los tres ítems de
"Prueba tu integración" tildados y sin forma de avanzar. Coherente con lo
anterior —las etapas siguientes son homologación y calidad, que para
Suscripciones no aplican—, pero **no verificable desde afuera**: ningún endpoint
devuelve la etapa.

En contra de leerlo como "la app sigue en modo prueba" juega que esa cuenta ya
liquidó dinero real (los 7 pagos de julio, con comisión y retenciones). A favor,
que esos fueron Checkout Pro y no débito recurrente. Va como pregunta 4 del
ticket.

### El `notification_url` del preapproval no sirve para nada — y §3.4 lo daba por bueno

La documentación dice, repetido en catorce páginas: *"Este método de
configuración no está disponible para integraciones con QR Code ni
Suscripciones. Para configurar notificaciones con alguna de estas dos
integraciones, utiliza el método Configuración durante la creación de un pago"*.

**Es al revés.** Medido el 11/08 con tres pruebas independientes:

| Aplicación | Webhook en el panel | Notificaciones |
|---|---|---|
| 437871649677590 (productiva) | sí | 25 de 25 entregadas |
| 709858592631421 (segunda productiva) | no | historial vacío |
| 2447216447432403 (cuenta de prueba) | no | ninguna |

Las tres mandaron `notification_url` en el POST. La regla real es una sola:

> Notifica **sólo** la aplicación que tiene el webhook configurado en el panel.
> El `notification_url` del payload MercadoPago lo descarta.

La prueba de que lo descarta y no que simplemente el GET no lo devuelve: el
preapproval `6b7133c7d6b44cb79af81c916168c36c`, creado con el campo, quedó con
`notification_url` vacío **estando en `pending`**. No es el vaciado de campos
que hace una baja. Queda un fleco: `498c7bbb` —creado por la app productiva— sí
lo devuelve en el GET, lo que sugiere que MercadoPago sólo lo acepta cuando la
aplicación ya tiene notificaciones habilitadas.

Esto también disuelve una contradicción que quedaba anotada: los preapproval
`DIAG-*` del 10/08 notificaron porque eran de **producción**, no de la cuenta de
prueba. `GET /preapproval/63c894da…` con el token de prueba devuelve
`BadRequest`.

**Lo que hay que corregir de §3.4.** El comentario de `ArmarNotificationUrl`
afirmaba que mandar el campo dejaba a cada suscripción atada a su propia URL, de
modo que tocar el panel no afectara a las ya creadas. No existe esa red: si
alguien cambia la configuración del panel, las suscripciones vivas dejan de
notificar y la conciliación se corta sin ningún error visible. El comentario ya
está corregido en el código.

**Consecuencia operativa:** migrar de aplicación exige configurarle el webhook en
el panel **antes** de mover el access token. Si no, los cobros se dejan de
conciliar en silencio — que es el peor modo de falla posible acá.

### El hueco del `Id` 29 — investigado y cerrado, no era un bug

La fila de `8ef74c91` (COT-36, 10/08) no está, y el `Id` 29 falta en la
secuencia. Se sospechó de la rama de la carrera del índice único
(`SuscripcionServicio`), que es el único camino que consume un IDENTITY sin
dejar fila **y** manda el único `PUT` de cancelación no pedido por un usuario.
Habría significado que veníamos cancelando suscripciones de clientes y leyéndolo
como "MercadoPago las cancela solas".

**No fue eso.** El log del servidor lo dice literal:

```
Suscripción 29 creada para la cotización 36 (8ef74c91585845b3b72e985de1ee0e7a).
Suscripción 8ef74c91… sincronizada. Estado: pending.
Suscripción 8ef74c91… sincronizada. Estado: cancelled.
```

El insert prosperó, y no hay una sola línea de *"Alta rechazada por índice
único"* en ningún log. La fila se borró a mano después —consistente con que la
tabla arranque en `Id` 21 y con que `PagoUnico` tenga 0 filas y el IDENTITY en
6—. La baja de `8ef74c91` la informó MercadoPago, igual que las demás, así que
**§2 ter se sostiene**.

Los preapproval huérfanos de esos días son los `DIAG-A/B/C/D` del script de
diagnóstico. `b54d74ad` (COT-31) es de las filas `Id` ≤ 20 que se borraron.

Dos cosas quedaron a la vista y valen aparte:

- **Una notificación sin fila local se marca `Procesado = 1` con `ErrorProceso`
  en NULL** ([`ProcesadorNotificaciones`](../src/TecnisegurMercadoPago.Api/Servicios/ProcesadorNotificaciones.cs)
  loguea un warning y retorna normal). En la base es indistinguible de una que
  actualizó bien; el único rastro está en el log de la aplicación. Nueve
  preapproval pasaron por ahí sin dejar huella en la base.
- **Se borran filas a mano en producción.** Es lo que hizo que este rastro
  costara tres consultas y un log. Conviene decidir si se permite o no.

### Herramientas de esta medición

- `Herramientas/CruceNotificaciones.ps1` — ejecuta un SQL contra TSD y se niega
  si el texto contiene verbos de escritura. Habilitado por ruta exacta en
  `.claude/settings.local.json`.
- `Database/14_CruceNotificacionesMp.sql` — el cruce entre lo que MercadoPago
  dice que entregó y lo que quedó en la base, y la atribución de cada
  preapproval a su fila local.

---

## 2 quinquies. El control que faltaba: es la cuenta, no la aplicación

**Medido el 11/08/2026.** La objeción de §2 bis —que el experimento contra la
cuenta de prueba cambiaba la cuenta *y* la aplicación al mismo tiempo— quedaba
sin resolver. Se cerró creando una **segunda aplicación bajo la misma cuenta
cobradora** y repitiendo el alta con sus credenciales de producción, de modo que
la única variable que cambia sea la aplicación.

```
preapproval        085af30debdd43b2b3f425ba02432bc5
application_id     709858592631421      ← distinta
collector_id       3521850855           ← la misma
date_created       2026-08-11T20:01:50.915Z
last_modified      2026-08-11T20:02:37.792Z    (46,9 s)
status             cancelled
card_id            null
payment_method_id  null
billing.allow      true    codes: []
```

El comprador completó el checkout con tarjeta real y el resultado fue idéntico
al de la aplicación productiva. Con eso la matriz queda cerrada:

```
misma app,  otra cuenta   →  autoriza
otra app,   misma cuenta  →  falla
```

**La variable que determina el resultado es la cuenta cobradora 3521850855.**
Ya no queda nada del lado de la integración por descartar.

### Dos cosas que este experimento aclaró de paso

- **La redirección al inicio de mercadopago.com no es señal de nada.** La
  aplicación nueva tampoco tiene `callback_url` configurada, y produce la misma
  pantalla. §2 bis la ofrecía como indicio de que "el flujo se corta a nivel de
  aplicación"; no lo es, es el destino por defecto de una app sin URL de retorno.
  En el ticket quedó como observación, no como argumento.
- **El MCP no puede crear la aplicación.** `create_application` responde
  `OAuth ownership validation failed`: la conexión sirve para leer pero no está
  autenticada como aplicación OAuth. La app se creó a mano desde el panel.

### Herramienta

`Herramientas/AltaConOtraApp.ps1` — crea UN preapproval con el token de otra
aplicación y lo deja vivo para poder autorizarlo (a diferencia de
`DiagnosticarAltaSuscripcion.ps1`, que cancela todo lo que crea). Aborta si el
token resulta ser de otra cuenta: con dos variables cambiadas la corrida no
mide nada, y es preferible frenar que sacar una conclusión falsa.

---

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

> Actualizado el 11/08. Los dos primeros puntos ya estaban hechos.

- ~~Ejecutar `Database/11_TrazaAltaSuscripcion.sql`~~ — **ejecutado**. La columna
  `PayloadEnvioJson` existe y COT-36 la tiene poblada.
- ~~Correr las consultas de higiene~~ — **corridas**. Quedan dos suscripciones
  `pending` abandonadas (COT-22 desde el 31/07, COT-16 desde el 04/08).
- **Configurar `MercadoPago__CorreoCobrador`** en el `web.config` del servidor.
  Sin él la comprobación de pagador≠cobrador no corre — no rompe nada, pero se
  pierde un mensaje claro.
- **`MercadoPago__BackUrl`** — el requisito quedó definido el 11/08:
  `https://www.tecnisegur.com.uy/`, sin ningún parámetro. `UrlRetorno` ya no
  agrega `?cotizacion=N`; el valor del servidor sale normalizado con la barra
  final. **No** se implementa `RetornoSuscripcion`: el retorno es sólo
  navegación y la sincronización es enteramente server-side.
- **Publicar** API y EmpleadoWeb — ver `DEPLOY.md` y `PENDIENTES.md` §1.
- **Abrir el ticket con soporte de MercadoPago.** `TICKET-SOPORTE-MP.md` está
  listo para copiar y pegar, con la evidencia de §2 ter y el control de
  §2 quinquies. Es lo único que queda por hacer sobre este problema: del lado de
  la integración no hay nada más que descartar.
- ~~El MCP de MercadoPago no está conectado.~~ — **conectado el 11/08.**
  `application_list` responde con la app 437871649677590. Lo que se pudo medir
  con él está en §2 quater.

Lo que sigue abierto en `PENDIENTES.md` no se tocó: cancelación de pagos únicos
(§2), pool de IIS para el `BackgroundService` (§3bis.1), contracargos tardíos
(§3bis.2), permisos de `CotizacionAlarmaController` (§4) y las definiciones de
morosidad (§5).

---

## Referencias

- [Crear suscripción — API Reference](https://www.mercadopago.com.ar/developers/en/reference/online-payments/subscriptions/create-preapproval/post)
- [Suscripción sin plan asociado, pago pendiente](https://www.mercadopago.com.uy/developers/es/docs/subscriptions/integration-configuration/subscription-no-associated-plan/pending-payments)
- [Suscripción sin plan asociado, pago autorizado](https://www.mercadopago.com.co/developers/en/docs/subscriptions/integration-configuration/subscription-no-associated-plan/authorized-payments)
