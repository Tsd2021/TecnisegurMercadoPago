/* ============================================================================
   10 - Estado de liberación informado por MercadoPago

   POR QUÉ
   -------
   El script 08 partía de este supuesto, escrito ahí textualmente:

       "Consultar el pago el día previsto tampoco lo confirma:
        GET /v1/payments/{id} devuelve el estado del PAGO, no el de nuestro
        saldo. Lo que se verifica es la ausencia de reversión, que no es lo
        mismo que la presencia del crédito."

   Es falso. El pago SÍ informa si el dinero se liberó, en money_release_status.

   Verificado el 31/07/2026 contra el cobro real 166657246137 ($20 con débito,
   aprobado el 06/07, liberado el 27/07):

       money_release_date   : 2026-07-27T14:46:39.000-04:00
       money_release_status : released

   Y esa fecha coincide al segundo con la columna DATE del reporte de
   Liberaciones de la cuenta, que es la fuente contable. O sea que la previsión
   no sólo se cumplió: MercadoPago además la confirma en la misma llamada que ya
   hacía ProcesadorNotificaciones.

   QUÉ CAMBIA
   ----------
   Hasta acá "Liberado s/MP" salía de comparar FechaLiberacion contra GETDATE().
   Eso no distingue dos situaciones muy distintas:

       - MercadoPago liberó el dinero;
       - pasó la fecha prevista y nadie sabe qué pasó.

   Con la columna nueva la primera se afirma y la segunda se sigue infiriendo,
   pero queda marcada como tal.

   EstadoLiberacion NO agrega valores nuevos: el módulo de cobranzas de TSD ya
   está construido contra los cuatro de 08 y romperlo no aporta nada. La
   precisión se expone aparte, en LiberacionConfirmada:

       1     MercadoPago informó 'released'
       0     MercadoPago informó 'pending'
       NULL  no se sabe — cobro viejo, sin reconsultar todavía

   Regla de lectura para la pantalla: "Liberado s/MP" con
   LiberacionConfirmada = 1 es un hecho informado; con NULL es una deducción
   del almanaque. Sigue sin ser un asiento contable — eso sólo lo da el reporte
   de Liberaciones — pero deja de ser una promesa sin verificar.

   Requiere 09_SepararComisionDeRetenciones.sql.
   Idempotente.
   ============================================================================ */

USE TSD;
GO

