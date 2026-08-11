/* ============================================================================
   Forense de UNA suscripción — todo lo que la base sabe de ella

   Sólo lectura. No modifica nada; se puede ejecutar en producción.

   Complementa Herramientas\ForensePreapproval.ps1: el script consulta a
   MercadoPago, esto consulta lo que quedó de nuestro lado. Las dos mitades
   juntas arman la línea temporal.

   Poner el preapproval abajo y ejecutar el archivo entero.
   ============================================================================ */

USE TSD;
GO

DECLARE @Preapproval VARCHAR(64) = '8ef74c91585845b3b72e985de1ee0e7a';  -- <<< CAMBIAR


/* ----------------------------------------------------------------------------
   1) La fila local y el payload que se le mandó a MercadoPago

   PayloadEnvioJson es la única forma de reconstruir con qué start_date,
   end_date y back_url se creó. NULL ahí significa "se creó antes de esta
   instrumentación", nunca "se mandó vacío".
   ---------------------------------------------------------------------------- */
SELECT
    s.Id                AS IdSuscripcion,
    s.IdCotizacion,
    s.ExternalReference,
    s.PreapprovalId,
    s.Estado,
    s.MontoMensual,
    s.Moneda,
    s.DiasPrueba,
    s.FechaInicio,
    s.FechaCreacion,
    s.FechaAutorizacion,
    s.FechaCancelacion,
    s.MotivoCancelacion,          -- 'Cancelada en MercadoPago' = la baja la informó MP
    s.FechaProximoPago,
    s.FechaActualizacion,
    s.UsuarioCreacion,
    s.Origen,
    s.PayloadEnvioJson
FROM dbo.SuscripcionCotizacion AS s
WHERE s.PreapprovalId = @Preapproval;


/* ----------------------------------------------------------------------------
   2) Cuotas registradas

   Vacío = MercadoPago nunca notificó ninguna cuota para esta suscripción.
   Junto con authorized_payments/search vacío del lado de MP, eso responde
   "¿llegó a crearse el primer cobro?" con un no.
   ---------------------------------------------------------------------------- */
SELECT
    p.Id,
    p.MpAuthorizedPaymentId,
    p.MpPaymentId,
    p.Monto,
    p.Estado,
    p.EstadoPago,
    p.DetalleEstado,
    p.FechaProgramada,
    p.FechaPago,
    p.FechaRegistro
FROM dbo.SuscripcionPago AS p
    INNER JOIN dbo.SuscripcionCotizacion AS s ON s.Id = p.IdSuscripcion
WHERE s.PreapprovalId = @Preapproval
ORDER BY p.Id;


/* ----------------------------------------------------------------------------
   3) TODAS las notificaciones de esta suscripción, en orden

   Es la respuesta a "¿MercadoPago nos notificó pending → cancelled, o nos
   enteramos después?".

   Cómo leerla:

     - Una fila subscription_preapproval con FechaRecepcion cercana al momento
       en que el cliente cargó la tarjeta  => MercadoPago avisó del cambio.
     - Ninguna fila en esa ventana, y el estado cancelado apareciendo recién
       tras un /sincronizar manual  => nos enteramos nosotros, MP no avisó.

   OJO con dos columnas que NO existen:

     - x-request-id NO se persiste. El webhook guarda cuerpo, tipo, acción y
       data.id, pero no las cabeceras. Para el ticket de soporte hay que
       sacarlo del panel de monitoreo de la aplicación en MercadoPago.
     - El status HTTP con el que respondimos tampoco se persiste, pero se
       deduce sin ambigüedad: WebhookController devuelve 200 en todos los
       caminos salvo el fallo de persistencia, que devuelve 500 y NO deja
       fila. Por lo tanto: si la fila existe, respondimos 200.
   ---------------------------------------------------------------------------- */
SELECT
    n.Id,
    n.FechaRecepcion,
    n.Tipo,                 -- subscription_preapproval | subscription_authorized_payment | payment
    n.Accion,               -- created | updated
    n.DataId,
    n.FirmaValida,          -- 0 = nunca se procesó, aunque haya llegado
    n.Procesado,
    n.IntentosProceso,
    n.ErrorProceso,
    n.PayloadJson
FROM dbo.MercadoPagoNotificacion AS n
WHERE n.DataId = @Preapproval
ORDER BY n.Id;


/* ----------------------------------------------------------------------------
   4) Notificaciones de las cuotas de esta suscripción

   Las subscription_authorized_payment traen el id de la cuota como data.id, no
   el del preapproval, así que la consulta 3 no las ve. Se buscan por el
   preapproval_id que aparece dentro del payload.
   ---------------------------------------------------------------------------- */
SELECT
    n.Id,
    n.FechaRecepcion,
    n.Tipo,
    n.Accion,
    n.DataId,
    n.FirmaValida,
    n.Procesado,
    n.ErrorProceso,
    n.PayloadJson
FROM dbo.MercadoPagoNotificacion AS n
WHERE n.PayloadJson LIKE '%' + @Preapproval + '%'
  AND n.DataId <> @Preapproval
ORDER BY n.Id;


/* ----------------------------------------------------------------------------
   5) Ventana completa alrededor del alta

   Para ver qué más pasó en la cuenta mientras esta suscripción se creaba y se
   caía: notificaciones de otras suscripciones, pagos sueltos, firmas
   inválidas. Sirve para descartar que algo concurrente la haya afectado.
   ---------------------------------------------------------------------------- */
DECLARE @Desde DATETIME, @Hasta DATETIME;

SELECT @Desde = DATEADD(MINUTE, -10, s.FechaCreacion),
       @Hasta = DATEADD(HOUR,     4, s.FechaCreacion)
FROM dbo.SuscripcionCotizacion AS s
WHERE s.PreapprovalId = @Preapproval;

SELECT
    n.Id,
    n.FechaRecepcion,
    n.Tipo,
    n.Accion,
    n.DataId,
    n.FirmaValida,
    n.Procesado
FROM dbo.MercadoPagoNotificacion AS n
WHERE n.FechaRecepcion BETWEEN @Desde AND @Hasta
ORDER BY n.Id;
GO
