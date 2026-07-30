using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TecnisegurMercadoPago.Api.Configuracion;

namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Error devuelto por Twilio, con el cuerpo original para poder diagnosticar
/// sin reproducir la llamada. Mismo criterio que MercadoPagoException.
/// </summary>
public sealed class TwilioException : Exception
{
    public int CodigoHttp { get; }
    public string? CuerpoRespuesta { get; }

    public TwilioException(string mensaje, int codigoHttp, string? cuerpo)
        : base(mensaje)
    {
        CodigoHttp = codigoHttp;
        CuerpoRespuesta = cuerpo;
    }
}

/// <summary>
/// Envío de mensajes de WhatsApp por Twilio.
///
/// Se registra con AddHttpClient para reutilizar el HttpMessageHandler; no se
/// instancia un HttpClient por llamada, que agota sockets.
/// </summary>
public sealed class TwilioCliente
{
    private readonly HttpClient _http;
    private readonly TwilioOpciones _opciones;
    private readonly ILogger<TwilioCliente> _log;

    public TwilioCliente(
        HttpClient http,
        IOptions<TwilioOpciones> opciones,
        ILogger<TwilioCliente> log)
    {
        _http = http;
        _opciones = opciones.Value;
        _log = log;
    }

    /// <summary>
    /// Falso cuando falta configuración. Los endpoints lo consultan para
    /// devolver un mensaje claro en vez de fallar contra Twilio.
    /// </summary>
    public bool Configurado =>
        _opciones.Habilitado &&
        !string.IsNullOrWhiteSpace(_opciones.AccountSid) &&
        !string.IsNullOrWhiteSpace(_opciones.AuthToken) &&
        !string.IsNullOrWhiteSpace(_opciones.NumeroOrigen);

    public string MotivoNoConfigurado()
    {
        if (!_opciones.Habilitado)
            return "El envío por WhatsApp está deshabilitado (Twilio:Habilitado = false).";

        var faltantes = new List<string>();

        if (string.IsNullOrWhiteSpace(_opciones.AccountSid)) faltantes.Add("Twilio:AccountSid");
        if (string.IsNullOrWhiteSpace(_opciones.AuthToken)) faltantes.Add("Twilio:AuthToken");
        if (string.IsNullOrWhiteSpace(_opciones.NumeroOrigen)) faltantes.Add("Twilio:NumeroOrigen");

        return $"Falta configurar {string.Join(", ", faltantes)}.";
    }

    /// <summary>
    /// Deja el número en formato E.164 para WhatsApp.
    ///
    /// Acepta lo que el vendedor tenga cargado: "099 123 456", "+598 99 123 456",
    /// "(099) 123-456". Se queda con los dígitos y antepone el prefijo del país
    /// si hace falta.
    /// </summary>
    public string NormalizarTelefono(string telefono)
    {
        var digitos = new string(telefono.Where(char.IsDigit).ToArray());

        if (digitos.Length == 0)
            throw new ArgumentException("El teléfono no tiene dígitos.", nameof(telefono));

        var prefijo = _opciones.PrefijoPais;

        /* El 0 inicial es la marca de larga distancia nacional y no viaja en
           el formato internacional: 099 123 456 → 598 99 123 456. */
        if (digitos.StartsWith("0"))
            digitos = prefijo + digitos.TrimStart('0');
        else if (!digitos.StartsWith(prefijo))
            digitos = prefijo + digitos;

        return "+" + digitos;
    }

    /// <summary>
    /// Manda el mensaje y devuelve el SID de Twilio.
    ///
    /// Si hay <paramref name="contentSid"/> se usa la plantilla aprobada con
    /// sus variables; si no, se manda <paramref name="cuerpoLibre"/>, que sólo
    /// funciona dentro de la ventana de 24 horas o contra el sandbox.
    /// </summary>
    public async Task<string> EnviarWhatsAppAsync(
        string telefono,
        string cuerpoLibre,
        string? contentSid = null,
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken ct = default)
    {
        if (!Configurado)
            throw new InvalidOperationException(MotivoNoConfigurado());

        var destino = NormalizarTelefono(telefono);

        var campos = new Dictionary<string, string>
        {
            ["From"] = $"whatsapp:{_opciones.NumeroOrigen}",
            ["To"] = $"whatsapp:{destino}"
        };

        if (!string.IsNullOrWhiteSpace(contentSid))
        {
            campos["ContentSid"] = contentSid;

            if (variables is { Count: > 0 })
                campos["ContentVariables"] = JsonSerializer.Serialize(variables);
        }
        else
        {
            campos["Body"] = cuerpoLibre;
        }

        var ruta = $"/2010-04-01/Accounts/{_opciones.AccountSid}/Messages.json";

        using var peticion = new HttpRequestMessage(HttpMethod.Post, ruta)
        {
            Content = new FormUrlEncodedContent(campos)
        };

        var credenciales = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_opciones.AccountSid}:{_opciones.AuthToken}"));

        peticion.Headers.Authorization =
            new AuthenticationHeaderValue("Basic", credenciales);

        using var respuesta = await _http.SendAsync(peticion, ct);
        var texto = await respuesta.Content.ReadAsStringAsync(ct);

        if (!respuesta.IsSuccessStatusCode)
        {
            _log.LogError(
                "Twilio devolvió {Codigo} al enviar a {Destino}: {Cuerpo}",
                (int)respuesta.StatusCode, destino, texto);

            throw new TwilioException(
                $"Twilio devolvió {(int)respuesta.StatusCode} al enviar el WhatsApp. " +
                $"Respuesta: {Resumir(texto)}",
                (int)respuesta.StatusCode,
                texto);
        }

        var sid = LeerSid(texto);

        _log.LogInformation(
            "WhatsApp enviado a {Destino} (sid {Sid}).", destino, sid);

        return sid;
    }

    private static string LeerSid(string cuerpo)
    {
        try
        {
            using var doc = JsonDocument.Parse(cuerpo);

            return doc.RootElement.TryGetProperty("sid", out var sid)
                ? sid.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            // El mensaje ya se envió; no poder leer el sid no lo invalida.
            return string.Empty;
        }
    }

    private static string Resumir(string? cuerpo)
    {
        if (string.IsNullOrWhiteSpace(cuerpo))
            return "(sin cuerpo)";

        var limpio = cuerpo.Replace("\r", " ").Replace("\n", " ").Trim();

        return limpio.Length > 600 ? limpio[..600] + "…" : limpio;
    }
}