/* ----------------------------------------------------------------------------
   1) Columna nueva

      VARCHAR y no BIT: MercadoPago hoy devuelve 'pending' y 'released', pero
      guardar el texto tal cual evita tener que migrar la tabla si mañana suma
      un tercer valor. La traducción a booleano vive en la vista, que se recrea
      sin costo.
   ---------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.SuscripcionPago')
                 AND name      = 'EstadoLiberacionMp')
BEGIN
    ALTER TABLE dbo.SuscripcionPago ADD EstadoLiberacionMp VARCHAR(20) NULL;
    PRINT 'Columna SuscripcionPago.EstadoLiberacionMp agregada.';
END
ELSE
    PRINT 'SuscripcionPago.EstadoLiberacionMp ya existía. Sin cambios.';
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.PagoUnico')
                 AND name      = 'EstadoLiberacionMp')
BEGIN
    ALTER TABLE dbo.PagoUnico ADD EstadoLiberacionMp VARCHAR(20) NULL;
    PRINT 'Columna PagoUnico.EstadoLiberacionMp agregada.';
END
ELSE
    PRINT 'PagoUnico.EstadoLiberacionMp ya existía. Sin cambios.';
GO


/* ----------------------------------------------------------------------------
   2) Vistas de detalle
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
    p.EstadoLiberacionMp,
    p.Origen,
    p.UsuarioCreacion
FROM dbo.PagoUnico AS p;
GO

PRINT 'Vista vw_PagosUnicosEstado recreada con EstadoLiberacionMp.';
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
    p.EstadoLiberacionMp,
    p.FechaRegistro,
    p.FechaActualizacion
FROM dbo.SuscripcionPago AS p
INNER JOIN dbo.SuscripcionCotizacion AS s ON s.Id = p.IdSuscripcion;
GO

PRINT 'Vista vw_CuotasSuscripcion recreada con EstadoLiberacionMp.';
GO


/* ----------------------------------------------------------------------------
   3) Vista de consumo

      El orden del CASE de EstadoLiberacion importa: lo que informa MercadoPago
      va ANTES que la comparación de fechas. Un pago con money_release_status
      'pending' y fecha vencida es 'A liberar', no 'Liberado' — MercadoPago
      todavía lo tiene retenido, y creerle al almanaque en ese caso sería
      exactamente el error que este script viene a corregir.
   ---------------------------------------------------------------------------- */
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
    c.EstadoLiberacionMp,
    c.FechaActualizacion,
    c.MpPaymentId,

    CAST(CASE WHEN c.EstadoPago = 'approved' THEN 1 ELSE 0 END AS BIT) AS Cobrado,

    CAST(CASE WHEN c.EstadoPago IN ('refunded', 'charged_back')
              THEN 1 ELSE 0 END AS BIT) AS Revertido,

    /* Se mantiene por compatibilidad con lo ya construido en TSD.
       EstadoLiberacion es más preciso: preferirlo en pantallas nuevas. */
    CAST(CASE WHEN c.FechaLiberacion IS NULL      THEN NULL
              WHEN c.FechaLiberacion <= GETDATE() THEN 1
              ELSE 0 END AS BIT) AS Disponible,

    /* 1 informado, 0 informado como pendiente, NULL sin reconsultar.

       La reversión va primero: un cobro devuelto o con contracargo no cuenta
       como liberado por más que money_release_status siga diciendo 'released'.
       Ese valor quedó de antes del contracargo y leerlo solo daría por confirmada
       plata que volvió. */
    CAST(CASE WHEN c.EstadoPago IN ('refunded', 'charged_back') THEN 0
              WHEN c.EstadoLiberacionMp = 'released'            THEN 1
              WHEN c.EstadoLiberacionMp IS NULL                 THEN NULL
              ELSE 0 END AS BIT) AS LiberacionConfirmada,

    CASE
        WHEN c.EstadoPago IN ('refunded', 'charged_back') THEN 'Revertido'
        WHEN c.EstadoPago <> 'approved'                   THEN NULL
        WHEN c.EstadoLiberacionMp = 'released'            THEN 'Liberado s/MP'
        WHEN c.EstadoLiberacionMp IS NOT NULL             THEN 'A liberar'
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
    u.EstadoLiberacionMp,
    p.FechaActualizacion,
    u.MpPaymentId,

    CAST(CASE WHEN u.Estado = 'approved' THEN 1 ELSE 0 END AS BIT) AS Cobrado,

    CAST(CASE WHEN u.Estado IN ('refunded', 'charged_back')
              THEN 1 ELSE 0 END AS BIT) AS Revertido,

    CAST(CASE WHEN u.FechaLiberacion IS NULL      THEN NULL
              WHEN u.FechaLiberacion <= GETDATE() THEN 1
              ELSE 0 END AS BIT) AS Disponible,

    CAST(CASE WHEN u.Estado IN ('refunded', 'charged_back') THEN 0
              WHEN u.EstadoLiberacionMp = 'released'        THEN 1
              WHEN u.EstadoLiberacionMp IS NULL             THEN NULL
              ELSE 0 END AS BIT) AS LiberacionConfirmada,

    CASE
        WHEN u.Estado IN ('refunded', 'charged_back') THEN 'Revertido'
        WHEN u.Estado <> 'approved'                   THEN NULL
        WHEN u.EstadoLiberacionMp = 'released'        THEN 'Liberado s/MP'
        WHEN u.EstadoLiberacionMp IS NOT NULL         THEN 'A liberar'
        WHEN u.FechaLiberacion IS NULL                THEN 'Sin datos'
        WHEN u.FechaLiberacion > GETDATE()            THEN 'A liberar'
        ELSE 'Liberado s/MP'
    END AS EstadoLiberacion

FROM dbo.vw_PagosUnicosEstado AS u
INNER JOIN dbo.PagoUnico AS p ON p.Id = u.IdPago;
GO

PRINT 'Vista vw_CobranzasMercadoPago recreada con LiberacionConfirmada.';
GO


/* ----------------------------------------------------------------------------
   Control: cobros aprobados cuya liberación todavía no fue confirmada por
   MercadoPago. Los que tengan LiberacionConfirmada NULL y fecha vencida son los
   que se están mostrando como liberados por deducción y no por dato.

   Con la API al día esta consulta se vacía sola: el repaso diario de
   ProcesadorNotificaciones completa la columna.
   ---------------------------------------------------------------------------- */
PRINT '';
PRINT 'Cobros liberados por deducción (sin confirmación de MercadoPago):';
GO

SELECT Tipo, IdCotizacion, NombreCliente, Monto, MontoNeto,
       FechaPago, FechaLiberacion, EstadoLiberacion, FechaActualizacion
FROM dbo.vw_CobranzasMercadoPago
WHERE Cobrado = 1
  AND LiberacionConfirmada IS NULL
ORDER BY FechaLiberacion;
GO
