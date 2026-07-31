/* ============================================================================
   06 - Vistas de consumo del módulo TSD Desktop (IT -> Alarmas -> Cobranzas)

   IMPORTANTE: este script debe ser ejecutado manualmente contra la base TSD.
   El desktop lee por SQL y escribe por la API intermedia.

   Requiere 05_AgregarLiberacionYNeto.sql ejecutado previamente: estas vistas
   exponen MontoNeto, Comision y FechaLiberacion, que ese script agrega.

   (Antes se llamaba 05_VistasModuloTSD.sql. Se renumeró al insertar el script
   de liberación y neto, que tiene que correr primero.)
   ============================================================================ */

USE TSD;
GO

/* ----------------------------------------------------------------------------
   1) vw_CuotasSuscripcion
      Cada cuota con los datos del cliente, para el detalle de la grilla.
      SuscripcionPago sola no sirve: obliga a la UI a hacer el join.
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
        WHEN 'approved' THEN 'Aprobado'
        WHEN 'rejected' THEN 'Rechazado'
        WHEN 'pending'  THEN 'Pendiente'
        WHEN 'refunded' THEN 'Devuelto'
        ELSE p.EstadoPago
    END AS EstadoPagoDescripcion,
    p.DetalleEstado,
    p.FechaProgramada,
    p.FechaPago,
    p.FechaLiberacion,
    p.FechaRegistro
FROM dbo.SuscripcionPago AS p
INNER JOIN dbo.SuscripcionCotizacion AS s ON s.Id = p.IdSuscripcion;
GO

PRINT 'Vista vw_CuotasSuscripcion creada.';
GO


/* ----------------------------------------------------------------------------
   2) vw_CobranzasMercadoPago
      Cuotas y pagos únicos en una sola lista.

      Dos columnas calculadas que la UI no debe recalcular por su cuenta:

      Cobrado    = se le cobró al cliente. Lo define EstadoPago = 'approved'
                   (cuotas) o Estado = 'approved' (pagos únicos), NO el Estado
                   de la cuota: una cuota 'processed' pudo haber sido rechazada.

      Disponible = la plata está en la cuenta de Tecnisegur. Con liberación a 21
                   días, un cobro puede estar Cobrado = 1 y Disponible = 0
                   durante tres semanas. Son cosas distintas y confundirlas es
                   equivocarse por 21 días de facturación.

      Disponible es NULL —y no 0— cuando no hay FechaLiberacion: "no liberado" y
      "no sabemos" no son lo mismo.
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
    c.Moneda,
    ISNULL(c.EstadoPago, c.Estado) AS Estado,
    ISNULL(c.EstadoPagoDescripcion, c.EstadoDescripcion) AS EstadoDescripcion,
    c.DetalleEstado,
    c.FechaProgramada AS FechaVencimiento,
    c.FechaPago,
    c.FechaLiberacion,
    c.MpPaymentId,
    CAST(CASE WHEN c.EstadoPago = 'approved' THEN 1 ELSE 0 END AS BIT) AS Cobrado,
    CAST(CASE WHEN c.FechaLiberacion IS NULL          THEN NULL
              WHEN c.FechaLiberacion <= GETDATE()     THEN 1
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
    u.MpPaymentId,
    CAST(CASE WHEN u.Estado = 'approved' THEN 1 ELSE 0 END AS BIT) AS Cobrado,
    CAST(CASE WHEN u.FechaLiberacion IS NULL          THEN NULL
              WHEN u.FechaLiberacion <= GETDATE()     THEN 1
              ELSE 0 END AS BIT) AS Disponible
FROM dbo.vw_PagosUnicosEstado AS u;
GO

PRINT 'Vista vw_CobranzasMercadoPago creada.';
GO


/* ----------------------------------------------------------------------------
   3) vw_ClientesCobranzaMP
      Una fila por cotización con cobros MercadoPago, con los datos de contacto
      del contrato. Es la pestaña "Clientes".

      OUTER APPLY con TOP 1 y no un JOIN: una cotización puede acumular varias
      suscripciones a lo largo del tiempo (la vieja cancelada, la nueva viva) y
      un JOIN duplicaría la fila del cliente. El ORDER BY prioriza la viva.

      TotalCobrado es BRUTO (lo que se le cobró al cliente); TotalNeto es lo que
      entró después de la comisión. Los dos, porque responden preguntas
      distintas: cuánto facturó ese cliente y cuánto dejó.

      OJO: el nombre CotizacionesComericales lleva el typo tal como está en la
      base. No corregirlo.
   ---------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.vw_ClientesCobranzaMP', 'V') IS NOT NULL
    DROP VIEW dbo.vw_ClientesCobranzaMP;
GO

CREATE VIEW dbo.vw_ClientesCobranzaMP
AS
SELECT
    c.Id AS IdCotizacion,
    c.Fecha AS FechaCotizacion,
    c.Cliente,
    con.Documento,
    con.Correo,
    con.TelefonoContacto,
    con.Departamento,
    con.PlazoContrato,
    c.TotalServiciosMensual,
    c.TotalProductos,
    c.SumarProductosMensual,
    sus.IdSuscripcion,
    sus.Estado AS EstadoSuscripcion,
    ISNULL(sus.EstadoDescripcion, 'Sin suscripción') AS EstadoSuscripcionDescripcion,
    sus.MontoMensual,
    sus.FechaProximoPago,
    sus.FechaUltimoPago,
    ISNULL(sus.CuotasCobradas, 0) AS CuotasCobradas,
    ISNULL(sus.CuotasRechazadas, 0) AS CuotasRechazadas,
    ISNULL(sus.TotalCobrado, 0) AS TotalCobrado,
    sus.Origen,
    ISNULL(pu.PagosUnicos, 0) AS PagosUnicos,
    ISNULL(pu.TotalPagosUnicos, 0) AS TotalPagosUnicos,

    /* Neto acumulado: cuotas + pagos únicos, ya descontada la comisión.
       Puede quedar por debajo de TotalCobrado + TotalPagosUnicos mientras
       MercadoPago no haya informado el neto de algún cobro. */
    ISNULL(net.TotalNeto, 0) AS TotalNeto,
    ISNULL(net.TotalComision, 0) AS TotalComision,
    ISNULL(net.TotalALiberar, 0) AS TotalALiberar
