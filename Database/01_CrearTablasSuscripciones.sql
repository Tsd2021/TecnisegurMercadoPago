/* ============================================================================
   Integración MercadoPago — Suscripciones de cotizaciones de alarma
   Base de datos: TSD  (servidor 172.16.10.22)

   Se instala junto a CotizacionesComericales, que es donde viven las
   cotizaciones (ver EmpleadoWeb\Models\Cotizacion.cs).

   Script idempotente: se puede ejecutar más de una vez sin romper nada.
   ============================================================================ */

USE TSD;
GO

/* ----------------------------------------------------------------------------
   1) SuscripcionCotizacion
      Una fila por suscripción creada en MercadoPago a partir de una cotización.
   ---------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.SuscripcionCotizacion', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SuscripcionCotizacion
    (
        Id                  INT IDENTITY(1,1)   NOT NULL,

        /* Vínculo con el sistema propio */
        IdCotizacion        INT                 NOT NULL,
        ExternalReference   VARCHAR(64)         NOT NULL,   -- 'COT-{IdCotizacion}'

        /* Identificadores de MercadoPago */
        PreapprovalId       VARCHAR(64)         NULL,       -- id de la suscripción en MP
        InitPoint           VARCHAR(500)        NULL,       -- link que se envía al cliente

        /* Datos denormalizados para mostrar sin depender de otras tablas */
        NombreCliente       VARCHAR(200)        NULL,
        PayerEmail          VARCHAR(200)        NOT NULL,
        MontoMensual        DECIMAL(18,2)       NOT NULL,
        Moneda              CHAR(3)             NOT NULL,
        DiasPrueba          INT                 NULL,

        /* Estado y ciclo de vida */
        Estado              VARCHAR(20)         NOT NULL,   -- pending|authorized|paused|cancelled
        FechaCreacion       DATETIME            NOT NULL,
        FechaAutorizacion   DATETIME            NULL,
        FechaCancelacion    DATETIME            NULL,
        FechaProximoPago    DATETIME            NULL,
        FechaUltimoPago     DATETIME            NULL,
        MotivoCancelacion   VARCHAR(200)        NULL,

        /* Auditoría */
        UsuarioCreacion     VARCHAR(50)         NULL,
        Origen              VARCHAR(20)         NULL,       -- 'WEBEMPLEADO' | 'TSD'
        FechaActualizacion  DATETIME            NULL,

        CONSTRAINT PK_SuscripcionCotizacion PRIMARY KEY CLUSTERED (Id)
    );

    /* Una sola suscripción viva por cotización.
       Es la defensa contra el doble click en "Generar link" → doble cobro. */
    CREATE UNIQUE NONCLUSTERED INDEX UX_SuscripcionCotizacion_Viva
        ON dbo.SuscripcionCotizacion (IdCotizacion)
        WHERE Estado IN ('pending', 'authorized', 'paused');

    CREATE UNIQUE NONCLUSTERED INDEX UX_SuscripcionCotizacion_Preapproval
        ON dbo.SuscripcionCotizacion (PreapprovalId)
        WHERE PreapprovalId IS NOT NULL;

    CREATE NONCLUSTERED INDEX IX_SuscripcionCotizacion_Estado
        ON dbo.SuscripcionCotizacion (Estado, FechaCreacion DESC);

    PRINT 'Tabla SuscripcionCotizacion creada.';
END
ELSE
    PRINT 'Tabla SuscripcionCotizacion ya existe. Sin cambios.';
GO

/* Default de FechaCreacion / Moneda por separado para poder re-ejecutar */
IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_SuscripcionCotizacion_FechaCreacion')
    ALTER TABLE dbo.SuscripcionCotizacion
        ADD CONSTRAINT DF_SuscripcionCotizacion_FechaCreacion
            DEFAULT (GETDATE()) FOR FechaCreacion;
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_SuscripcionCotizacion_Moneda')
    ALTER TABLE dbo.SuscripcionCotizacion
        ADD CONSTRAINT DF_SuscripcionCotizacion_Moneda
            DEFAULT ('UYU') FOR Moneda;
GO

/* FK contra la tabla de cotizaciones.
   OJO: el nombre lleva el typo 'Comericales' tal como está en la base. */
IF OBJECT_ID('dbo.CotizacionesComericales', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys
                   WHERE name = 'FK_SuscripcionCotizacion_Cotizacion')
BEGIN
    ALTER TABLE dbo.SuscripcionCotizacion
        ADD CONSTRAINT FK_SuscripcionCotizacion_Cotizacion
            FOREIGN KEY (IdCotizacion)
            REFERENCES dbo.CotizacionesComericales (Id);

    PRINT 'FK a CotizacionesComericales creada.';
END
GO


