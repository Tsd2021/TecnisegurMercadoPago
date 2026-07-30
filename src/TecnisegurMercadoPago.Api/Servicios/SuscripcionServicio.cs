using System.Text.Json;
using Microsoft.Extensions.Options;
using TecnisegurMercadoPago.Api.Configuracion;
using TecnisegurMercadoPago.Api.Datos;
using TecnisegurMercadoPago.Api.Modelos.Contratos;
using TecnisegurMercadoPago.Api.Modelos.MercadoPago;

namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Se lanza cuando la operación no puede completarse por una regla de negocio
/// (no por un fallo técnico). El controller la traduce a 409.
/// </summary>
public sealed class ReglaNegocioException : Exception
{
    public ReglaNegocioException(string mensaje) : base(mensaje) { }
}

/// <summary>
/// Orquesta el alta y el mantenimiento de suscripciones:
/// valida reglas, llama a MercadoPago y persiste el resultado.
/// </summary>
public sealed class SuscripcionServicio
{
    private readonly MercadoPagoCliente _mercadoPago;
    private readonly SuscripcionRepositorio _repositorio;
    private readonly MercadoPagoOpciones _opciones;
    private readonly ILogger<SuscripcionServicio> _log;

    public SuscripcionServicio(
        MercadoPagoCliente mercadoPago,
        SuscripcionRepositorio repositorio,
        IOptions<MercadoPagoOpciones> opciones,
        ILogger<SuscripcionServicio> log)
    {
        _mercadoPago = mercadoPago;
        _repositorio = repositorio;
        _opciones = opciones.Value;
        _log = log;
    }

