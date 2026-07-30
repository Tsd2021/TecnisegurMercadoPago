/* ============================================================================
   Verificación del circuito de webhooks — consultas de diagnóstico

   Sólo lectura. Se puede ejecutar en cualquier momento, también en producción.
   Pensado para mirar mientras se corre la prueba end-to-end de
   src/TecnisegurMercadoPago.Api/PruebaDeploy.http
   ============================================================================ */

USE TSD;
GO

/* ----------------------------------------------------------------------------
   1) ¿Está llegando algo?

   Si esta consulta viene vacía después de adherirse, MercadoPago no está
   entregando: el problema es de configuración del webhook (URL o pestaña),
   no de la aplicación. Confirmarlo en el Panel de monitoreo de la aplicación
   en MP, que muestra los intentos de entrega.
   ---------------------------------------------------------------------------- */
SELECT TOP 20
    Id,
    FechaRecepcion,
    Tipo,
    Accion,
    DataId,
    FirmaValida,        -- 1 = la firma HMAC validó
    Procesado,          -- 1 = ProcesadorNotificaciones ya la trabajó
    IntentosProceso,
    ErrorProceso
FROM dbo.MercadoPagoNotificacion
ORDER BY Id DESC;
GO

/* ----------------------------------------------------------------------------
   2) Notificaciones con firma inválida

   Cualquier fila acá significa que el WebhookSecret del servidor NO coincide
   con el de la pestaña configurada en MercadoPago. Se guardan para auditoría
   pero ObtenerPendientesAsync las filtra: NUNCA se procesan.

   Arreglo: copiar la clave secreta de la pestaña correcta (Modo productivo si
   el token es APP_USR-...), actualizar MercadoPago__WebhookSecret en el
   web.config del servidor y reiniciar el Application Pool.
   ---------------------------------------------------------------------------- */
SELECT
    Id, FechaRecepcion, Tipo, DataId,
    LEFT(PayloadJson, 400) AS PayloadRecortado
FROM dbo.MercadoPagoNotificacion
WHERE FirmaValida = 0
ORDER BY Id DESC;
GO

/* ----------------------------------------------------------------------------
   3) Notificaciones que llegaron bien pero fallan al procesarse

   Firma válida y varios intentos = la aplicación no puede consultar MP o la
   base. Mirar ErrorProceso; suele ser un 403 de MercadoPago (suscripción de
   otra cuenta) o un problema de conexión a TSD.
   ---------------------------------------------------------------------------- */
SELECT
    Id, FechaRecepcion, Tipo, DataId, IntentosProceso, ErrorProceso
FROM dbo.MercadoPagoNotificacion
WHERE FirmaValida = 1
  AND Procesado = 0
  AND IntentosProceso > 0
ORDER BY IntentosProceso DESC, Id DESC;
GO

/* ----------------------------------------------------------------------------
   4) Estado de las suscripciones

   Lectura por la vista, no por las tablas (convención del proyecto).
   Tras adherirse, Estado debe pasar de 'pending' a 'authorized' solo,
   sin ejecutar /sincronizar. Si sólo cambia al sincronizar, el webhook
   no está funcionando.
   ---------------------------------------------------------------------------- */
SELECT
    IdSuscripcion, IdCotizacion, ExternalReference, PreapprovalId,
    NombreCliente, MontoMensual, Moneda,
    Estado, EstadoDescripcion,
    FechaCreacion, FechaAutorizacion, FechaProximoPago, FechaUltimoPago,
    CuotasCobradas, CuotasRechazadas, TotalCobrado,
    Origen, UsuarioCreacion
FROM dbo.vw_SuscripcionesEstado
ORDER BY IdSuscripcion DESC;
GO

/* ----------------------------------------------------------------------------
   5) Cuotas registradas

   La primera se cobra ~1 hora después de autorizada (con diasPrueba = 0).
   Estado     = processed | recycling | scheduled | cancelled  (la cuota)
   EstadoPago = approved  | rejected  | pending   | refunded   (el pago)
   ---------------------------------------------------------------------------- */
SELECT
    p.Id, p.IdSuscripcion, s.IdCotizacion,
    p.MpAuthorizedPaymentId, p.MpPaymentId,
    p.Monto, p.Moneda,
    p.Estado, p.EstadoPago, p.DetalleEstado,
    p.FechaProgramada, p.FechaPago, p.FechaRegistro
FROM dbo.SuscripcionPago AS p
JOIN dbo.SuscripcionCotizacion AS s ON s.Id = p.IdSuscripcion
ORDER BY p.Id DESC;
GO

/* ----------------------------------------------------------------------------
   6) Resumen rápido — una línea con el semáforo del circuito
   ---------------------------------------------------------------------------- */
SELECT
    (SELECT COUNT(*) FROM dbo.MercadoPagoNotificacion)                      AS NotificacionesTotales,
    (SELECT COUNT(*) FROM dbo.MercadoPagoNotificacion WHERE FirmaValida = 0) AS FirmaInvalida,
    (SELECT COUNT(*) FROM dbo.MercadoPagoNotificacion
      WHERE FirmaValida = 1 AND Procesado = 0)                              AS PendientesDeProceso,
    (SELECT COUNT(*) FROM dbo.SuscripcionCotizacion)                        AS Suscripciones,
    (SELECT COUNT(*) FROM dbo.SuscripcionPago)                              AS Cuotas;
GO
