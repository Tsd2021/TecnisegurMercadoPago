# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

API intermedia entre los sistemas de Tecnisegur (**EmpleadoWeb**, **TSD Desktop**)
y MercadoPago, para el cobro recurrente de las cotizaciones de alarma.
**ASP.NET Core / .NET 10**, distinto al resto de la casa (`TecnisegurApi` y
`EmpleadoWeb` son .NET Framework con Web API 5 / MVC 5).

Código, comentarios e identificadores en **español**, igual que el resto de los
sistemas. Los nombres de la API de MercadoPago (`preapproval`, `init_point`,
`external_reference`) se dejan en inglés.

## Build & Run

```bash
dotnet build
dotnet run --project src/TecnisegurMercadoPago.Api
```

No hay proyecto de tests; la verificación es manual con curl/Postman contra el
sandbox de MercadoPago (ver README §3).

La app **no arranca** sin `MercadoPago:AccessToken` y `MercadoPago:WebhookSecret`
(`ValidateOnStart` en `Program.cs`). Para correr localmente, user-secrets o
variables de entorno — ver README §1.2. Nunca poner credenciales en
`appsettings.json`: ese archivo se versiona.

## Arquitectura

### Por qué existe este proyecto y no vive dentro de EmpleadoWeb

EmpleadoWeb registra `VerificarSession` como filtro **global**: toda request sin
sesión recibe un redirect al login. El webhook de MercadoPago es un POST anónimo,
así que ahí recibiría un 302, MP lo tomaría como fallo y desistiría. Sumado a que
TSD Desktop necesita la misma lógica y el access token debe estar en un solo lugar.

### Flujo de una suscripción

`SuscripcionesController` → `SuscripcionServicio` (reglas) → `MercadoPagoCliente`
(HTTP) + `SuscripcionRepositorio` (persistencia).

Se usa **`preapproval` sin plan asociado**, no `preapproval_plan`. En ese modo MP
exige `external_reference`, donde va `COT-{IdCotizacion}`; MP lo devuelve en cada
notificación y así la conciliación cotización↔pago es automática. Se crea con
`status: "pending"` para obtener `init_point` y evitar manejar datos de tarjeta
(sin carga PCI).

### El webhook guarda y responde; nunca procesa en línea

MercadoPago corta a los **22 segundos** y reintenta cada 15 minutos.
`WebhookController` sólo persiste en `MercadoPagoNotificacion` y devuelve 200.
`ProcesadorNotificaciones` (`BackgroundService`, cada 15 s) consulta MP y
actualiza estado. **No agregar llamadas a MercadoPago dentro del controller.**

Excepción: si falla la persistencia sí se devuelve 500, para que MP reintente y
la notificación no se pierda.

### Idempotencia — tres niveles, los tres necesarios

1. `X-Idempotency-Key` en el POST a MP (derivada de `external_reference` + importe).
2. Índice único filtrado `UX_SuscripcionCotizacion_Viva` sobre `IdCotizacion`
   para estados `pending|authorized|paused`.
3. `MpNotificationId` único en `MercadoPagoNotificacion`.

Si `InsertarAsync` devuelve `null` es que el índice único rechazó el insert
(carrera): `SuscripcionServicio` cancela la suscripción recién creada en MP para
no dejarla huérfana. No cambiar ese comportamiento sin entender que la
alternativa es cobrarle dos veces al cliente.

### Liberación del dinero: `approved` no es lo mismo que cobrado

La cuenta libera a **21 días**. Entre que MercadoPago aprueba el cobro y que la
plata está disponible pasan tres semanas, y por el medio se descuenta más de lo
que parece. Verificado contra el cobro real `166657246137` el 31/07/2026:

```
bruto 20,00  −  mercadopago_fee 1,22  −  tax_withholding 1,31  =  neto 17,47
```

**`Comision` y `Retenciones` son columnas separadas a propósito.** La comisión es
un costo perdido; las retenciones son adelantos de impuestos que la empresa
acredita contra DGI. Sumarlas informaría como gasto algo que en buena parte es
recuperable, y sobreestimaría el costo de MercadoPago en más del doble.

La comisión sale de sumar **`fee_details`**, que trae sólo `mercadopago_fee`. Las
retenciones son el resto: `(bruto − neto) − comisión`. No derivar la comisión de
`(bruto − neto)`: eso da comisión + retenciones juntas.

`NULL` en cualquiera de estas columnas significa "MercadoPago todavía no lo
informó", **nunca cero**. La interfaz las muestra como "—". Un neto en cero es
indistinguible de "no se sabe", y en una pantalla de cobranza esa ambigüedad es
un error de negocio.

### No hay webhook de liberación — por eso existe el repaso

MercadoPago avisa de pagos, órdenes, suscripciones, contracargos y fraude, pero
**no** de que el dinero se liberó (verificado contra la lista de tópicos el
31/07/2026). Informa `money_release_date` al aprobar y después no dice nada más.

