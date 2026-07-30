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

DDL en `Database/01_CrearTablasSuscripciones.sql`, idempotente.

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
