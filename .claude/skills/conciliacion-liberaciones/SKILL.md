---
name: conciliacion-liberaciones
description: Concilia lo que MercadoPago cobró contra lo que la empresa recibe — comisión, retenciones de DGI, neto acreditado, fecha y estado de liberación del dinero (los 21 días). Usar cuando haya que verificar el desglose de un cobro real, cuadrar la base contra el reporte de Liberaciones, entender por qué un pago figura "A liberar" o "Liberado", o tocar las columnas Comision / Retenciones / NetoAcreditado / FechaLiberacion / EstadoLiberacion / LiberacionConfirmada.
---

# Conciliación del dinero

`approved` no es lo mismo que cobrado. La cuenta libera a **21 días** y por el
medio se descuenta más de lo que parece.

Verificado contra el cobro real `166657246137` (31/07/2026):

```
bruto                          20,00
  mercadopago_fee              -1,22   (6,09 %)
  tax_withholding-uruguay      -0,98   (5 %)
  tax_withholding-lif_debito   -0,33   (2 %)
                             -------
neto acreditado                17,47          descuento real: 12,65 %
```

## La distinción que sostiene todo esto

**Comisión y retenciones son dos columnas, no una.** La comisión es un costo
perdido; las retenciones son adelantos de impuestos que la empresa acredita
contra DGI. Sumarlas informaría como gasto algo recuperable y sobreestimaría el
costo de MercadoPago en más del doble.

- `Comision` = suma de **`fee_details`** (se verificó que trae sólo
  `mercadopago_fee`).
- `Retenciones` = `(bruto − neto) − comisión`.
- **Nunca** derives la comisión de `(bruto − neto)`: eso da las dos juntas. Es
  exactamente el error que corrige `Database/09_SepararComisionDeRetenciones.sql`.

## Verificar un cobro real

```bash
powershell -File Herramientas/VerificarDesglosePago.ps1 -PagoId <id> -Pedir
```

Contrasta la API de pagos contra el reporte de Liberaciones y contra lo que
persiste la base; las tres fuentes tienen que coincidir **al centavo**.
`money_release_date` puede diferir del reporte en un par de segundos por
redondeo: eso es normal.

Para bajar el reporte crudo: `Herramientas/ReporteLiberaciones.ps1 -Dias 30`.
Queda en `Herramientas/reportes/`, que está fuera de git a propósito — son datos
financieros de la cuenta.

## De dónde sale el estado de liberación

`GET /v1/payments/{id}` informa **`money_release_status`** (`pending` |
`released`). Eso convierte la liberación de inferencia en dato informado.

> `"released"` se verificó con los ojos. `"pending"` sale de la documentación: no
> había ningún cobro en retención para mirar.

Una cuota de suscripción **no** trae esto: `GET /authorized_payments/{id}`
devuelve un `payment` anidado con `id`, `status` y `status_detail` nada más. Por
eso hay que ir al pago completo, y por eso `ObtenerDatosLiberacionAsync` es un
método aparte. No existe liberación a nivel suscripción: el `preapproval` no
maneja dinero.

## No hay webhook de liberación

Verificado contra la lista oficial de tópicos: MercadoPago avisa de pagos,
órdenes, suscripciones, contracargos y fraude, pero **no** de que el dinero se
liberó. Informa `money_release_date` al aprobar y después calla.

Lo cubre `ProcesadorNotificaciones.RepasarLiberacionesAsync`: cada **24 h** toma
hasta 100 cuotas y 100 pagos únicos sin liberación confirmada y reconsulta.
Latencia: hasta 24 h desde que MP libera.

```
Día 0        autoriza → webhook → cuota cobrada (~1 h)
             se guardan FechaLiberacion (día 21) y EstadoLiberacionMp='pending'
Días 1-20    "A liberar" (amarillo en TSD)
Día 21+24 h  el repaso trae 'released' → "Liberado s/MP" (verde)
Días 21-31   sigue en el conjunto por si hay reversión
Día 31+      sale
```

El filtro del repaso incluye **`Comision IS NULL`**
(`SuscripcionRepositorio.cs:360`). Sin esa condición, una cuota con neto y fecha
ya cargados nunca volvía a consultarse y se quedaba sin comisión para siempre —
el script 09 anula esa columna en las filas viejas justo para que se recalculen.

## Reglas al tocar estas columnas

1. **`NULL` es "MercadoPago no lo informó todavía", nunca cero.** La interfaz
   muestra "—". Un neto en cero indistinguible de "no se sabe" es un error de
   negocio en una pantalla de cobranza.
2. Todo `UPDATE` usa **`ISNULL(@Campo, Campo)`**: perder el dato es peor que no
   actualizarlo.
3. `ObtenerDatosLiberacionAsync` **nunca lanza**. Un fallo de red no puede
   invalidar el registro de una cuota ya cobrada: devuelve `Vacio` y el repaso
   completa después.
4. En el `CASE` de la vista, **lo informado por MercadoPago va antes que la
   comparación de fechas**. `pending` con fecha vencida es *"A liberar"*.
5. `EstadoLiberacion` conserva sus **cuatro** valores: el módulo de TSD compara
   contra los literales. La precisión nueva va en `LiberacionConfirmada`
   (`1` confirmado, `0` pendiente, `NULL` sin reconsultar).