    public async Task<CrearSuscripcionRespuesta> CrearAsync(
        CrearSuscripcionSolicitud solicitud, CancellationToken ct = default)
    {
        /* -------------------------------------------------------------------
         * 1) No puede haber dos suscripciones vivas para la misma cotización.
         *    Se chequea acá para dar un mensaje claro; el índice único de la
         *    base es la garantía real ante llamadas concurrentes.
         * ------------------------------------------------------------------- */
        var existente = await _repositorio
            .ObtenerVivaPorCotizacionAsync(solicitud.IdCotizacion, ct);

        if (existente is not null)
        {
            throw new ReglaNegocioException(
                $"La cotización {solicitud.IdCotizacion} ya tiene una suscripción " +
                $"en estado '{existente.EstadoDescripcion}'. " +
                "Cancele la existente antes de generar una nueva.");
        }

        var externalReference = $"COT-{solicitud.IdCotizacion}";

        /* -------------------------------------------------------------------
         * Cuándo arranca el cobro. FechaInicio (start_date) y DiasPrueba
         * (free_trial) hacen lo mismo y MercadoPago no documenta cómo los
         * combina, así que se rechaza la mezcla en vez de mandar algo cuyo
         * resultado no podemos predecir.
         * ------------------------------------------------------------------- */
        var fechaInicio = NormalizarFechaInicio(solicitud.FechaInicio);

        if (fechaInicio.HasValue && solicitud.DiasPrueba > 0)
        {
            throw new ReglaNegocioException(
                "No se puede indicar fecha de inicio y días de prueba a la vez: " +
                "los dos posponen la primera cuota. Elija uno de los dos.");
        }

        /* El default de configuración sólo aplica cuando no hay fecha: si no,
           una llamada que manda FechaInicio y omite DiasPrueba se comería los
           15 días por defecto sin que nadie los haya pedido. */
        var diasPrueba = fechaInicio.HasValue
            ? 0
            : solicitud.DiasPrueba ?? _opciones.DiasPruebaPorDefecto;

        var preapproval = new PreapprovalSolicitud
        {
            Reason = ArmarConcepto(solicitud.NombreCliente),
            ExternalReference = externalReference,
            PayerEmail = solicitud.PayerEmail.Trim(),
            BackUrl = ArmarBackUrl(solicitud.IdCotizacion),
            Status = "pending",
            AutoRecurring = new AutoRecurring
            {
                Frequency = 1,
                FrequencyType = "months",
                TransactionAmount = solicitud.MontoMensual,
                CurrencyId = _opciones.Moneda,
                StartDate = fechaInicio,
                EndDate = solicitud.PlazoMeses.HasValue
                    ? DateTimeOffset.Now.AddMonths(solicitud.PlazoMeses.Value)
                    : null,
                FreeTrial = diasPrueba > 0
                    ? new FreeTrial { Frequency = diasPrueba, FrequencyType = "days" }
                    : null
            }
        };

        /* -------------------------------------------------------------------
         * 2) La clave de idempotencia se deriva de la cotización y el importe.
         *    Si el usuario hace doble click, MercadoPago devuelve la misma
         *    suscripción en vez de crear una segunda.
         * ------------------------------------------------------------------- */
        var claveIdempotencia = $"{externalReference}-{solicitud.MontoMensual:0.00}";

        var respuesta = await _mercadoPago
            .CrearSuscripcionAsync(preapproval, claveIdempotencia, ct);

        if (string.IsNullOrWhiteSpace(respuesta.Id) ||
            string.IsNullOrWhiteSpace(respuesta.InitPoint))
        {
            throw new InvalidOperationException(
                "MercadoPago no devolvió id o init_point para la suscripción.");
        }

        /* -------------------------------------------------------------------
         * 3) Persistir. Si el índice único rechaza el insert significa que otra
         *    llamada concurrente ganó la carrera: se cancela la suscripción
         *    recién creada en MercadoPago para no dejarla huérfana.
         * ------------------------------------------------------------------- */
        var idSuscripcion = await _repositorio.InsertarAsync(
            solicitud.IdCotizacion,
            externalReference,
            respuesta.Id,
            respuesta.InitPoint,
            solicitud.NombreCliente.Trim(),
            solicitud.PayerEmail.Trim(),
            solicitud.MontoMensual,
            _opciones.Moneda,
            diasPrueba,
            fechaInicio?.LocalDateTime,
            respuesta.Status ?? "pending",
            respuesta.NextPaymentDate?.LocalDateTime,
            solicitud.UsuarioCreacion,
            solicitud.Origen,
            ct);

        if (idSuscripcion is null)
        {
            _log.LogWarning(
                "Carrera detectada en la cotización {Id}. Se cancela {Preapproval}.",
                solicitud.IdCotizacion, respuesta.Id);

            await CancelarEnMercadoPagoSilencioso(respuesta.Id, ct);

            throw new ReglaNegocioException(
                "Otra operación creó la suscripción al mismo tiempo. " +
                "Vuelva a consultar el estado de la cotización.");
        }

        _log.LogInformation(
            "Suscripción {Id} creada para la cotización {Cotizacion} ({Preapproval}).",
            idSuscripcion, solicitud.IdCotizacion, respuesta.Id);

        return new CrearSuscripcionRespuesta
        {
            IdSuscripcion = idSuscripcion.Value,
            IdCotizacion = solicitud.IdCotizacion,
            PreapprovalId = respuesta.Id,
            InitPoint = respuesta.InitPoint,
            Estado = respuesta.Status ?? "pending",
            MontoMensual = solicitud.MontoMensual,
            Moneda = _opciones.Moneda,
            DiasPrueba = diasPrueba,
            FechaInicio = fechaInicio?.LocalDateTime
        };
    }

    public async Task<SuscripcionEstadoDto?> ObtenerAsync(
        int idSuscripcion, CancellationToken ct = default)
    {
        var suscripcion = await _repositorio.ObtenerPorIdAsync(idSuscripcion, ct);
        if (suscripcion is null) return null;

        suscripcion.Pagos = await _repositorio.ListarPagosAsync(idSuscripcion, ct);
        return suscripcion;
    }

    public async Task<List<SuscripcionEstadoDto>> ListarPorCotizacionAsync(
        int idCotizacion, CancellationToken ct = default)
    {
        var lista = await _repositorio.ListarPorCotizacionAsync(idCotizacion, ct);

        foreach (var item in lista)
            item.Pagos = await _repositorio.ListarPagosAsync(item.IdSuscripcion, ct);

        return lista;
    }

