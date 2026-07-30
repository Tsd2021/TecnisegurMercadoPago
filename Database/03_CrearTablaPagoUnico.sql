/* ============================================================================
   Integración MercadoPago — Pagos únicos (Checkout Pro)
   Base de datos: TSD  (servidor 172.16.10.22)

   Cubre el caso que la suscripción NO cubre: el cliente compra equipamiento o
   insumos y no quiere financiarlos en la cuota mensual. En EmpleadoWeb es
   Cotizacion.TotalProductos cuando SumarProductosMensual = false.

   Script idempotente: se puede ejecutar más de una vez sin romper nada.
   Requiere 01_CrearTablasSuscripciones.sql ejecutado previamente.
   ============================================================================ */

USE TSD;
GO

/* ----------------------------------------------------------------------------
   1) PagoUnico
      Una fila por cobro puntual. A diferencia de las suscripciones, una misma
      cotización PUEDE tener varios pagos únicos a lo largo del tiempo (venta
      de insumos a demanda), así que no hay un "uno solo por cotización".
   ---------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.PagoUnico', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PagoUnico
    (
        Id                 INT IDENTITY(1,1)  NOT NULL,

        IdCotizacion       INT                NOT NULL,

        /* Identificadores de MercadoPago.
           La preferencia es el "link de pago"; el pago es el cobro concreto.
           Una preferencia puede terminar en cero o en un pago. */
        MpPreferenceId     VARCHAR(64)        NULL,
        MpPaymentId        VARCHAR(64)        NULL,
        InitPoint          VARCHAR(500)       NULL,

        /* Datos denormalizados, mismo criterio que SuscripcionCotizacion:
           poder mostrar el estado sin depender de otras tablas. */
        Concepto           VARCHAR(255)       NULL,
        NombreCliente      VARCHAR(200)       NULL,
        PayerEmail         VARCHAR(200)       NOT NULL,
        Monto              DECIMAL(18,2)      NOT NULL,
        Moneda             CHAR(3)            NOT NULL,

        /* pendiente = link generado, el cliente todavía no pagó.
           Después toma el status del pago de MercadoPago:
           approved | rejected | in_process | cancelled | refunded */
        Estado             VARCHAR(30)        NOT NULL,
        EstadoDetalle      VARCHAR(100)       NULL,

        FechaCreacion      DATETIME           NOT NULL,
        FechaPago          DATETIME           NULL,
        FechaActualizacion DATETIME           NULL,

        UsuarioCreacion    VARCHAR(50)        NULL,
        Origen             VARCHAR(20)        NULL,       -- 'WEBEMPLEADO' | 'TSD'

        PayloadJson        NVARCHAR(MAX)      NULL,

        /* Referencia que viaja a MercadoPago y vuelve en la consulta del pago.
           Es COLUMNA CALCULADA y PERSISTED a propósito: al derivarse del Id
           identity queda única por construcción, sin carrera posible y sin
           un UPDATE posterior al insert.

           El prefijo PAGO- la distingue de las suscripciones, que usan COT-.
           Compartir el prefijo haría ambigua la conciliación en el webhook. */
        ExternalReference  AS ('PAGO-' + CAST(IdCotizacion AS VARCHAR(10))
                                       + '-' + CAST(Id AS VARCHAR(10))) PERSISTED,

        CONSTRAINT PK_PagoUnico PRIMARY KEY CLUSTERED (Id)
    );

    /* Conciliación desde el webhook: el pago trae external_reference y por acá
       se llega a la fila local en una sola búsqueda. */
    CREATE UNIQUE NONCLUSTERED INDEX UX_PagoUnico_ExternalReference
        ON dbo.PagoUnico (ExternalReference);

    /* MercadoPago notifica el mismo pago más de una vez. */
    CREATE UNIQUE NONCLUSTERED INDEX UX_PagoUnico_MpPayment
        ON dbo.PagoUnico (MpPaymentId)
        WHERE MpPaymentId IS NOT NULL;

    CREATE UNIQUE NONCLUSTERED INDEX UX_PagoUnico_MpPreference
        ON dbo.PagoUnico (MpPreferenceId)
        WHERE MpPreferenceId IS NOT NULL;

    /* Defensa contra el doble click en "Generar link de pago": no puede haber
       dos links vivos sin resolver para la misma cotización. Una vez que el
       anterior se aprueba, rechaza o cancela, se puede generar otro — que es
       lo que habilita la venta de insumos a demanda. */
    CREATE UNIQUE NONCLUSTERED INDEX UX_PagoUnico_PendientePorCotizacion
        ON dbo.PagoUnico (IdCotizacion)
        WHERE Estado = 'pendiente';

    CREATE NONCLUSTERED INDEX IX_PagoUnico_Cotizacion
        ON dbo.PagoUnico (IdCotizacion, FechaCreacion DESC);

    PRINT 'Tabla PagoUnico creada.';
END
ELSE
    PRINT 'Tabla PagoUnico ya existe. Sin cambios.';
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_PagoUnico_FechaCreacion')
    ALTER TABLE dbo.PagoUnico
        ADD CONSTRAINT DF_PagoUnico_FechaCreacion
            DEFAULT (GETDATE()) FOR FechaCreacion;
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_PagoUnico_Moneda')
    ALTER TABLE dbo.PagoUnico
        ADD CONSTRAINT DF_PagoUnico_Moneda
            DEFAULT ('UYU') FOR Moneda;
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_PagoUnico_Estado')
    ALTER TABLE dbo.PagoUnico
        ADD CONSTRAINT DF_PagoUnico_Estado
            DEFAULT ('pendiente') FOR Estado;
GO

/* FK contra la tabla de cotizaciones.
   OJO: el nombre lleva el typo 'Comericales' tal como está en la base. */
IF OBJECT_ID('dbo.CotizacionesComericales', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys
                   WHERE name = 'FK_PagoUnico_Cotizacion')
BEGIN
    ALTER TABLE dbo.PagoUnico
        ADD CONSTRAINT FK_PagoUnico_Cotizacion
            FOREIGN KEY (IdCotizacion)
            REFERENCES dbo.CotizacionesComericales (Id);

    PRINT 'FK de PagoUnico a CotizacionesComericales creada.';
END
GO


/* ----------------------------------------------------------------------------
   2) Vista de consumo
      Mismo criterio que vw_SuscripcionesEstado: las lecturas van contra la
      vista, no contra la tabla.
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
    p.Origen,
    p.UsuarioCreacion
FROM dbo.PagoUnico AS p;
GO

PRINT 'Vista vw_PagosUnicosEstado creada.';
GO
