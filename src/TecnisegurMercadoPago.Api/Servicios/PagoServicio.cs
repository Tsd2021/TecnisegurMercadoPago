using System.Text.Json;
using Microsoft.Extensions.Options;
using TecnisegurMercadoPago.Api.Configuracion;
using TecnisegurMercadoPago.Api.Datos;
using TecnisegurMercadoPago.Api.Modelos.Contratos;
using TecnisegurMercadoPago.Api.Modelos.MercadoPago;

namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Cobros únicos por Checkout Pro: el equipamiento o los insumos que el cliente
/// paga de una sola vez.
///
/// La suscripción cobra Cotizacion.TotalServiciosMensual todos los meses; esto
/// cobra Cotizacion.TotalProductos una vez. Son dos links distintos y ninguno
/// reemplaza al otro.
/// </summary>
public sealed class PagoServicio
{
    private readonly MercadoPagoCliente _mercadoPago;
    private readonly PagoRepositorio _repositorio;
    private readonly MercadoPagoOpciones _opciones;
    private readonly ILogger<PagoServicio> _log;

    public PagoServicio(
        MercadoPagoCliente mercadoPago,
        PagoRepositorio repositorio,
        IOptions<MercadoPagoOpciones> opciones,
        ILogger<PagoServicio> log)
    {
        _mercadoPago = mercadoPago;
        _repositorio = repositorio;
        _opciones = opciones.Value;
        _log = log;
    }

    public async Task<CrearPagoRespuesta> CrearAsync(
        CrearPagoSolicitud solicitud, CancellationToken ct = default)
    {
        /* -------------------------------------------------------------------
         * 1) Reservar la fila local. El índice único filtrado sobre
         *    (IdCotizacion) WHERE Estado = 'pendiente' es lo que impide que un
         *    doble click genere dos links de pago para el mismo cobro.
         * ------------------------------------------------------------------- */
        var reserva = await _repositorio.ReservarAsync(
            solicitud.IdCotizacion,
            solicitud.NombreCliente.Trim(),
            solicitud.PayerEmail.Trim(),
            solicitud.Monto,
            _opciones.Moneda,
            solicitud.Concepto?.Trim(),
            solicitud.UsuarioCreacion,
            solicitud.Origen,
            ct);

        if (reserva is null)
        {
            throw new ReglaNegocioException(
                $"La cotización {solicitud.IdCotizacion} ya tiene un pago " +
                "pendiente sin resolver. Cancélelo o espere a que se acredite " +
                "antes de generar otro link.");
        }

        var concepto = ArmarConcepto(solicitud);

        var preferencia = new PreferenciaSolicitud
        {
            ExternalReference = reserva.ExternalReference,
            NotificationUrl = string.IsNullOrWhiteSpace(_opciones.UrlWebhook)
                ? null
                : _opciones.UrlWebhook,
            StatementDescriptor = "TECNISEGUR",
            Payer = new PreferenciaPagador
            {
                Email = solicitud.PayerEmail.Trim(),
                Name = solicitud.NombreCliente.Trim(),
                Identification = ArmarIdentificacion(solicitud.Documento),
                Phone = ArmarTelefono(solicitud.Telefono)
            },
            Items =
            {
                new PreferenciaItem
                {
                    Title = concepto,
                    Quantity = 1,
                    UnitPrice = solicitud.Monto,
                    CurrencyId = _opciones.Moneda
                }
            }
        };

        AplicarBackUrls(preferencia, solicitud.IdCotizacion);

        /* -------------------------------------------------------------------
         * 2) Crear la preferencia. Si falla se libera la reserva: de lo
         *    contrario la fila 'pendiente' quedaría bloqueando el índice y no
         *    se podría reintentar nunca.
         * ------------------------------------------------------------------- */
        PreferenciaRespuesta respuesta;

        try
        {
            respuesta = await _mercadoPago.CrearPreferenciaAsync(
                preferencia, reserva.ExternalReference, ct);
        }
        catch
        {
            await _repositorio.LiberarReservaAsync(reserva.Id, ct);
            throw;
        }

        if (string.IsNullOrWhiteSpace(respuesta.Id) ||
            string.IsNullOrWhiteSpace(respuesta.InitPoint))
        {
            await _repositorio.LiberarReservaAsync(reserva.Id, ct);

            throw new InvalidOperationException(
                "MercadoPago no devolvió id o init_point para la preferencia.");
        }

        await _repositorio.AsignarPreferenciaAsync(
            reserva.Id, respuesta.Id, respuesta.InitPoint, ct);

        _log.LogInformation(
            "Pago único {Id} creado para la cotización {Cotizacion} " +
            "({Referencia}, preferencia {Preferencia}).",
            reserva.Id, solicitud.IdCotizacion,
            reserva.ExternalReference, respuesta.Id);

        return new CrearPagoRespuesta
        {
            IdPago = reserva.Id,
            IdCotizacion = solicitud.IdCotizacion,
            ExternalReference = reserva.ExternalReference,
            MpPreferenceId = respuesta.Id,
            InitPoint = respuesta.InitPoint,
            Estado = "pendiente",
            Monto = solicitud.Monto,
            Moneda = _opciones.Moneda
        };
    }

    public Task<PagoUnicoDto?> ObtenerAsync(int idPago, CancellationToken ct = default)
        => _repositorio.ObtenerPorIdAsync(idPago, ct);

    public Task<List<PagoUnicoDto>> ListarPorCotizacionAsync(
        int idCotizacion, CancellationToken ct = default)
        => _repositorio.ListarPorCotizacionAsync(idCotizacion, ct);

