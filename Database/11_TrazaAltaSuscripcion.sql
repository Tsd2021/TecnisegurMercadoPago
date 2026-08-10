/* ============================================================================
   11 - Traza del alta: el payload que se le mandó a MercadoPago

   Agrega SuscripcionCotizacion.PayloadEnvioJson y deja dos consultas de higiene
   para detectar las suscripciones que quedaron trabadas.

   POR QUÉ HACE FALTA
   ------------------
   Cuando un cliente abre el init_point y no puede autorizar, no queda rastro de
   nada: MercadoPago no guarda el intento fallido, el preapproval se queda en
   'pending' para siempre y la única evidencia de que algo salió mal es una fila
   que envejece. Sin el payload no hay forma de saber con qué start_date y qué
   end_date se creó esa suscripción, que es justo donde estuvieron los dos
   errores que se corrigieron:

     - end_date se calculaba desde hoy y no desde la fecha de adhesión, de modo
       que un plazo corto con adhesión lejana producía una suscripción que
       terminaba antes de empezar. MercadoPago la aceptaba en 'pending' sin
       chistar y recién fallaba cuando el cliente iba a autorizar.

     - start_date se congela al crear el link. Si el vendedor pone la adhesión
       para mañana y el cliente abre el link una semana después, esa fecha ya es
       pasada en el momento de autorizar.

   La columna es NULL para todo lo creado antes de este script. NULL significa
   "se creó sin traza", no "se mandó vacío".

   Requiere 01_CrearTablasSuscripciones.sql y 04_AgregarFechaInicioSuscripcion.sql.
   Idempotente: se puede correr las veces que haga falta.
   ============================================================================ */

USE TSD;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.SuscripcionCotizacion')
                 AND name      = 'PayloadEnvioJson')
BEGIN
    ALTER TABLE dbo.SuscripcionCotizacion ADD PayloadEnvioJson NVARCHAR(MAX) NULL;

    PRINT 'Columna SuscripcionCotizacion.PayloadEnvioJson agregada.';
END
ELSE
    PRINT 'SuscripcionCotizacion.PayloadEnvioJson ya existía. Sin cambios.';
GO


/* ============================================================================
   CONSULTAS DE HIGIENE — no crean nada, se corren a mano cuando hace falta
   ============================================================================ */

/* ---------------------------------------------------------------------------
   1) Suscripciones trabadas: 'pending' con la fecha de adhesión ya vencida.

   Son las que el cliente ya no puede autorizar aunque abra el link: el
   start_date que quedó grabado en MercadoPago es pasado. Hay que cancelarlas y
   generar el link de nuevo; no se arreglan solas.
   --------------------------------------------------------------------------- */
/*
SELECT
    s.Id                AS IdSuscripcion,
    s.IdCotizacion,
    s.NombreCliente,
    s.PayerEmail,
    s.MontoMensual,
    s.FechaInicio,
    DATEDIFF(DAY, s.FechaInicio, GETDATE()) AS DiasVencida,
    s.FechaCreacion,
    s.Origen,
    s.UsuarioCreacion
FROM dbo.SuscripcionCotizacion AS s
WHERE s.Estado = 'pending'
  AND s.FechaInicio IS NOT NULL
  AND s.FechaInicio < CAST(GETDATE() AS DATE)
ORDER BY s.FechaInicio;
*/


/* ---------------------------------------------------------------------------
   2) Suscripciones abandonadas: 'pending' de más de 7 días.

   El link se generó y nadie lo autorizó. Cada una ocupa el índice único
   UX_SuscripcionCotizacion_Viva, así que bloquea generar otra para la misma
   cotización.
   --------------------------------------------------------------------------- */
/*
SELECT
    s.Id                AS IdSuscripcion,
    s.IdCotizacion,
    s.NombreCliente,
    s.PayerEmail,
    s.MontoMensual,
    s.FechaCreacion,
    DATEDIFF(DAY, s.FechaCreacion, GETDATE()) AS DiasSinAutorizar,
    s.Origen,
    s.UsuarioCreacion
FROM dbo.SuscripcionCotizacion AS s
WHERE s.Estado = 'pending'
  AND s.FechaCreacion < DATEADD(DAY, -7, GETDATE())
ORDER BY s.FechaCreacion;
*/


/* ---------------------------------------------------------------------------
   3) Qué fechas se mandaron realmente, leídas del payload.

   Sirve para confirmar el diagnóstico sobre las suscripciones creadas DESPUÉS
   de este script. Si FinAntesDeInicio da 1 en alguna, el cálculo de end_date
   volvió a romperse.
   --------------------------------------------------------------------------- */
/*
SELECT
    s.Id                AS IdSuscripcion,
    s.IdCotizacion,
    s.PayerEmail,
    s.Estado,
    JSON_VALUE(s.PayloadEnvioJson, '$.auto_recurring.start_date') AS StartDateEnviado,
    JSON_VALUE(s.PayloadEnvioJson, '$.auto_recurring.end_date')   AS EndDateEnviado,
    JSON_VALUE(s.PayloadEnvioJson, '$.back_url')                  AS BackUrlEnviada,
    JSON_VALUE(s.PayloadEnvioJson, '$.notification_url')          AS NotificationUrlEnviada,
    CASE
        WHEN JSON_VALUE(s.PayloadEnvioJson, '$.auto_recurring.end_date') <=
             JSON_VALUE(s.PayloadEnvioJson, '$.auto_recurring.start_date')
        THEN 1 ELSE 0
    END                 AS FinAntesDeInicio
FROM dbo.SuscripcionCotizacion AS s
WHERE s.PayloadEnvioJson IS NOT NULL
ORDER BY s.FechaCreacion DESC;
*/