    /// <summary>
    /// Cambia el importe de una suscripción viva. Es lo que hay que llamar
    /// cuando se edita una cotización que ya tiene suscripción: sin esto,
    /// MercadoPago sigue cobrando el importe original indefinidamente.
    /// </summary>
    public async Task<SuscripcionEstadoDto> ActualizarMontoAsync(
        int idSuscripcion, decimal montoMensual, CancellationToken ct = default)
    {
        var suscripcion = await _repositorio.ObtenerPorIdAsync(idSuscripcion, ct)
            ?? throw new ReglaNegocioException(
                $"No existe la suscripción {idSuscripcion}.");

        if (string.IsNullOrWhiteSpace(suscripcion.PreapprovalId))
        {
            throw new ReglaNegocioException(
                "La suscripción no tiene identificador de MercadoPago.");
        }

        if (suscripcion.Estado == "cancelled")
        {
            throw new ReglaNegocioException(
                "No se puede modificar el importe de una suscripción cancelada.");
        }

        await _mercadoPago.ActualizarMontoAsync(
            suscripcion.PreapprovalId, montoMensual, ct);

        await _repositorio.ActualizarMontoAsync(idSuscripcion, montoMensual, ct);

        _log.LogInformation(
            "Importe de la suscripción {Id} actualizado a {Monto}.",
            idSuscripcion, montoMensual);

        return (await ObtenerAsync(idSuscripcion, ct))!;
    }

    public async Task<SuscripcionEstadoDto> CancelarAsync(
        int idSuscripcion, string? motivo, CancellationToken ct = default)
    {
        var suscripcion = await _repositorio.ObtenerPorIdAsync(idSuscripcion, ct)
            ?? throw new ReglaNegocioException(
                $"No existe la suscripción {idSuscripcion}.");

        if (suscripcion.Estado == "cancelled")
            return (await ObtenerAsync(idSuscripcion, ct))!;

        if (string.IsNullOrWhiteSpace(suscripcion.PreapprovalId))
        {
            throw new ReglaNegocioException(
                "La suscripción no tiene identificador de MercadoPago.");
        }

        await _mercadoPago.CancelarSuscripcionAsync(suscripcion.PreapprovalId, ct);

        await _repositorio.ActualizarEstadoAsync(
            suscripcion.PreapprovalId, "cancelled", null,
            motivo ?? "Cancelada desde el sistema", ct);

        _log.LogInformation("Suscripción {Id} cancelada. Motivo: {Motivo}",
            idSuscripcion, motivo);

        return (await ObtenerAsync(idSuscripcion, ct))!;
    }

    /// <summary>
    /// Vuelve a consultar MercadoPago y sincroniza estado Y cuotas.
    ///
    /// Es la red de seguridad ante notificaciones perdidas: las que ocurrieron
    /// antes de configurar el webhook, las que fallaron tras los 5 reintentos,
    /// o cualquier ventana en que el servicio estuvo caído. Por eso recupera
    /// también los pagos: sincronizar sólo el estado dejaría la cobranza en
    /// cero aunque MercadoPago ya hubiera cobrado.
    ///
    /// Es idempotente: el MERGE sobre MpAuthorizedPaymentId evita duplicar
    /// cuotas que ya estaban registradas.
    /// </summary>
    public async Task<SuscripcionEstadoDto?> SincronizarAsync(
        int idSuscripcion, CancellationToken ct = default)
    {
        var suscripcion = await _repositorio.ObtenerPorIdAsync(idSuscripcion, ct);

        if (suscripcion?.PreapprovalId is null) return suscripcion;

        var remota = await _mercadoPago
            .ObtenerSuscripcionAsync(suscripcion.PreapprovalId, ct);

        await _repositorio.ActualizarEstadoAsync(
            suscripcion.PreapprovalId,
            remota.Status ?? suscripcion.Estado!,
            remota.NextPaymentDate?.LocalDateTime,
            null,
            ct);

        await SincronizarPagosAsync(suscripcion, ct);

        return await ObtenerAsync(idSuscripcion, ct);
    }