    /// <summary>
    /// Registra el resultado de un pago avisado por webhook.
    ///
    /// Descarta los pagos que pertenecen a una suscripción: esas cuotas llegan
    /// además como subscription_authorized_payment y las registra
    /// ProcesadorNotificaciones en SuscripcionPago. Tomarlas también acá
    /// contaría el mismo cobro dos veces.
    /// </summary>
    public async Task RegistrarPagoNotificadoAsync(
        string pagoId, CancellationToken ct = default)
    {
        var pago = await _mercadoPago.ObtenerPagoAsync(pagoId, ct);

        if (!string.IsNullOrWhiteSpace(pago.PreapprovalId))
        {
            _log.LogDebug(
                "Pago {Id} pertenece a la suscripción {Preapproval}: " +
                "lo maneja subscription_authorized_payment.",
                pagoId, pago.PreapprovalId);

            return;
        }

        if (string.IsNullOrWhiteSpace(pago.ExternalReference))
        {
            _log.LogWarning(
                "Pago {Id} sin external_reference: no se puede conciliar.", pagoId);
            return;
        }

        /* Las suscripciones usan el prefijo COT-; los cobros únicos, PAGO-.
           Sin este filtro, un pago de suscripción que llegara sin
           preapproval_id se buscaría inútilmente en PagoUnico. */
        if (!pago.ExternalReference.StartsWith("PAGO-", StringComparison.OrdinalIgnoreCase))
        {
            _log.LogDebug(
                "Pago {Id} con referencia {Referencia}: no es un cobro único.",
                pagoId, pago.ExternalReference);

            return;
        }

        var registrado = await _repositorio.RegistrarResultadoAsync(
            pago.ExternalReference,
            pagoId,
            pago.Status ?? "desconocido",
            pago.StatusDetail,
            pago.TransactionAmount,
            pago.CurrencyId,
            pago.Status == "approved"
                ? (pago.DateApproved ?? pago.DateCreated)?.LocalDateTime
                : null,
            JsonSerializer.Serialize(pago),
            pago.MoneyReleaseDate?.LocalDateTime,
            pago.TransactionDetails?.NetReceivedAmount,
            pago.ComisionCalculada,
            pago.RetencionesCalculadas,
            pago.MoneyReleaseStatus,
            ct);

        if (!registrado)
        {
            _log.LogWarning(
                "Pago {Id} con referencia {Referencia} sin fila local. " +
                "¿Se creó fuera del sistema?",
                pagoId, pago.ExternalReference);

            return;
        }

        _log.LogInformation(
            "Pago único {Referencia} actualizado a {Estado} ({Detalle}).",
            pago.ExternalReference, pago.Status, pago.StatusDetail);
    }

    private static string ArmarConcepto(CrearPagoSolicitud solicitud)
    {
        var concepto = string.IsNullOrWhiteSpace(solicitud.Concepto)
            ? $"Equipamiento e instalación - {solicitud.NombreCliente.Trim()}"
            : solicitud.Concepto.Trim();

        // MercadoPago trunca los títulos largos; mejor controlarlo acá.
        return concepto.Length > 255 ? concepto[..255] : concepto;
    }

    /// <summary>
    /// La cédula se manda sólo en dígitos: en el contrato se carga a mano y
    /// suele venir con puntos y guión ("1.234.567-8"), formato que MercadoPago
    /// no acepta. Si no queda ningún dígito se devuelve null y el campo se
    /// omite — precargar el checkout con basura es peor que no precargarlo.
    /// </summary>
    private static PreferenciaIdentificacion? ArmarIdentificacion(string? documento)
    {
        var digitos = SoloDigitos(documento);

        return string.IsNullOrEmpty(digitos)
            ? null
            : new PreferenciaIdentificacion { Type = "CI", Number = digitos };
    }

    /// <summary>
    /// El teléfono va sin código de área: el que se carga en el contrato es un
    /// celular uruguayo ("099 123 456") donde el 099 es parte del número, no un
    /// área separable.
    /// </summary>
    private static PreferenciaTelefono? ArmarTelefono(string? telefono)
    {
        var digitos = SoloDigitos(telefono);

        return string.IsNullOrEmpty(digitos)
            ? null
            : new PreferenciaTelefono { Number = digitos };
    }

    private static string SoloDigitos(string? valor)
        => string.IsNullOrWhiteSpace(valor)
            ? string.Empty
            : new string(valor.Where(char.IsDigit).ToArray());

    /// <summary>
    /// auto_return sólo se manda junto con back_urls.success: MercadoPago
    /// responde 400 si recibe uno sin el otro. Sin BackUrl configurada se
    /// omiten los dos y el cliente simplemente se queda en MercadoPago.
    /// </summary>
    private void AplicarBackUrls(PreferenciaSolicitud preferencia, int idCotizacion)
    {
        var url = UrlRetorno.ConCotizacion(_opciones.BackUrl, idCotizacion);

        if (url is null)
        {
            _log.LogWarning(
                "MercadoPago:BackUrl ausente o no absoluta ('{BackUrl}'). El link " +
                "de pago se crea igual, pero el cliente no vuelve a ningún lado " +
                "tras pagar.",
                _opciones.BackUrl);

            return;
        }

        preferencia.BackUrls = new PreferenciaBackUrls
        {
            Success = url,
            Pending = url,
            Failure = url
        };

        preferencia.AutoReturn = "approved";
    }
}
