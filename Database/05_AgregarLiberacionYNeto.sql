/* ============================================================================
   05 - Liberación del dinero y monto neto

   Hoy SuscripcionPago.Monto y PagoUnico.Monto guardan lo que paga el CLIENTE, y
   FechaPago cuándo se le cobró. Ninguna de las dos cosas es lo que le entra a
   Tecnisegur.

   Verificado en el panel de la cuenta el 31/07/2026 (TRANSPORTADORA DE VALORES
   TECNISEGUR URUGUAY S.A.), sección "Comisiones y cuotas":

       Plazo de liberación : 21 días (todos los medios de pago)
       Comisión            : 4,99% + IVA  =  6,09% efectivo sobre el bruto

   O sea que el dinero llega tres semanas más tarde y un 6% más chico. Sobre la
   cuota de referencia: el cliente paga $7.770, MercadoPago retiene $473 y a
   Tecnisegur le entran $7.297, veintiún días después.

   Sin estas columnas el módulo de cobranzas de TSD no puede conciliar contra el
   extracto bancario ni por importe ni por fecha.

   Los tres valores los devuelve MercadoPago por cobro; no se calculan acá.
   Guardar el valor de MP y no un "FechaPago + 21" es deliberado: el plazo se
   puede cambiar en el panel en cualquier momento, y un pago retenido por disputa
   se libera cuando MercadoPago decide, no cuando la configuración dice.

   Requiere 01_CrearTablasSuscripciones.sql y 03_CrearTablaPagoUnico.sql.
   Ejecutar ANTES de 06_VistasModuloTSD.sql, que consume estas columnas.

   Script idempotente: se puede ejecutar más de una vez sin romper nada.
   ============================================================================ */

USE TSD;
GO

/* ----------------------------------------------------------------------------
   1) Columnas nuevas en SuscripcionPago (cuotas mensuales)
   ---------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.SuscripcionPago')
                 AND name      = 'FechaLiberacion')
BEGIN
    ALTER TABLE dbo.SuscripcionPago ADD
        FechaLiberacion DATETIME      NULL,   -- money_release_date
        MontoNeto       DECIMAL(18,2) NULL,   -- transaction_details.net_received_amount
        Comision        DECIMAL(18,2) NULL;   -- Monto - MontoNeto (comisión con su IVA)

    PRINT 'Columnas de liberación agregadas a SuscripcionPago.';
END
ELSE
    PRINT 'SuscripcionPago ya tenía las columnas de liberación. Sin cambios.';
GO


/* ----------------------------------------------------------------------------
   2) Columnas nuevas en PagoUnico (cobros de equipamiento)
   ---------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.PagoUnico')
                 AND name      = 'FechaLiberacion')
BEGIN
    ALTER TABLE dbo.PagoUnico ADD
        FechaLiberacion DATETIME      NULL,
        MontoNeto       DECIMAL(18,2) NULL,
        Comision        DECIMAL(18,2) NULL;

    PRINT 'Columnas de liberación agregadas a PagoUnico.';
END
ELSE
    PRINT 'PagoUnico ya tenía las columnas de liberación. Sin cambios.';
GO


/* ----------------------------------------------------------------------------
   3) Índices para la consulta más frecuente del módulo: "qué está por liberarse"
   ---------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_SuscripcionPago_Liberacion')
BEGIN
    CREATE NONCLUSTERED INDEX IX_SuscripcionPago_Liberacion
        ON dbo.SuscripcionPago (FechaLiberacion)
        WHERE FechaLiberacion IS NOT NULL;

    PRINT 'Índice IX_SuscripcionPago_Liberacion creado.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_PagoUnico_Liberacion')
BEGIN
    CREATE NONCLUSTERED INDEX IX_PagoUnico_Liberacion
        ON dbo.PagoUnico (FechaLiberacion)
        WHERE FechaLiberacion IS NOT NULL;

    PRINT 'Índice IX_PagoUnico_Liberacion creado.';
END
GO


/* ----------------------------------------------------------------------------
   4) Recrear vw_PagosUnicosEstado para exponer las columnas nuevas.

      Es la misma definición de 03_CrearTablaPagoUnico.sql más tres campos. Se
      recrea entera —y no con un ALTER puntual— porque una vista con SELECT
      explícito no se puede extender de otra forma.
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

PRINT 'Vista vw_PagosUnicosEstado recreada con liberación y neto.';
GO


/* ============================================================================
   NOTA PARA QUIEN EJECUTE ESTO

   Las tres columnas quedan en NULL:

     - en todos los cobros ya registrados;
     - en los cobros nuevos, hasta que la API los capture.

   La captura es trabajo aparte, en TecnisegurMercadoPago.Api: agregar
   money_release_date, fee_details y transaction_details.net_received_amount al
   modelo PagoRespuesta y persistirlos. Ver §9.1 de
   TSD\MODULO_COBRANZAS_MERCADOPAGO.md.

   Para rellenar los cobros viejos alcanza con POST /api/suscripciones/{id}/sincronizar
   una vez que la captura esté hecha.

   NULL significa "MercadoPago todavía no lo informó", NO significa cero. La
   interfaz tiene que mostrarlo como "—". Un neto en cero es indistinguible de
   "no se sabe", y en una pantalla de cobranza esa ambigüedad es un error de
   negocio.
   ============================================================================ */
