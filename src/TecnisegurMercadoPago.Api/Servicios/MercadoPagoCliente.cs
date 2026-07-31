using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TecnisegurMercadoPago.Api.Configuracion;
using TecnisegurMercadoPago.Api.Modelos.MercadoPago;

namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Error devuelto por la API de MercadoPago, con el cuerpo original para poder
/// diagnosticar sin tener que reproducir la llamada.
/// </summary>
public sealed class MercadoPagoException : Exception
{
    public int CodigoHttp { get; }
    public string? CuerpoRespuesta { get; }

    public MercadoPagoException(string mensaje, int codigoHttp, string? cuerpo)
        : base(mensaje)
    {
        CodigoHttp = codigoHttp;
        CuerpoRespuesta = cuerpo;
    }
}

/// <summary>
/// Datos de acreditación de un pago: cuándo MercadoPago libera el dinero y
/// cuánto queda después de la comisión.
///
/// Todos los campos son nullables porque MercadoPago puede no informarlos —y
/// porque null significa "no se sabe", que no es lo mismo que cero.
/// </summary>
public sealed record DatosLiberacion(
    DateTime? FechaLiberacion,
    decimal? MontoNeto,
    decimal? Comision,
    decimal? Retenciones = null,
    string? Estado = null,
    string? DetalleEstado = null,
    string? EstadoLiberacionMp = null)
{
    public static readonly DatosLiberacion Vacio = new(null, null, null);

    public bool HayDatos =>
        FechaLiberacion is not null ||
        MontoNeto is not null ||
        EstadoLiberacionMp is not null;

    /// <summary>
    /// MercadoPago informó que el dinero está liberado. No se infiere de la
    /// fecha: es lo que dice money_release_status.
    /// </summary>
    public bool Liberado => EstadoLiberacionMp == "released";
}

/// <summary>
/// Cliente HTTP tipado contra la API de MercadoPago.
/// Se registra con AddHttpClient, de modo que el HttpMessageHandler se reutiliza
/// (no se instancia un HttpClient por llamada, que agota sockets).
/// </summary>
public sealed class MercadoPagoCliente
{
    private readonly HttpClient _http;
    private readonly MercadoPagoOpciones _opciones;
    private readonly ILogger<MercadoPagoCliente> _log;

    private static readonly JsonSerializerOptions JsonOpciones = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public MercadoPagoCliente(
        HttpClient http,
        IOptions<MercadoPagoOpciones> opciones,
        ILogger<MercadoPagoCliente> log)
    {
        _http = http;
        _opciones = opciones.Value;
        _log = log;
    }

    /// <summary>
    /// Crea una suscripción sin plan asociado y devuelve el init_point.
    /// <paramref name="claveIdempotencia"/> evita que un doble click genere
    /// dos suscripciones (y por lo tanto dos cobros al cliente).
    /// </summary>
    public Task<PreapprovalRespuesta> CrearSuscripcionAsync(
        PreapprovalSolicitud solicitud,
        string claveIdempotencia,
        CancellationToken ct = default)
    {
        return EnviarAsync<PreapprovalRespuesta>(
            HttpMethod.Post,
            "/preapproval",
            solicitud,
            claveIdempotencia,
            ct);
    }

    public Task<PreapprovalRespuesta> ObtenerSuscripcionAsync(
        string preapprovalId,
        CancellationToken ct = default)
    {
        return EnviarAsync<PreapprovalRespuesta>(
            HttpMethod.Get,
            $"/preapproval/{preapprovalId}",
            cuerpo: null,
            claveIdempotencia: null,
            ct);
    }

    /// <summary>
    /// Cambia el importe de una suscripción ya autorizada.
    /// Es la salida cuando se edita una cotización que ya tiene suscripción viva.
    /// </summary>
    public Task<PreapprovalRespuesta> ActualizarMontoAsync(
        string preapprovalId,
        decimal montoMensual,
        CancellationToken ct = default)
    {
        var cuerpo = new PreapprovalActualizacion
        {
            AutoRecurring = new AutoRecurringMonto
            {
                TransactionAmount = montoMensual,
                CurrencyId = _opciones.Moneda
            }
        };

        return EnviarAsync<PreapprovalRespuesta>(
            HttpMethod.Put,
            $"/preapproval/{preapprovalId}",
            cuerpo,
            claveIdempotencia: null,
            ct);
    }

    public Task<PreapprovalRespuesta> CancelarSuscripcionAsync(
        string preapprovalId,
        CancellationToken ct = default)
    {
        var cuerpo = new PreapprovalActualizacion { Status = "cancelled" };

        return EnviarAsync<PreapprovalRespuesta>(
            HttpMethod.Put,
            $"/preapproval/{preapprovalId}",
            cuerpo,
            claveIdempotencia: null,
            ct);
    }

    public Task<PagoAutorizadoRespuesta> ObtenerPagoAutorizadoAsync(
        string authorizedPaymentId,
        CancellationToken ct = default)
    {
        return EnviarAsync<PagoAutorizadoRespuesta>(
            HttpMethod.Get,
            $"/authorized_payments/{authorizedPaymentId}",
            cuerpo: null,
            claveIdempotencia: null,
            ct);
    }

