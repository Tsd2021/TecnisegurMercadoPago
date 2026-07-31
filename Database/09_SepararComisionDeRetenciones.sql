/* ============================================================================
   09 - Separar la comisión de MercadoPago de las retenciones impositivas

   POR QUÉ
   -------
   Hasta acá Comision guardaba (bruto − neto), que NO es la comisión: es la
   comisión más las retenciones impositivas.

   Verificado contra un cobro real de la cuenta de producción el 31/07/2026
   (pago 166657246137, $20 con tarjeta de débito). El desglose que devuelve
   MercadoPago en charges_details:

       mercadopago_fee              rate 6,09 %    $1,22
       tax_withholding-uruguay      rate 5 %       $0,98
       tax_withholding-lif_debito   rate 2 %       $0,33
       ---------------------------------------------------
       bruto $20,00  −  $2,53  =  neto acreditado $17,47

   O sea: el descuento real fue 12,65 %, no 6,09 %. Guardar todo junto bajo
   "Comision" sobreestima el costo de MercadoPago en más del doble.

   Y hay una razón de fondo, no sólo de prolijidad: la comisión es un costo
   perdido, mientras que las retenciones son adelantos de impuestos que la
   empresa acredita contra DGI. Sumarlas en una sola columna hace que el módulo
   de cobranzas informe como gasto algo que en buena parte es recuperable.

   La retención lif_debito aparece sólo en pagos con débito (Ley de Inclusión
   Financiera), así que el porcentaje total varía según el medio de pago. Por eso
   se guarda lo que informa MercadoPago por cobro y no se calcula con una tasa
   fija.

   MontoNeto no cambia: net_received_amount ya venía siendo lo efectivamente
   acreditado, y coincide exacto con NET_CREDIT_AMOUNT del reporte de
   Liberaciones. Eso quedó verificado.

   Requiere 05_AgregarLiberacionYNeto.sql.
   Idempotente.
   ============================================================================ */

USE TSD;
GO

/* ----------------------------------------------------------------------------
   1) Columna nueva
   ---------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.SuscripcionPago')
                 AND name      = 'Retenciones')
BEGIN
    ALTER TABLE dbo.SuscripcionPago ADD Retenciones DECIMAL(18,2) NULL;
    PRINT 'Columna SuscripcionPago.Retenciones agregada.';
END
ELSE
    PRINT 'SuscripcionPago.Retenciones ya existía. Sin cambios.';
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.PagoUnico')
                 AND name      = 'Retenciones')
BEGIN
    ALTER TABLE dbo.PagoUnico ADD Retenciones DECIMAL(18,2) NULL;
    PRINT 'Columna PagoUnico.Retenciones agregada.';
END
ELSE
    PRINT 'PagoUnico.Retenciones ya existía. Sin cambios.';
GO


/* ----------------------------------------------------------------------------
   2) Limpiar el dato viejo mal clasificado.

      Las filas cargadas antes de este cambio tienen en Comision la suma de
      comisión + retenciones. No hay forma de separarlas retroactivamente sin
      volver a consultar el pago, así que se anulan las dos columnas y el repaso
      diario de ProcesadorNotificaciones las vuelve a completar bien.

      Preferir NULL sobre un número que se sabe mal clasificado: NULL se muestra
      como "—" y no engaña a nadie.

      Sólo toca las filas de datos de prueba o cargadas antes de hoy; las nuevas
      ya vienen con el desglose correcto.
   ---------------------------------------------------------------------------- */
UPDATE dbo.SuscripcionPago
SET Comision = NULL, Retenciones = NULL, FechaActualizacion = NULL
WHERE Comision IS NOT NULL
  AND Retenciones IS NULL;

PRINT CAST(@@ROWCOUNT AS VARCHAR(10)) + ' cuota(s) marcada(s) para recalcular.';
GO

