/* ============================================================================
   15 - Nota de proceso: por qué una notificación no cambió nada

   Agrega MercadoPagoNotificacion.NotaProceso.

   POR QUÉ HACE FALTA
   ------------------
   Una notificación de un preapproval que no tiene fila local se marca hoy
   `Procesado = 1` con `ErrorProceso` en NULL, exactamente igual que una que
   actualizó una suscripción real. El procesador loguea un warning y retorna
   normal, así que en la base las dos son indistinguibles.

   No es hipotético: entre el 30/07 y el 11/08 nueve preapproval pasaron por ahí
   sin dejar rastro en la base. Reconstruir qué había pasado con uno de ellos
   —la suscripción 8ef74c91, cuya fila alguien borró a mano— costó tres
   consultas y terminó necesitando el stdout del servidor, que es rotativo y
   puede no existir cuando se lo busca.

   `ErrorProceso` no servía para esto: no es un error. La notificación se
   procesó bien; lo que hay que registrar es que no había a quién aplicarla.

   NULL significa "procesada normalmente" o "creada antes de este script",
   nunca "sin nota porque no se sabe".

   Requiere 01_CrearTablasSuscripciones.sql.
   Idempotente: se puede correr las veces que haga falta.
   ============================================================================ */

USE TSD;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.MercadoPagoNotificacion')
                 AND name      = 'NotaProceso')
BEGIN
    ALTER TABLE dbo.MercadoPagoNotificacion ADD NotaProceso NVARCHAR(400) NULL;

    PRINT 'Columna MercadoPagoNotificacion.NotaProceso agregada.';
END
ELSE
    PRINT 'MercadoPagoNotificacion.NotaProceso ya existía. Sin cambios.';
GO


/* ----------------------------------------------------------------------------
   Consulta de higiene: notificaciones que se procesaron sin efecto

   Las que traen nota son las que no encontraron a quién actualizar. Un
   external_reference con prefijo COT- acá significa que MercadoPago notificó
   una suscripción nuestra de la que no queda fila: o se borró, o el alta nunca
   se persistió. Las DIAG- son del script de diagnóstico y se esperan.
   ---------------------------------------------------------------------------- */
/*
SELECT
    n.Id,
    n.Tipo,
    n.Accion,
    n.DataId,
    n.NotaProceso,
    n.FechaRecepcion,
    n.FechaProceso
FROM dbo.MercadoPagoNotificacion AS n
WHERE n.NotaProceso IS NOT NULL
ORDER BY n.FechaRecepcion DESC;
*/
