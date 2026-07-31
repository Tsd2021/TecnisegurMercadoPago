/* ============================================================================
   08 - Estado de liberación explícito

   POR QUÉ
   -------
   Hasta acá la vista devolvía Disponible como BIT: 1 = la plata está, 0 = falta,
   NULL = no se sabe. Ese modelo esconde dos cosas.

   La primera: "Disponible = 1" NO es una confirmación de que el dinero entró.
   MercadoPago informa money_release_date al aprobar el cobro y nunca vuelve a
   avisar; no existe webhook de liberación. Consultar el pago el día previsto
   tampoco lo confirma: GET /v1/payments/{id} devuelve el estado del PAGO, no el
   de nuestro saldo. Lo que se verifica es la ausencia de reversión, que no es lo
   mismo que la presencia del crédito. La única fuente que lo confirma es el
   reporte de Liberaciones, que todavía no se consume.

   La segunda: un cobro revertido (devolución o contracargo) desaparecía de la
   pantalla. Al pasar EstadoPago a 'refunded' o 'charged_back', Cobrado cae a 0 y
   la fila se iba de "Pagos realizados" sin dejar rastro. Es plata que se creía
   cobrada y volvió: es lo último que debería desaparecer en silencio.

   QUÉ AGREGA
   ----------
   EstadoLiberacion  texto, uno de:
       'Revertido'      devolución o contracargo — la plata no va a llegar
       'Liberado s/MP'  fecha vencida y sin reversión a la última verificación.
                        "s/MP" = según MercadoPago. Es una inferencia sólida,
                        no un asiento contable.
       'A liberar'      fecha futura
       'Sin datos'      MercadoPago no informó la fecha todavía
       NULL             no corresponde (el cobro no se concretó)

   Revertido         BIT, para contar y alertar sin parsear texto.

   Requiere 07_AgregarFechaActualizacionCuota.sql.
   Idempotente.
   ============================================================================ */

USE TSD;
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

    CAST(CASE WHEN c.EstadoPago IN ('refunded', 'charged_back')
              THEN 1 ELSE 0 END AS BIT) AS Revertido,

    /* Se mantiene por compatibilidad con lo ya construido en TSD.
       EstadoLiberacion es más preciso: preferirlo en pantallas nuevas. */
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

PRINT 'Vista vw_CobranzasMercadoPago recreada con EstadoLiberacion y Revertido.';
GO


/* ----------------------------------------------------------------------------
   Consulta de control para el panel de alertas del Resumen: cobros que se
   revirtieron en los últimos 30 días. Es plata que se dio por cobrada y volvió.
   ---------------------------------------------------------------------------- */
PRINT '';
PRINT 'Cobros revertidos en los últimos 30 días:';
GO

SELECT Tipo, IdCotizacion, NombreCliente, Monto, Estado,
       EstadoDescripcion, FechaPago, FechaActualizacion
FROM dbo.vw_CobranzasMercadoPago
WHERE Revertido = 1
  AND FechaPago >= DATEADD(day, -30, GETDATE())
ORDER BY FechaPago DESC;
GO
