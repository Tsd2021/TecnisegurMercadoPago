/* ============================================================================
   04 - Fecha de inicio de la suscripción (día de la adhesión)
   ============================================================================

   Agrega SuscripcionCotizacion.FechaInicio: la fecha desde la que MercadoPago
   empieza a cobrar la cuota, que viaja como auto_recurring.start_date en el
   POST /preapproval.

   Reemplaza a DiasPrueba como forma de posponer la primera cuota. Los dos
   hacen lo mismo y MercadoPago no documenta cómo se combinan, así que se manda
   uno o el otro, nunca los dos — la exclusión la impone SuscripcionServicio.
   DiasPrueba queda en la tabla porque las suscripciones viejas lo tienen
   cargado y la vista lo sigue mostrando.

   Además de fijar cuándo arranca el cobro, FechaInicio es la única forma de
   controlar en qué día del mes caen las cuotas: billing_day sólo existe en
   preapproval_plan, y esta integración usa preapproval sin plan asociado.

   Idempotente: se puede correr las veces que haga falta.
   ============================================================================ */

IF NOT EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.SuscripcionCotizacion')
      AND name      = 'FechaInicio'
)
BEGIN
    ALTER TABLE dbo.SuscripcionCotizacion
        ADD FechaInicio DATETIME NULL;

    PRINT 'Columna SuscripcionCotizacion.FechaInicio agregada.';
END
ELSE
BEGIN
    PRINT 'La columna SuscripcionCotizacion.FechaInicio ya existía.';
END
GO

/* ----------------------------------------------------------------------------
   Recrear la vista para que exponga FechaInicio.

   Es la misma definición de 01_CrearTablasSuscripciones.sql con la columna
   nueva. Se recrea entera —y no con ALTER puntual— porque una vista con
   SELECT explícito no se puede extender de otra forma.
   ---------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.vw_SuscripcionesEstado', 'V') IS NOT NULL
    DROP VIEW dbo.vw_SuscripcionesEstado;
GO

CREATE VIEW dbo.vw_SuscripcionesEstado
AS
SELECT
    s.Id                    AS IdSuscripcion,
    s.IdCotizacion,
    s.ExternalReference,
    s.PreapprovalId,
    s.NombreCliente,
    s.PayerEmail,
    s.MontoMensual,
    s.Moneda,
    s.DiasPrueba,
    s.FechaInicio,
    s.Estado,
    CASE s.Estado
        WHEN 'pending'    THEN 'Pendiente de autorización'
        WHEN 'authorized' THEN 'Activa'
        WHEN 'paused'     THEN 'Pausada'
        WHEN 'cancelled'  THEN 'Cancelada'
        ELSE s.Estado
    END                     AS EstadoDescripcion,
    s.InitPoint,
    s.FechaCreacion,
    s.FechaAutorizacion,
    s.FechaCancelacion,
    s.MotivoCancelacion,
    s.FechaProximoPago,
    s.FechaUltimoPago,
    s.Origen,
    s.UsuarioCreacion,

    /* Métricas de cobranza */
    ISNULL(p.CuotasCobradas, 0)   AS CuotasCobradas,
    ISNULL(p.CuotasRechazadas, 0) AS CuotasRechazadas,
    ISNULL(p.TotalCobrado, 0)     AS TotalCobrado
FROM dbo.SuscripcionCotizacion AS s
LEFT JOIN
(
    SELECT
        IdSuscripcion,
        SUM(CASE WHEN EstadoPago = 'approved' THEN 1 ELSE 0 END)      AS CuotasCobradas,
        SUM(CASE WHEN EstadoPago = 'rejected' THEN 1 ELSE 0 END)      AS CuotasRechazadas,
        SUM(CASE WHEN EstadoPago = 'approved' THEN Monto ELSE 0 END)  AS TotalCobrado
    FROM dbo.SuscripcionPago
    GROUP BY IdSuscripcion
) AS p
    ON p.IdSuscripcion = s.Id;
GO

PRINT 'Vista vw_SuscripcionesEstado recreada con FechaInicio.';
GO
