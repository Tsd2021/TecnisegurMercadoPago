using System.Diagnostics;

namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Rastro de auditoría de toda mutación sobre un preapproval de MercadoPago.
///
/// EXISTE PORQUE NO SE PODÍA DEMOSTRAR QUIÉN CANCELABA. Cuando una suscripción
/// aparece cancelada, la pregunta es si el PUT salió de acá o si MercadoPago la
/// dio de baja por su cuenta. Hasta ahora el log decía "Suscripción X cancelada"
/// sin estado anterior ni origen, y la sincronización decía "Estado: cancelled"
/// sin decir contra qué estado se comparaba: las dos líneas son indistinguibles
/// de la otra hipótesis.
///
/// Todas las líneas llevan el prefijo AUDITORIA-PREAPPROVAL para poder aislarlas
/// del resto del log con un solo filtro:
///
///     Select-String "AUDITORIA-PREAPPROVAL" .\logs\stdout_*.log
///
/// Nunca se registra el access token ni ningún secreto: sólo identificadores de
/// MercadoPago, estados y el origen de la operación.
/// </summary>
public static class AuditoriaPreapproval
{
    /// <summary>
    /// Origen de una mutación. El valor viaja hasta el log para poder responder
    /// "quién lo hizo" sin reconstruirlo del contexto.
    /// </summary>
    public static class Origen
    {
        /// <summary>Baja pedida por un usuario desde EmpleadoWeb o TSD.</summary>
        public const string CancelacionExplicita = "cancelacion-explicita";

        /// <summary>
        /// El índice único rechazó el insert: se cancela la suscripción recién
        /// creada para no dejarla huérfana en MercadoPago.
        /// </summary>
        public const string CarreraIndiceUnico = "carrera-indice-unico";

        /// <summary>Cambio de importe por edición de la cotización.</summary>
        public const string CambioImporte = "cambio-importe";

        /// <summary>
        /// La transición la informó MercadoPago; nuestro código sólo la copió.
        /// Es el valor que distingue una baja ajena de una propia.
        /// </summary>
        public const string MercadoPago = "mercadopago";
    }

    /// <summary>
    /// Identificador para correlacionar la línea de auditoría con el resto de
    /// las líneas de la misma request. Usa el trace de ASP.NET Core cuando hay
    /// uno —así se ata a la llamada de EmpleadoWeb— y cae a un id propio en el
    /// BackgroundService, que corre fuera de toda request.
    /// </summary>
    public static string Correlacion() =>
        Activity.Current?.Id ?? $"bg-{Guid.NewGuid():N}"[..20];

    /// <summary>
    /// Una mutación que SALIÓ DE NUESTRO CÓDIGO: el PUT lo mandamos nosotros.
    /// Se emite antes de llamar a MercadoPago, de modo que quede registrada
    /// aunque la llamada falle o el proceso muera en el medio.
    /// </summary>
    public static void Emitida(
        ILogger log,
        string operacion,
        string origen,
        string preapprovalId,
        string? externalReference,
        int? idCotizacion,
        string? estadoLocalAnterior,
        string? estadoRemotoAnterior,
        string? estadoNuevo,
        string? detalle,
        string correlacion)
    {
        log.LogWarning(
            "AUDITORIA-PREAPPROVAL {InstanteUtc:o} operacion={Operacion} " +
            "direccion=SALIENTE origen={Origen} preapproval={Preapproval} " +
            "external_reference={Referencia} cotizacion={Cotizacion} " +
            "estado_local_anterior={EstadoLocal} estado_remoto_anterior={EstadoRemoto} " +
            "estado_nuevo={EstadoNuevo} detalle={Detalle} correlacion={Correlacion}",
            DateTime.UtcNow, operacion, origen, preapprovalId,
            externalReference ?? "(sin dato)", idCotizacion,
            estadoLocalAnterior ?? "(sin dato)", estadoRemotoAnterior ?? "(no consultado)",
            estadoNuevo ?? "(sin dato)", detalle ?? "(sin detalle)", correlacion);
    }

    /// <summary>
    /// Una transición que NOS INFORMÓ MercadoPago y que sólo estamos copiando.
    /// Es la contracara de <see cref="Emitida"/>: si una suscripción aparece
    /// cancelada y en el log hay una línea ENTRANTE sin ninguna SALIENTE previa
    /// para el mismo preapproval, la baja no salió de acá.
    /// </summary>
    public static void Recibida(
        ILogger log,
        string preapprovalId,
        string? externalReference,
        int? idCotizacion,
        string? estadoLocalAnterior,
        string? estadoInformado,
        string correlacion)
    {
        var degrada =
            EstadoSuscripcion.EsCancelada(estadoInformado) &&
            EstadoSuscripcion.EstaViva(estadoLocalAnterior);

        /* Una baja informada por MercadoPago sobre una suscripción que estaba
           viva es exactamente el hecho que se quería poder demostrar: se sube a
           warning para que no se pierda entre las líneas de sincronización. */
        var nivel = degrada ? LogLevel.Warning : LogLevel.Information;

        log.Log(nivel,
            "AUDITORIA-PREAPPROVAL {InstanteUtc:o} operacion=SINCRONIZAR " +
            "direccion=ENTRANTE origen={Origen} preapproval={Preapproval} " +
            "external_reference={Referencia} cotizacion={Cotizacion} " +
            "estado_local_anterior={EstadoLocal} estado_informado={EstadoInformado} " +
            "baja_ajena={BajaAjena} correlacion={Correlacion}",
            DateTime.UtcNow, Origen.MercadoPago, preapprovalId,
            externalReference ?? "(sin dato)", idCotizacion,
            estadoLocalAnterior ?? "(sin dato)", estadoInformado ?? "(sin dato)",
            degrada, correlacion);
    }

    /// <summary>
    /// MercadoPago notificó un preapproval del que no hay fila local. Devuelve
    /// la nota que se guarda en <c>MercadoPagoNotificacion.NotaProceso</c>: la
    /// línea de log sola no alcanza, porque el stdout del servidor es rotativo y
    /// en la base el caso es indistinguible de una sincronización normal.
    ///
    /// Un <paramref name="externalReference"/> con prefijo <c>COT-</c> es la
    /// señal fea: significa que la suscripción fue nuestra y la fila ya no está.
    /// Con prefijo <c>DIAG-</c> es esperable, son las del script de diagnóstico.
    /// </summary>
    public static string SinRegistroLocal(
        ILogger log,
        string preapprovalId,
        string? externalReference,
        string? estadoInformado,
        string correlacion)
    {
        var referencia = externalReference ?? "(sin dato)";
        var estado = estadoInformado ?? "(sin dato)";

        log.LogWarning(
            "AUDITORIA-PREAPPROVAL {InstanteUtc:o} operacion=SINCRONIZAR " +
            "direccion=ENTRANTE origen={Origen} preapproval={Preapproval} " +
            "external_reference={Referencia} estado_informado={EstadoInformado} " +
            "sin_registro_local=True correlacion={Correlacion}",
            DateTime.UtcNow, Origen.MercadoPago, preapprovalId,
            referencia, estado, correlacion);

        var nota =
            $"Sin registro local. external_reference={referencia}, " +
            $"estado_informado={estado}, correlacion={correlacion}.";

        return nota.Length <= 400 ? nota : nota[..400];
    }
}