/* ----------------------------------------------------------------------------
   2) SuscripcionPago
      Una fila por cuota cobrada (o intentada) por MercadoPago.
   ---------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.SuscripcionPago', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SuscripcionPago
    (
        Id                    INT IDENTITY(1,1) NOT NULL,
        IdSuscripcion         INT               NOT NULL,

        MpAuthorizedPaymentId VARCHAR(64)       NULL,
        MpPaymentId           VARCHAR(64)       NULL,

        Monto                 DECIMAL(18,2)     NOT NULL,
        Moneda                CHAR(3)           NOT NULL,

        /* processed | recycling | scheduled | cancelled */
        Estado                VARCHAR(30)       NOT NULL,
        /* approved | rejected | pending | refunded */
        EstadoPago            VARCHAR(30)       NULL,
        DetalleEstado         VARCHAR(100)      NULL,

        FechaProgramada       DATETIME          NULL,
        FechaPago             DATETIME          NULL,
        FechaRegistro         DATETIME          NOT NULL,

        PayloadJson           NVARCHAR(MAX)     NULL,

        CONSTRAINT PK_SuscripcionPago PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_SuscripcionPago_Suscripcion
            FOREIGN KEY (IdSuscripcion)
            REFERENCES dbo.SuscripcionCotizacion (Id)
    );

    /* Evita duplicar la misma cuota si MP reenvía la notificación */
    CREATE UNIQUE NONCLUSTERED INDEX UX_SuscripcionPago_Authorized
        ON dbo.SuscripcionPago (MpAuthorizedPaymentId)
        WHERE MpAuthorizedPaymentId IS NOT NULL;

    CREATE NONCLUSTERED INDEX IX_SuscripcionPago_Suscripcion
        ON dbo.SuscripcionPago (IdSuscripcion, FechaPago DESC);

    PRINT 'Tabla SuscripcionPago creada.';
END
ELSE
    PRINT 'Tabla SuscripcionPago ya existe. Sin cambios.';
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_SuscripcionPago_FechaRegistro')
    ALTER TABLE dbo.SuscripcionPago
        ADD CONSTRAINT DF_SuscripcionPago_FechaRegistro
            DEFAULT (GETDATE()) FOR FechaRegistro;
GO


/* ----------------------------------------------------------------------------
   3) MercadoPagoNotificacion
      Log crudo de webhooks. Sirve para idempotencia, auditoría y reproceso.
      El webhook escribe acá y responde 200 de inmediato; el procesamiento
      ocurre después (MP exige responder en menos de 22 segundos).
   ---------------------------------------------------------------------------- */
IF OBJECT_ID('dbo.MercadoPagoNotificacion', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MercadoPagoNotificacion
    (
        Id               INT IDENTITY(1,1)  NOT NULL,
        MpNotificationId VARCHAR(64)        NOT NULL,   -- id de la notificación
        Tipo             VARCHAR(50)        NOT NULL,   -- subscription_preapproval | ...
        Accion           VARCHAR(50)        NULL,
        DataId           VARCHAR(64)        NULL,       -- data.id (preapproval / payment)
        PayloadJson      NVARCHAR(MAX)      NOT NULL,
        FirmaValida      BIT                NOT NULL,
        FechaRecepcion   DATETIME           NOT NULL,
        Procesado        BIT                NOT NULL,
        FechaProceso     DATETIME           NULL,
        IntentosProceso  INT                NOT NULL,
        ErrorProceso     NVARCHAR(1000)     NULL,

        CONSTRAINT PK_MercadoPagoNotificacion PRIMARY KEY CLUSTERED (Id)
    );

    /* Idempotencia: MP puede reenviar la misma notificación varias veces */
    CREATE UNIQUE NONCLUSTERED INDEX UX_MercadoPagoNotificacion_MpId
        ON dbo.MercadoPagoNotificacion (MpNotificationId);

    CREATE NONCLUSTERED INDEX IX_MercadoPagoNotificacion_Pendientes
        ON dbo.MercadoPagoNotificacion (Procesado, FechaRecepcion)
        WHERE Procesado = 0;

    PRINT 'Tabla MercadoPagoNotificacion creada.';
END
ELSE
    PRINT 'Tabla MercadoPagoNotificacion ya existe. Sin cambios.';
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_MercadoPagoNotificacion_Fecha')
    ALTER TABLE dbo.MercadoPagoNotificacion
        ADD CONSTRAINT DF_MercadoPagoNotificacion_Fecha
            DEFAULT (GETDATE()) FOR FechaRecepcion;
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_MercadoPagoNotificacion_Procesado')
    ALTER TABLE dbo.MercadoPagoNotificacion
        ADD CONSTRAINT DF_MercadoPagoNotificacion_Procesado
            DEFAULT (0) FOR Procesado;
GO

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_MercadoPagoNotificacion_Intentos')
    ALTER TABLE dbo.MercadoPagoNotificacion
        ADD CONSTRAINT DF_MercadoPagoNotificacion_Intentos
            DEFAULT (0) FOR IntentosProceso;
GO


/* ----------------------------------------------------------------------------
   4) Vista de consumo
      Pensada para mostrar el estado desde cualquier plataforma (EmpleadoWeb,
      TSD Desktop, Excel, Power BI) sin tener que replicar los joins.
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
) AS p ON p.IdSuscripcion = s.Id;
GO

PRINT 'Vista vw_SuscripcionesEstado creada.';
GO
