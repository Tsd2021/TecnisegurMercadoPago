/* ============================================================================
   07 - Fecha de última consulta de una cuota

   Agrega SuscripcionPago.FechaActualizacion: cuándo se verificó por última vez
   esa cuota contra MercadoPago.

   POR QUÉ HACE FALTA
   ------------------
   MercadoPago informa money_release_date al aprobar el cobro y NUNCA vuelve a
   avisar si el dinero efectivamente se liberó: no existe webhook de liberación
   (verificado contra la lista oficial de tópicos el 31/07/2026). Si en el medio
   hay un contracargo o una devolución, la fecha guardada queda mintiendo y nadie
   se entera.

   Por eso ProcesadorNotificaciones repasa una vez por día las cuotas cuya
   liberación no se puede dar por cumplida. Esta columna es lo que le permite
   ordenar por "hace más que no se mira" y, sobre todo, lo que le permite a la
   interfaz decir "disponible, verificado hace 6 horas" en vez de "disponible" a
   secas. Sin ella no hay forma de saber si el dato es de hoy o de hace tres
   meses.

   PagoUnico ya tiene FechaActualizacion desde 03_CrearTablaPagoUnico.sql.

   Requiere 05_AgregarLiberacionYNeto.sql y 06_VistasModuloTSD.sql.
   Idempotente: se puede correr las veces que haga falta.
   ============================================================================ */

USE TSD;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.SuscripcionPago')
                 AND name      = 'FechaActualizacion')
BEGIN
    ALTER TABLE dbo.SuscripcionPago ADD FechaActualizacion DATETIME NULL;

    PRINT 'Columna SuscripcionPago.FechaActualizacion agregada.';
END
ELSE
    PRINT 'SuscripcionPago.FechaActualizacion ya existía. Sin cambios.';
GO

/* Índice de apoyo del repaso diario: ordena por la cuota menos verificada. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SuscripcionPago_Repaso')
BEGIN
    CREATE NONCLUSTERED INDEX IX_SuscripcionPago_Repaso
        ON dbo.SuscripcionPago (EstadoPago, FechaActualizacion)
        INCLUDE (MpPaymentId, FechaLiberacion, MontoNeto);

    PRINT 'Índice IX_SuscripcionPago_Repaso creado.';
END
GO


/* ----------------------------------------------------------------------------
   Recrear las dos vistas del módulo para exponer FechaActualizacion.

   Es la misma definición de 06_VistasModuloTSD.sql más una columna en cada una.
   vw_ClientesCobranzaMP no cambia.
   ---------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.vw_CuotasSuscripcion', 'V') IS NOT NULL
    DROP VIEW dbo.vw_CuotasSuscripcion;
GO

CREATE VIEW dbo.vw_CuotasSuscripcion
AS
SELECT
    p.Id,
    p.IdSuscripcion,
    s.IdCotizacion,
    s.NombreCliente,
    s.PayerEmail,
    p.MpAuthorizedPaymentId,
    p.MpPaymentId,
    p.Monto,
    p.MontoNeto,
    p.Comision,
    p.Moneda,
    p.Estado,
    CASE p.Estado
        WHEN 'scheduled' THEN 'Programada'
        WHEN 'processed' THEN 'Procesada'
        WHEN 'recycling' THEN 'En reintento'
        WHEN 'cancelled' THEN 'Cancelada'
        ELSE p.Estado
    END AS EstadoDescripcion,
    p.EstadoPago,
    CASE p.EstadoPago
        WHEN 'approved'     THEN 'Aprobado'
        WHEN 'rejected'     THEN 'Rechazado'
        WHEN 'pending'      THEN 'Pendiente'
        WHEN 'refunded'     THEN 'Devuelto'
        WHEN 'charged_back' THEN 'Contracargo'
        ELSE p.EstadoPago
    END AS EstadoPagoDescripcion,
    p.DetalleEstado,
    p.FechaProgramada,
    p.FechaPago,
    p.FechaLiberacion,
    p.FechaRegistro,
    p.FechaActualizacion
FROM dbo.SuscripcionPago AS p
INNER JOIN dbo.SuscripcionCotizacion AS s ON s.Id = p.IdSuscripcion;
GO

PRINT 'Vista vw_CuotasSuscripcion recreada con FechaActualizacion.';
GO

IF OBJECT_ID('dbo.vw_CobranzasMercadoPago', 'V') IS NOT NULL
    DROP VIEW dbo.vw_CobranzasMercadoPago;
GO

CREATE VIEW dbo.vw_CobranzasMercadoPago
AS
SELECT
    'Cuota mensual' AS Tipo,
    c.IdSuscripcion AS IdOrigen,
    c.IdCotizacion,
    c.NombreCliente,
    CAST('Cuota mensual de monitoreo' AS VARCHAR(255)) AS Concepto,
    c.Monto,
    c.MontoNeto,
    c.Comision,
    c.Moneda,
    ISNULL(c.EstadoPago, c.Estado) AS Estado,
    ISNULL(c.EstadoPagoDescripcion, c.EstadoDescripcion) AS EstadoDescripcion,
    c.DetalleEstado,
    c.FechaProgramada AS FechaVencimiento,
    c.FechaPago,
    c.FechaLiberacion,
    c.FechaActualizacion,
    c.MpPaymentId,
    CAST(CASE WHEN c.EstadoPago = 'approved' THEN 1 ELSE 0 END AS BIT) AS Cobrado,
    CAST(CASE WHEN c.FechaLiberacion IS NULL      THEN NULL
              WHEN c.FechaLiberacion <= GETDATE() THEN 1
              ELSE 0 END AS BIT) AS Disponible
FROM dbo.vw_CuotasSuscripcion AS c

UNION ALL

SELECT
    'Pago único' AS Tipo,
    u.IdPago AS IdOrigen,
    u.IdCotizacion,
    u.NombreCliente,
    u.Concepto,
    u.Monto,
    u.MontoNeto,
    u.Comision,
    u.Moneda,
    u.Estado,
    u.EstadoDescripcion,
    u.EstadoDetalle AS DetalleEstado,
    u.FechaCreacion AS FechaVencimiento,
    u.FechaPago,
    u.FechaLiberacion,
    p.FechaActualizacion,
    u.MpPaymentId,
    CAST(CASE WHEN u.Estado = 'approved' THEN 1 ELSE 0 END AS BIT) AS Cobrado,
    CAST(CASE WHEN u.FechaLiberacion IS NULL      THEN NULL
              WHEN u.FechaLiberacion <= GETDATE() THEN 1
              ELSE 0 END AS BIT) AS Disponible
FROM dbo.vw_PagosUnicosEstado AS u
/* FechaActualizacion no está en vw_PagosUnicosEstado; se toma de la tabla. */
INNER JOIN dbo.PagoUnico AS p ON p.Id = u.IdPago;
GO

PRINT 'Vista vw_CobranzasMercadoPago recreada con FechaActualizacion.';
GO
