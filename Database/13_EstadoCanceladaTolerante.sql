/* ============================================================================
   13 — vw_SuscripcionesEstado tolerante a las dos ortografías de "cancelada"

   Idempotente. Sólo recrea una vista; no toca datos ni estructura de tablas.

   POR QUÉ
   -------
   MercadoPago documenta hoy `canceled` (una ele) para dar de baja una
   suscripción. Este sistema venía mandando `cancelled` (dos eles) y MercadoPago
   lo aceptaba. Con qué ortografía RESPONDE el GET sigue sin verificarse.

   La API ya normaliza: EstadoSuscripcion.ParaPersistir traduce cualquiera de
   las dos al valor de contrato 'cancelled' antes de escribir, porque TSD
   Desktop y EmpleadoWeb comparan contra ese literal. Esta vista es la segunda
   red: cubre las filas escritas ANTES de esa normalización, por si alguna
   sincronización guardó 'canceled' verbatim.

   Sin esto, una fila con 'canceled' caería al ELSE del CASE y la pantalla
   mostraría el texto crudo 'canceled' en vez de 'Cancelada'.

   vw_ClientesCobranzaMP hereda EstadoDescripcion de esta vista, así que se
   arregla sola. vw_CuotasSuscripcion y vw_PagosUnicosEstado NO se tocan: su
   columna Estado es el estado de la CUOTA (scheduled/processed/recycling),
   otro dominio con su propio vocabulario.

   La definición es la de 04_AgregarFechaInicioSuscripcion.sql más el WHEN
   nuevo. Se recrea entera porque una vista con SELECT explícito no se puede
   extender de otra forma.
   ============================================================================ */

USE TSD;
GO

/* ----------------------------------------------------------------------------
   1) DIAGNÓSTICO — ¿con qué ortografía responde MercadoPago?

   Esta consulta contesta desde los datos la pregunta que quedó abierta. Toda
   fila cancelada llegó a la base por uno de dos caminos:

     - CancelarAsync    → escribe 'cancelled' (valor de contrato, lo elegimos
                          nosotros: no informa nada sobre MercadoPago).
     - Sincronización   → antes de esta normalización escribía verbatim lo que
                          devolvía GET /preapproval.

   Por lo tanto: si aparece aunque sea UNA fila con 'canceled' y su
   MotivoCancelacion es 'Cancelada en MercadoPago', entonces MercadoPago
   responde con una sola ele. Si todas dicen 'cancelled', o responde con dos o
   nunca hubo una baja informada por ellos.

   Correrla ANTES de aplicar el resto del script.
   ---------------------------------------------------------------------------- */
SELECT
    s.Estado,
    s.MotivoCancelacion,
    COUNT(*)          AS Filas,
    MIN(s.FechaActualizacion) AS PrimeraVez,
    MAX(s.FechaActualizacion) AS UltimaVez
FROM dbo.SuscripcionCotizacion AS s
GROUP BY s.Estado, s.MotivoCancelacion
ORDER BY s.Estado, Filas DESC;
GO


/* ----------------------------------------------------------------------------
   2) La vista
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

    /* Estado crudo. NO se normaliza acá: TSD y EmpleadoWeb comparan contra
       'cancelled' y cambiar esta columna los rompería en silencio. La
       normalización vive en la API (EstadoSuscripcion.ParaPersistir), que es
       quien escribe. */
    s.Estado,

    CASE s.Estado
        WHEN 'pending'    THEN 'Pendiente de autorización'
        WHEN 'authorized' THEN 'Activa'
        WHEN 'paused'     THEN 'Pausada'
        WHEN 'cancelled'  THEN 'Cancelada'
        WHEN 'canceled'   THEN 'Cancelada'   -- ortografía de MercadoPago
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

PRINT 'Vista vw_SuscripcionesEstado recreada: EstadoDescripcion acepta cancelled y canceled.';
GO


/* ----------------------------------------------------------------------------
   3) OPCIONAL — unificar filas históricas al valor de contrato

   Comentado a propósito: es un UPDATE y no corresponde ejecutarlo sin haber
   mirado antes la consulta 1. Sólo tiene sentido si esa consulta devolvió
   filas con 'canceled'.

   El índice único filtrado no se ve afectado: su predicado es por inclusión
   ('pending','authorized','paused'), así que ninguna de las dos ortografías
   entra ni sale de él.
   ---------------------------------------------------------------------------- */
/*
UPDATE dbo.SuscripcionCotizacion
SET    Estado = 'cancelled',
       FechaActualizacion = GETDATE()
WHERE  Estado = 'canceled';
*/