    /// <summary>
    /// Todas las cuotas de una suscripción. Se usa al sincronizar para
    /// recuperar los cobros cuyas notificaciones no llegaron (por ejemplo,
    /// los ocurridos antes de configurar el webhook).
    /// </summary>
    public Task<BusquedaPagosAutorizados> BuscarPagosAutorizadosAsync(
        string preapprovalId,
        CancellationToken ct = default)
    {
        return EnviarAsync<BusquedaPagosAutorizados>(
            HttpMethod.Get,
            $"/authorized_payments/search?preapproval_id={preapprovalId}",
            cuerpo: null,
            claveIdempotencia: null,
            ct);
    }

    /// <summary>
    /// Crea una preferencia de Checkout Pro y devuelve el init_point del cobro
    /// único. Es la vía para el equipamiento, que la suscripción no cubre.
    /// </summary>
    public Task<PreferenciaRespuesta> CrearPreferenciaAsync(
        PreferenciaSolicitud solicitud,
        string claveIdempotencia,
        CancellationToken ct = default)
    {
        return EnviarAsync<PreferenciaRespuesta>(
            HttpMethod.Post,
            "/checkout/preferences",
            solicitud,
            claveIdempotencia,
            ct);
    }

    /// <summary>
    /// Detalle de un pago. El webhook de tipo "payment" sólo trae el id: el
    /// external_reference —y por lo tanto la cotización— aparece acá.
    /// </summary>
    public Task<PagoRespuesta> ObtenerPagoAsync(
        string pagoId,
        CancellationToken ct = default)
    {
        return EnviarAsync<PagoRespuesta>(
            HttpMethod.Get,
            $"/v1/payments/{pagoId}",
            cuerpo: null,
            claveIdempotencia: null,
            ct);
    }

    /// <summary>
    /// Cuándo se libera el dinero de un pago y cuánto entra neto.
    ///
    /// Existe como método aparte porque la cuota de una suscripción NO trae
    /// estos datos: GET /authorized_payments/{id} devuelve un "payment" anidado
    /// con id, status y status_detail nada más. El importe neto y la fecha de
    /// liberación sólo están en el pago completo, así que hay que ir a buscarlo.
    ///
    /// Nunca lanza. Un fallo acá no puede invalidar el registro de una cuota que
    /// ya se cobró: se devuelve <see cref="DatosLiberacion.Vacio"/>, las columnas
    /// quedan en null y el repaso periódico las completa después.
    /// </summary>
    public async Task<DatosLiberacion> ObtenerDatosLiberacionAsync(
        string? pagoId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pagoId)) return DatosLiberacion.Vacio;

        try
        {
            var pago = await ObtenerPagoAsync(pagoId, ct);

            return new DatosLiberacion(
                pago.MoneyReleaseDate?.LocalDateTime,
                pago.TransactionDetails?.NetReceivedAmount,
                pago.ComisionCalculada,
                pago.RetencionesCalculadas,
                pago.Status,
                pago.StatusDetail,
                pago.MoneyReleaseStatus);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "No se pudieron obtener los datos de liberación del pago {Id}. " +
                "Las columnas quedan sin completar.", pagoId);

            return DatosLiberacion.Vacio;
        }
    }

    private async Task<T> EnviarAsync<T>(
        HttpMethod metodo,
        string ruta,
        object? cuerpo,
        string? claveIdempotencia,
        CancellationToken ct)
    {
        using var peticion = new HttpRequestMessage(metodo, ruta);

        peticion.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _opciones.AccessToken);

        if (!string.IsNullOrWhiteSpace(claveIdempotencia))
        {
            peticion.Headers.TryAddWithoutValidation(
                "X-Idempotency-Key", claveIdempotencia);
        }

        /* El JSON se arma aparte de la request para poder mostrarlo en el log
           cuando MercadoPago rechaza: sus 400 dicen qué campo está mal pero no
           qué se mandó, y sin el cuerpo original hay que adivinar. */
        string? cuerpoJson = null;

        if (cuerpo is not null)
        {
            cuerpoJson = JsonSerializer.Serialize(cuerpo, cuerpo.GetType(), JsonOpciones);

            peticion.Content = new StringContent(
                cuerpoJson, Encoding.UTF8, "application/json");
        }

        using var respuesta = await _http.SendAsync(peticion, ct);
        var texto = await respuesta.Content.ReadAsStringAsync(ct);

        if (!respuesta.IsSuccessStatusCode)
        {
            _log.LogError(
                "MercadoPago {Metodo} {Ruta} devolvió {Codigo}: {Cuerpo}. Se envió: {Enviado}",
                metodo, ruta, (int)respuesta.StatusCode, texto, Resumir(cuerpoJson));

            /* El motivo va en el mensaje de la excepción, no sólo en el log:
             * si queda únicamente en una línea suelta del log es muy fácil
             * perderlo y quedarse con un "400" sin explicación. */
            throw new MercadoPagoException(
                $"MercadoPago devolvió {(int)respuesta.StatusCode} en {metodo} {ruta}. " +
                $"Respuesta: {Resumir(texto)}",
                (int)respuesta.StatusCode,
                texto);
        }

        var resultado = JsonSerializer.Deserialize<T>(texto, JsonOpciones);

        if (resultado is null)
        {
            throw new MercadoPagoException(
                $"MercadoPago devolvió un cuerpo vacío en {metodo} {ruta}.",
                (int)respuesta.StatusCode,
                texto);
        }

        return resultado;
    }

    private static string Resumir(string? cuerpo)
    {
        if (string.IsNullOrWhiteSpace(cuerpo))
            return "(sin cuerpo)";

        var limpio = cuerpo.Replace("\r", " ").Replace("\n", " ").Trim();

        return limpio.Length > 600 ? limpio[..600] + "…" : limpio;
    }
}