Lo cubre `ProcesadorNotificaciones.RepasarLiberacionesAsync`: cada **24 horas**
toma hasta 100 cuotas y 100 pagos únicos sin liberación confirmada y consulta
`GET /v1/payments/{id}`. Latencia: hasta 24 h desde que MercadoPago libera.

La respuesta trae **`money_release_status`** (`pending` | `released`), que es lo
que convierte la liberación de inferencia en dato informado. Antes se deducía
comparando la fecha prevista contra hoy, lo que no distingue "se liberó" de
"pasó la fecha y nadie sabe".

En la vista, el orden del `CASE` importa: **lo que informa MercadoPago va antes
que la comparación de fechas**. Un pago con `pending` y fecha vencida es
*"A liberar"*, no *"Liberado"*.

`EstadoLiberacion` conserva sus cuatro valores porque el módulo de TSD compara
contra los literales; la precisión nueva va aparte, en `LiberacionConfirmada`
(`1` confirmado, `0` pendiente, `NULL` sin reconsultar).

Una cuota de suscripción **no** trae estos datos: `GET /authorized_payments/{id}`
devuelve un `payment` anidado con `id`, `status` y `status_detail` nada más. Por
eso hay que ir a buscar el pago completo, y por eso `ObtenerDatosLiberacionAsync`
es un método aparte. No existe liberación a nivel suscripción: el `preapproval`
no maneja dinero.

`ObtenerDatosLiberacionAsync` **nunca lanza**. Un fallo de red no puede invalidar
el registro de una cuota que ya se cobró: devuelve `Vacio`, las columnas quedan
en `NULL` y el repaso las completa después. Por lo mismo, todos los `UPDATE` de
estas columnas usan `ISNULL(@Campo, Campo)`: perder el dato es peor que no
actualizarlo.

### Seguridad

- `ApiKeyMiddleware` — header `X-Api-Key`, una clave por sistema consumidor,
  comparación en tiempo fijo. `/api/webhook` y `/health` están excluidos
  a propósito (MercadoPago no puede enviar la clave).
- `ValidadorFirmaWebhook` — HMAC-SHA256 del manifiesto
  `id:{data.id};request-id:{x-request-id};ts:{ts};` contra el header
  `x-signature`. Las notificaciones con firma inválida se guardan con
  `FirmaValida = 0` para auditoría, pero `ObtenerPendientesAsync` las filtra y
  nunca se procesan.

## Datos

Todo vive en la base **TSD** (`172.16.10.22`), junto a `CotizacionesComericales`
—el typo "Comericales" está en el esquema real, no corregirlo—. La otra base de
la organización, ENCUESTA, está en otro servidor, por lo que no hay FK posible
entre ambas.

DDL en `Database/01_CrearTablasSuscripciones.sql`, idempotente. Los scripts se
ejecutan **en orden numérico**: cada uno asume los anteriores, y varios recrean
vistas que el siguiente vuelve a extender. Todos son idempotentes.

- `SuscripcionCotizacion` — una fila por suscripción. Campos denormalizados
  (`NombreCliente`, `MontoMensual`) a propósito, para poder mostrar el estado sin
  depender de otras tablas.
- `SuscripcionPago` — una fila por cuota. Se escribe con `MERGE` sobre
  `MpAuthorizedPaymentId` porque MP notifica la misma cuota más de una vez.
- `MercadoPagoNotificacion` — log crudo, idempotencia y reproceso.
- `vw_SuscripcionesEstado` — vista de consumo con los agregados de cobranza.
  **Las lecturas van contra la vista, no contra las tablas.**

Acceso a datos: ADO.NET directo con `Microsoft.Data.SqlClient`, siguiendo la
convención del resto de los sistemas (sin ORM). Parámetros siempre tipados
(`cmd.Parameters.Add(..., SqlDbType...)`), conexión dentro de un `using`.

## Mapeo con EmpleadoWeb

Los importes los calcula `CotizacionAlarmaController.Guardar`:

| EmpleadoWeb | Acá |
|---|---|
| `Cotizacion.TotalServiciosMensual` | `montoMensual` → `auto_recurring.transaction_amount` |
| `Cotizacion.TotalProductos` | pago inicial — **no lo cubre la suscripción**, necesita Checkout Pro aparte |
| `ContratoCotizacionAlarma.Correo` | `payerEmail` |
| `ContratoCotizacionAlarma.PlazoContrato` | `plazoMeses` → `auto_recurring.end_date` |

## Comportamiento de MercadoPago que condiciona el diseño

- Reintenta una cuota rechazada hasta **4 veces en 10 días**.
- Tras **3 cuotas consecutivas rechazadas cancela la suscripción sola** y sólo
  avisa por mail al vendedor. Llega como `subscription_preapproval` con estado
  `cancelled`.
- La primera cuota se cobra **~1 hora** después de autorizada la suscripción
  (si no hay días de prueba).
- `PUT /preapproval/{id}` permite cambiar `transaction_amount` de una suscripción
  activa: es lo que hay que llamar si se edita una cotización ya suscripta, si no
  MP sigue cobrando el importe viejo.
