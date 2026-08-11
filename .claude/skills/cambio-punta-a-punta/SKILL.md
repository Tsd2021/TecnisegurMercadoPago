---
name: cambio-punta-a-punta
description: Propaga un cambio que cruza sistemas — un campo o endpoint nuevo, un dato que EmpleadoWeb debe mandar, una columna que TSD Desktop tiene que mostrar, un contrato de la API que cambia. Usar cuando el trabajo no termina en MPAPI porque toca también EmpleadoWeb2022, el módulo de cobranzas de TSD o las vistas de la base, para que no quede un extremo desactualizado en silencio.
---

# Un cambio que cruza sistemas

Tres repos consumen esta integración y ninguno se entera solo cuando el otro
cambia. El síntoma de olvidarse un extremo es siempre el mismo: **nada falla, el
dato simplemente no aparece.**

```
EmpleadoWeb2022  ──┐
                   ├──► MPAPI ──► MercadoPago
TSD Desktop      ──┘     │
                         └──► base TSD ──► vistas ──► TSD Desktop
```

## La lista de eslabones

Recorrela entera antes de decir que terminaste. Marcá cada uno como *tocado* o
*no aplica* — explícitamente, no por omisión.

| # | Eslabón | Dónde |
|---|---|---|
| 1 | Diálogo del vendedor | `../EmpleadoWeb2022/EmpleadoWeb/Views/CotizacionAlarma/CotizacionAlarma.cshtml`, función `abrirCobro` |
| 2 | Acción que llama a MPAPI | `CotizacionAlarmaController.GenerarLinkSuscripcion` / `GenerarLinkPagoUnico` |
| 3 | Cliente HTTP | `../EmpleadoWeb2022/EmpleadoWeb/Models/MercadoPagoApiCliente.cs` (`X-Api-Key`, TLS 1.2) y `RenglonCobro.cs` |
| 4 | Contrato de entrada | `Modelos/Contratos/Contratos.cs` |
| 5 | Controller | `Controllers/SuscripcionesController.cs` / `PagosController.cs` — sólo orquesta |
| 6 | Reglas de negocio | `Servicios/SuscripcionServicio.cs` / `PagoServicio.cs` |
| 7 | Payload a MP | `Servicios/MercadoPagoCliente.cs`, `Modelos/MercadoPago/*` |
| 8 | Persistencia | `Datos/*Repositorio.cs` |
| 9 | Esquema y vista | `Database/NN_*.sql` → usá la skill `migracion-sql` |
| 10 | Módulo de cobranzas | `../TSD/MODULO_COBRANZAS_MERCADOPAGO.md`, `ServicioCobranzasMercadoPago.cs` |
| 11 | Documentación | `ESTADO.md`, `PENDIENTES.md`, `README.md` (endpoints) |

## Cómo decidir hasta dónde llega

- **Campo nuevo que viaja desde el vendedor** → 1 a 9 completos. Si sólo lo
  agregás en MPAPI, EmpleadoWeb nunca lo manda y el campo queda siempre nulo.
- **Dato nuevo que informa MercadoPago** → 7, 8, 9 y **10**. Si la lógica vive en
  la vista, TSD lo muestra **sin recompilar**; si toca los literales que compara
  el código de escritorio, hay que tocar TSD.
- **Endpoint nuevo** → 4, 5, 6, más el `README.md` y el cliente HTTP de
  EmpleadoWeb. Los consumidores no deberían necesitar el Id interno: preferí
  rutas por `idCotizacion`.
- **Cambio de importe de una cotización ya suscripta** → además de la base, hay
  que llamar a `PUT /preapproval/{id}`, si no MercadoPago sigue cobrando el
  importe viejo.

## Reglas al propagar

- **Español** en todo: código, comentarios, identificadores. Los nombres de la
  API de MercadoPago quedan en inglés.
- Respetá el estilo de cada repo: MPAPI es .NET 10 moderno; EmpleadoWeb2022 y
  TecnisegurApi son .NET Framework (MVC 5 / Web API 5) y **no** admiten sintaxis
  nueva. No arrastres idioms de uno al otro.
- Los campos denormalizados de `SuscripcionCotizacion` (`NombreCliente`,
  `MontoMensual`) son deliberados: permiten mostrar el estado sin depender de
  otras tablas. No los "normalices".
- Un cambio de contrato que rompa a un consumidor no se mergea sin tocar al
  consumidor. Los dos repos están en `../`: abrilos y verificá, no supongas.
- `dotnet build` en MPAPI. Los otros dos compilan con MSBuild/Visual Studio: si
  no podés compilarlos, **decilo** en vez de dar por buena la modificación.

## Mapeo de importes (viene de `CotizacionAlarmaController.Guardar`)

| EmpleadoWeb | MPAPI |
|---|---|
| `Cotizacion.TotalServiciosMensual` | `montoMensual` → `auto_recurring.transaction_amount` |
| `Cotizacion.TotalProductos` | pago inicial — **no lo cubre la suscripción**, va por Checkout Pro |
| `ContratoCotizacionAlarma.Correo` | `payerEmail` |
| `ContratoCotizacionAlarma.PlazoContrato` | `plazoMeses` → `auto_recurring.end_date` |
| `ContratoCotizacionAlarma.Documento` | `documento` del pagador (pago único) |

## Al cerrar

Decí qué extremos quedaron tocados, cuáles no aplican y **cuáles quedaron
pendientes por no poder verificarlos**. Un extremo silenciosamente sin actualizar
es la falla más cara de esta integración.