FROM dbo.CotizacionesComericales AS c
OUTER APPLY
(
    SELECT TOP 1 x.Documento, x.Correo, x.TelefonoContacto,
                 x.Departamento, x.PlazoContrato
    FROM dbo.ContratoCotizacionAlarma AS x
    WHERE x.IdCotizacion = c.Id
    ORDER BY x.Id DESC
) AS con
OUTER APPLY
(
    SELECT TOP 1 v.IdSuscripcion, v.Estado, v.EstadoDescripcion, v.MontoMensual,
                 v.FechaProximoPago, v.FechaUltimoPago, v.CuotasCobradas,
                 v.CuotasRechazadas, v.TotalCobrado, v.Origen
    FROM dbo.vw_SuscripcionesEstado AS v
    WHERE v.IdCotizacion = c.Id
    ORDER BY
        CASE WHEN v.Estado IN ('pending', 'authorized', 'paused') THEN 0 ELSE 1 END,
        v.FechaCreacion DESC
) AS sus
OUTER APPLY
(
    SELECT COUNT(*) AS PagosUnicos,
           SUM(CASE WHEN u.Estado = 'approved' THEN u.Monto ELSE 0 END) AS TotalPagosUnicos
    FROM dbo.PagoUnico AS u
    WHERE u.IdCotizacion = c.Id
) AS pu
OUTER APPLY
(
    SELECT SUM(b.MontoNeto) AS TotalNeto,
           SUM(b.Comision)  AS TotalComision,
           SUM(CASE WHEN b.Disponible = 0 THEN b.MontoNeto ELSE 0 END) AS TotalALiberar
    FROM dbo.vw_CobranzasMercadoPago AS b
    WHERE b.IdCotizacion = c.Id
      AND b.Cobrado = 1
) AS net
WHERE sus.IdSuscripcion IS NOT NULL
   OR pu.PagosUnicos > 0;
GO

PRINT 'Vista vw_ClientesCobranzaMP creada.';
GO