    /// <summary>
    /// Trae de MercadoPago todas las cuotas de la suscripción y las registra.
    /// Un fallo acá no invalida la sincronización del estado, que es lo
    /// principal; se registra y se sigue.
    /// </summary>
    private async Task SincronizarPagosAsync(
        SuscripcionEstadoDto suscripcion, CancellationToken ct)
    {
        try
        {
            var busqueda = await _mercadoPago
                .BuscarPagosAutorizadosAsync(suscripcion.PreapprovalId!, ct);

            foreach (var cuota in busqueda.Results)
            {
                await _repositorio.RegistrarPagoAsync(
                    suscripcion.IdSuscripcion,
                    cuota.Id?.ToString(),
                    cuota.Payment?.Id?.ToString(),
                    cuota.TransactionAmount ?? suscripcion.MontoMensual,
                    cuota.CurrencyId ?? suscripcion.Moneda ?? _opciones.Moneda,
                    cuota.Status ?? "desconocido",
                    cuota.Payment?.Status,
                    cuota.Payment?.StatusDetail,
                    cuota.DebitDate?.LocalDateTime,
                    cuota.Payment?.Status == "approved"
                        ? cuota.DebitDate?.LocalDateTime
                        : null,
                    JsonSerializer.Serialize(cuota),
                    ct);
            }

            if (busqueda.Results.Count > 0)
            {
                _log.LogInformation(
                    "Sincronizadas {Cantidad} cuotas de la suscripción {Id}.",
                    busqueda.Results.Count, suscripcion.IdSuscripcion);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "No se pudieron sincronizar las cuotas de la suscripción {Id}.",
                suscripcion.IdSuscripcion);
        }
    }

    /// <summary>
    /// Convierte el día elegido por el vendedor en el instante que espera
    /// MercadoPago, o null si corresponde cobrar apenas el cliente autorice.
    ///
    /// Se manda el mediodía y no la medianoche: MercadoPago interpreta
    /// start_date en el huso del envío, y una medianoche exacta queda a un
    /// desfasaje de horas de caer en el día anterior. El mediodía deja seis
    /// horas de margen para cada lado y el día del mes nunca se corre —que es
    /// justamente lo que se quiere fijar.
    ///
    /// Una fecha pasada o de hoy se descarta: no se puede empezar a cobrar
    /// ayer, y MercadoPago devuelve 400 si start_date ya pasó.
    /// </summary>
    private static DateTimeOffset? NormalizarFechaInicio(DateTime? fechaInicio)
    {
        if (!fechaInicio.HasValue)
            return null;

        var dia = fechaInicio.Value.Date;

        if (dia <= DateTime.Today)
            return null;

        return new DateTimeOffset(
            dia.AddHours(12),
            TimeZoneInfo.Local.GetUtcOffset(dia.AddHours(12)));
    }

    private string ArmarConcepto(string nombreCliente)
    {
        var concepto = $"TECNISEGUR ALARMAS - {nombreCliente.Trim()}";

        // MercadoPago trunca los conceptos largos; mejor controlarlo acá.
        return concepto.Length > 255 ? concepto[..255] : concepto;
    }

    /// <summary>
    /// Devuelve null —no cadena vacía— cuando no hay BackUrl configurada, para
    /// que el campo se omita del JSON. MercadoPago valida back_url como URL y
    /// responde 400 si recibe "".
    /// </summary>
    private string? ArmarBackUrl(int idCotizacion)
    {
        if (string.IsNullOrWhiteSpace(_opciones.BackUrl))
        {
            _log.LogWarning(
                "No hay MercadoPago:BackUrl configurada. La suscripción se crea " +
                "igual, pero el cliente no vuelve a ningún lado tras autorizar.");

            return null;
        }

        var separador = _opciones.BackUrl.Contains('?') ? "&" : "?";
        return $"{_opciones.BackUrl}{separador}cotizacion={idCotizacion}";
    }

    private async Task CancelarEnMercadoPagoSilencioso(
        string preapprovalId, CancellationToken ct)
    {
        try
        {
            await _mercadoPago.CancelarSuscripcionAsync(preapprovalId, ct);
        }
        catch (Exception ex)
        {
            // Queda registrado para limpieza manual: no vale la pena romper
            // la respuesta al usuario por esto.
            _log.LogError(ex,
                "No se pudo cancelar la suscripción huérfana {Preapproval}.",
                preapprovalId);
        }
    }
}