UPDATE dbo.PagoUnico
SET Comision = NULL, Retenciones = NULL
WHERE Comision IS NOT NULL
  AND Retenciones IS NULL;

PRINT CAST(@@ROWCOUNT AS VARCHAR(10)) + ' pago(s) único(s) marcado(s) para recalcular.';
GO


/* ----------------------------------------------------------------------------
   3) Recrear las vistas afectadas.
      Se mantienen las definiciones de 07 y 08 más la columna nueva.
   ---------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.vw_PagosUnicosEstado', 'V') IS NOT NULL
    DROP VIEW dbo.vw_PagosUnicosEstado;
GO

CREATE VIEW dbo.vw_PagosUnicosEstado
AS
SELECT
    p.Id                AS IdPago,
    p.IdCotizacion,
    p.ExternalReference,
    p.MpPreferenceId,
    p.MpPaymentId,
    p.InitPoint,
    p.Concepto,
    p.NombreCliente,
    p.PayerEmail,
    p.Monto,
    p.MontoNeto,
    p.Comision,
    p.Retenciones,
    p.Moneda,
    p.Estado,
    CASE p.Estado
        WHEN 'pendiente'  THEN 'Pendiente de pago'
        WHEN 'approved'   THEN 'Pagado'
        WHEN 'in_process' THEN 'En revisión'
        WHEN 'rejected'   THEN 'Rechazado'
        WHEN 'cancelled'  THEN 'Cancelado'
        WHEN 'refunded'   THEN 'Devuelto'
        ELSE p.Estado
    END                 AS EstadoDescripcion,
    p.EstadoDetalle,
    p.FechaCreacion,
    p.FechaPago,
    p.FechaLiberacion,
    p.Origen,
    p.UsuarioCreacion
FROM dbo.PagoUnico AS p;
GO

PRINT 'Vista vw_PagosUnicosEstado recreada con Retenciones.';
GO

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
    p.Retenciones,
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

PRINT 'Vista vw_CuotasSuscripcion recreada con Retenciones.';
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
    c.Retenciones,
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
    CAST(CASE WHEN c.EstadoPago IN ('refunded', 'charged_back')
              THEN 1 ELSE 0 END AS BIT) AS Revertido,
    CAST(CASE WHEN c.FechaLiberacion IS NULL      THEN NULL
              WHEN c.FechaLiberacion <= GETDATE() THEN 1
              ELSE 0 END AS BIT) AS Disponible,
    CASE
        WHEN c.EstadoPago IN ('refunded', 'charged_back') THEN 'Revertido'
        WHEN c.EstadoPago <> 'approved'                   THEN NULL
        WHEN c.FechaLiberacion IS NULL                    THEN 'Sin datos'
        WHEN c.FechaLiberacion > GETDATE()                THEN 'A liberar'
        ELSE 'Liberado s/MP'
    END AS EstadoLiberacion
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
    u.Retenciones,
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
    CAST(CASE WHEN u.Estado IN ('refunded', 'charged_back')
              THEN 1 ELSE 0 END AS BIT) AS Revertido,
    CAST(CASE WHEN u.FechaLiberacion IS NULL      THEN NULL
              WHEN u.FechaLiberacion <= GETDATE() THEN 1
              ELSE 0 END AS BIT) AS Disponible,
    CASE
        WHEN u.Estado IN ('refunded', 'charged_back') THEN 'Revertido'
        WHEN u.Estado <> 'approved'                   THEN NULL
        WHEN u.FechaLiberacion IS NULL                THEN 'Sin datos'
        WHEN u.FechaLiberacion > GETDATE()            THEN 'A liberar'
        ELSE 'Liberado s/MP'
    END AS EstadoLiberacion
FROM dbo.vw_PagosUnicosEstado AS u
INNER JOIN dbo.PagoUnico AS p ON p.Id = u.IdPago;
GO

PRINT 'Vista vw_CobranzasMercadoPago recreada con Retenciones.';
GO
