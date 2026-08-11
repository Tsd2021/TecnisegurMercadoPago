---
name: forense-webhook
description: Rastrea una notificación de MercadoPago que no llegó, no se procesó o se procesó mal — pagos que MP cobró pero no aparecen en la base ni en TSD, notificaciones con FirmaValida = 0, Procesado = 0 con ErrorProceso, IntentosProceso creciendo, "suscripción sin registro local", reproceso manual. Usar ante cualquier duda sobre el circuito entrega → firma → persistencia → ProcesadorNotificaciones.
---

# Forense de un webhook

El circuito tiene cuatro puntos de falla y cada uno se diagnostica distinto:

```
entrega de MP  →  firma HMAC  →  persistencia  →  ProcesadorNotificaciones
```

Consultas listas y comentadas: `Database/02_VerificacionWebhook.sql`
(sólo lectura, se puede correr en producción).

## Punto 1 — ¿Está llegando algo?

```sql
SELECT TOP 20 Id, FechaRecepcion, Tipo, Accion, DataId,
       FirmaValida, Procesado, IntentosProceso, ErrorProceso
FROM dbo.MercadoPagoNotificacion ORDER BY Id DESC;
```

**Vacío = MercadoPago no está entregando.** El problema es de configuración, no
de la aplicación. Confirmalo en el *Panel de monitoreo* de la aplicación en MP,
que muestra los intentos de entrega:

- Sin intentos → configuración (URL o **pestaña equivocada**).
- Con errores → red o endpoint.

La trampa clásica: el panel tiene **Modo de prueba** y **Modo productivo**, con
URL y clave secreta separadas. Con `APP_USR-...` va *Modo productivo*. Elegir mal
es silencioso — el botón "Simular notificación" dispara a la pestaña que estás
mirando, así que *funciona*, y los eventos reales nunca llegan.

## Punto 2 — `FirmaValida = 0`

Llega y se guarda, pero **nunca se procesa**: `ObtenerPendientesAsync` filtra las
firmas inválidas a propósito, y se conservan sólo para auditoría.

Causa casi siempre única: el `WebhookSecret` cargado no es el de esa pestaña.
Ya pasó una vez y costó encontrarlo, porque el síntoma es mudo — la API responde
200 y MercadoPago queda conforme.

`ValidadorFirmaWebhook` valida HMAC-SHA256 del manifiesto
`id:{data.id};request-id:{x-request-id};ts:{ts};` contra el header `x-signature`.

> **Después de tocar el `WebhookSecret`, verificá siempre con una notificación
> simulada y confirmá `FirmaValida = 1`.**

## Punto 3 — Persistencia

Si falla el guardado, el controller devuelve **500 a propósito** para que MP
reintente (cada 15 minutos) y la notificación no se pierda. Todo lo demás
responde 200 de inmediato: MP corta a los **22 segundos**.

`MpNotificationId` es único: los reenvíos de la misma notificación no duplican.

**Nunca agregues una llamada a MercadoPago dentro de `WebhookController`.**

## Punto 4 — `Procesado = 0` con `ErrorProceso`

Acá el trabajo lo hace `ProcesadorNotificaciones` (cada 15 s, lotes de 20). Leé
`ErrorProceso` e `IntentosProceso` antes de tocar nada.

Errores típicos y qué significan:

| Error | Lectura |
|---|---|
| *Suscripción sin registro local* | Llega un `preapproval` que no está en la base. Suele ser una suscripción de prueba borrada de la base pero **no cancelada en MP**: sigue viva y cobrando. |
| Timeout / 5xx contra MP | Transitorio. Se reintenta solo; mirá si `IntentosProceso` crece sin parar. |
| Cuota duplicada | Es esperable: `SuscripcionPago` se escribe con `MERGE` sobre `MpAuthorizedPaymentId` porque MP notifica la misma cuota más de una vez. |

## Un cobro que existe en MP y no en la base

1. Confirmá que la notificación llegó (punto 1) y con qué `Tipo`.
2. Si no llegó: `POST /api/suscripciones/{id}/sincronizar` recupera el estado y
   las cuotas históricas desde MercadoPago.
3. Si es un pago único: la rama `payment` descarta las cuotas de suscripción
   mirando `preapproval_id` y el prefijo del `external_reference`
   (`COT-` vs `PAGO-`). Sin ese filtro el mismo cobro se contaría dos veces.

Para reprocesar, poné `Procesado = 0` en la fila: el `BackgroundService` la toma
en el ciclo siguiente. El `PayloadJson` crudo está guardado, así que la
notificación se puede releer entera sin depender de MercadoPago.

## Al terminar

Si el hallazgo es de configuración (pestaña, clave, URL), anotalo en `ESTADO.md`:
son los errores que más se repiten y los que menos rastro dejan.
