using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TecnisegurMercadoPago.Api.Configuracion;

namespace TecnisegurMercadoPago.Api.Seguridad;

/// <summary>
/// Valida la firma HMAC-SHA256 que MercadoPago envía en el header x-signature.
///
/// Sin esta validación cualquiera que conozca la URL del webhook puede postear
/// notificaciones falsas y marcar cuotas como pagas.
///
/// Formato del header:  x-signature: ts=1704908010,v1=618c8534...
/// Manifiesto firmado:  id:{data.id};request-id:{x-request-id};ts:{ts};
/// </summary>
public sealed class ValidadorFirmaWebhook
{
    private readonly MercadoPagoOpciones _opciones;
    private readonly ILogger<ValidadorFirmaWebhook> _log;

    public ValidadorFirmaWebhook(
        IOptions<MercadoPagoOpciones> opciones,
        ILogger<ValidadorFirmaWebhook> log)
    {
        _opciones = opciones.Value;
        _log = log;
    }

    public bool EsValida(string? xSignature, string? xRequestId, string? dataId)
    {
        if (string.IsNullOrWhiteSpace(_opciones.WebhookSecret))
        {
            _log.LogError(
                "No hay WebhookSecret configurado: se rechaza la notificación.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(xSignature))
        {
            _log.LogWarning("Notificación sin header x-signature.");
            return false;
        }

        var (ts, v1) = Descomponer(xSignature);

        if (string.IsNullOrWhiteSpace(ts) || string.IsNullOrWhiteSpace(v1))
        {
            _log.LogWarning("Header x-signature con formato inesperado: {Firma}", xSignature);
            return false;
        }

        // MercadoPago normaliza el data.id a minúsculas cuando es alfanumérico.
        var idNormalizado = dataId?.ToLowerInvariant() ?? string.Empty;

        var manifiesto = $"id:{idNormalizado};request-id:{xRequestId};ts:{ts};";

        var esperado = CalcularHmac(manifiesto, _opciones.WebhookSecret);

        var coincide = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(esperado),
            Encoding.UTF8.GetBytes(v1));

        if (!coincide)
        {
            _log.LogWarning(
                "Firma inválida. Manifiesto={Manifiesto} Esperado={Esperado} Recibido={Recibido}",
                manifiesto, esperado, v1);
        }

        return coincide;
    }

    private static (string? ts, string? v1) Descomponer(string xSignature)
    {
        string? ts = null;
        string? v1 = null;

        foreach (var parte in xSignature.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separador = parte.IndexOf('=');
            if (separador <= 0) continue;

            var clave = parte[..separador].Trim();
            var valor = parte[(separador + 1)..].Trim();

            if (clave.Equals("ts", StringComparison.OrdinalIgnoreCase)) ts = valor;
            else if (clave.Equals("v1", StringComparison.OrdinalIgnoreCase)) v1 = valor;
        }

        return (ts, v1);
    }

    private static string CalcularHmac(string mensaje, string secreto)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secreto));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(mensaje));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
