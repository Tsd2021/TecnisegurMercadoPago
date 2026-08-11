/* ============================================================================
   Cruce de notificaciones: lo que MercadoPago dice que entregó vs. lo que
   quedó en nuestra base

   Sólo lectura. No modifica nada; se puede ejecutar en producción.

   Motivo. El MCP de MercadoPago expone `notifications_history`, que es el log
   de entregas del lado de ELLOS. Al 11/08/2026 devuelve, para la aplicación
   437871649677590 y el último mes:

       Total 25   ·   exitosas 25   ·   fallidas 0
       Por tópico   subscription_preapproval  25
       Por código   HTTP 200                  25

   Si nuestras filas coinciden con esas 25, entonces nada se perdió en el
   camino, y la notificación faltante de `version 1` del preapproval
   498c7bbb05124f908ed0eb6bafd85b8c no es una entrega caída: es una
   notificación que MercadoPago nunca generó. Eso es lo que hay que poder
   afirmar en el ticket, y es más fuerte que "no nos llegó".

   Ejecutar el archivo entero y pegar los cuatro resultados.
   ============================================================================ */

USE TSD;
GO

DECLARE @Preapproval VARCHAR(64) = '498c7bbb05124f908ed0eb6bafd85b8c';  -- el del ticket
DECLARE @Desde       DATETIME    = DATEADD(day, -31, GETDATE());


/* ----------------------------------------------------------------------------
   1) El número que se compara contra las 25 de MercadoPago

   Se cuentan TODAS las filas, incluidas las de firma inválida: MercadoPago
   contabiliza lo que entregó, no lo que nosotros aceptamos. Filtrar por
   FirmaValida acá haría que los totales no cierren por construcción.
   ---------------------------------------------------------------------------- */
SELECT
    COUNT(*)                                              AS TotalRecibidas,
    SUM(CASE WHEN FirmaValida = 1 THEN 1 ELSE 0 END)      AS ConFirmaValida,
    SUM(CASE WHEN FirmaValida = 0 THEN 1 ELSE 0 END)      AS ConFirmaInvalida,
    SUM(CASE WHEN Procesado   = 1 THEN 1 ELSE 0 END)      AS Procesadas,
    MIN(FechaRecepcion)                                   AS Primera,
    MAX(FechaRecepcion)                                   AS Ultima
FROM dbo.MercadoPagoNotificacion
WHERE FechaRecepcion >= @Desde;


/* ----------------------------------------------------------------------------
   2) Desglose por tópico y acción

   MercadoPago informa 25 de `subscription_preapproval` y CERO de
   `subscription_authorized_payment`. Si acá aparece algún tópico que ellos no
   listan, el que tiene el hueco es su historial, no el nuestro.
   ---------------------------------------------------------------------------- */
SELECT
    Tipo,
    ISNULL(Accion, '(sin accion)')                        AS Accion,
    COUNT(*)                                              AS Cantidad,
    COUNT(DISTINCT DataId)                                AS RecursosDistintos,
    MIN(FechaRecepcion)                                   AS Primera,
    MAX(FechaRecepcion)                                   AS Ultima
FROM dbo.MercadoPagoNotificacion
WHERE FechaRecepcion >= @Desde
GROUP BY Tipo, Accion
ORDER BY Tipo, Accion;


/* ----------------------------------------------------------------------------
   3) La secuencia de `version` del preapproval del ticket

   La evidencia del salto 0 → 2. `version` sale del payload crudo; JSON_VALUE
   devuelve NULL si la notificación no lo trae, y eso también es dato.
   ---------------------------------------------------------------------------- */
SELECT
    n.Id,
    n.MpNotificationId,
    n.Tipo,
    n.Accion,
    TRY_CAST(JSON_VALUE(n.PayloadJson, '$.version') AS INT) AS Version,
    n.FirmaValida,
    n.Procesado,
    n.IntentosProceso,
    n.ErrorProceso,
    n.FechaRecepcion,
    n.FechaProceso
FROM dbo.MercadoPagoNotificacion AS n
WHERE n.DataId = @Preapproval
ORDER BY n.FechaRecepcion;


/* ----------------------------------------------------------------------------
   4) Saltos de `version` en TODOS los preapproval, no sólo el del ticket

   Si el salto se repite en varias suscripciones deja de ser una anécdota y
   pasa a ser el patrón: la mutación que asocia el medio de pago nunca se
   notifica. `Salto` > 1 es un hueco; `Salto` = 1 es una secuencia sana.
   ---------------------------------------------------------------------------- */
WITH Versiones AS
(
    SELECT
        DataId,
        FechaRecepcion,
        Accion,
        TRY_CAST(JSON_VALUE(PayloadJson, '$.version') AS INT) AS Version
    FROM dbo.MercadoPagoNotificacion
    WHERE Tipo = 'subscription_preapproval'
      AND DataId IS NOT NULL
)
SELECT
    v.DataId                                              AS Preapproval,
    v.Accion,
    LAG(v.Version) OVER (PARTITION BY v.DataId ORDER BY v.FechaRecepcion) AS VersionAnterior,
    v.Version                                             AS VersionActual,
    v.Version - LAG(v.Version) OVER (PARTITION BY v.DataId ORDER BY v.FechaRecepcion) AS Salto,
    v.FechaRecepcion
FROM Versiones AS v
ORDER BY v.DataId, v.FechaRecepcion;
GO


/* ----------------------------------------------------------------------------
   5) Quien tuvo `version 1` y quien no, contra la suscripcion local

   La consulta 4 mostro que el hueco no es del caso del ticket: es la mayoria.
   Falta saber si separa dos poblaciones. `EnSuscripcionCotizacion = NO` son
   preapproval que MPAPI no creo — los de diagnostico contra la cuenta de
   prueba, que llegan al mismo webhook pero no dejan fila local.

   Si los que SI emitieron `version 1` son justamente los que no son nuestros,
   entonces `version 1` es la mutacion que asocia el medio de pago, y sale
   solo donde la asociacion prospera. Esa es la frase para el ticket.

   Sirve ademas para el otro descuadre: `notifications_history` cuenta 25 para
   la aplicacion productiva y la tabla tiene 50 filas. Las que no son nuestras
   explican la diferencia.
   ---------------------------------------------------------------------------- */
WITH Versiones AS
(
    SELECT
        DataId,
        FechaRecepcion,
        TRY_CAST(JSON_VALUE(PayloadJson, '$.version') AS INT) AS Version
    FROM dbo.MercadoPagoNotificacion
    WHERE Tipo = 'subscription_preapproval'
      AND DataId IS NOT NULL
),
Resumen AS
(
    SELECT
        DataId,
        MAX(CASE WHEN Version = 1 THEN 1 ELSE 0 END)      AS TuvoVersion1,
        COUNT(*)                                          AS Notificaciones,
        MAX(Version)                                      AS VersionMaxima,
        MIN(FechaRecepcion)                               AS Primera
    FROM Versiones
    GROUP BY DataId
)
SELECT
    r.DataId                                              AS Preapproval,
    CASE WHEN c.PreapprovalId IS NULL THEN 'NO' ELSE 'SI' END AS EsNuestra,
    c.IdCotizacion,
    c.Estado,
    c.Origen,
    c.FechaAutorizacion,
    r.TuvoVersion1,
    r.Notificaciones,
    r.VersionMaxima,
    r.Primera
FROM Resumen AS r
LEFT JOIN dbo.SuscripcionCotizacion AS c
       ON c.PreapprovalId = r.DataId
ORDER BY r.TuvoVersion1 DESC, r.Primera;
GO
