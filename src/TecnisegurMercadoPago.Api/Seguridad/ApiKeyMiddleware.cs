using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TecnisegurMercadoPago.Api.Configuracion;

namespace TecnisegurMercadoPago.Api.Seguridad;

/// <summary>
/// Autenticación de los sistemas internos (EmpleadoWeb, TSD Desktop) por
/// header X-Api-Key.
///
/// El webhook queda EXCLUIDO a propósito: MercadoPago no puede enviar esta
/// clave. Ese endpoint se protege con la firma x-signature (ver
/// <see cref="ValidadorFirmaWebhook"/>), no con API key.
/// </summary>
public sealed class ApiKeyMiddleware
{
    public const string Header = "X-Api-Key";

    private static readonly string[] RutasPublicas =
    {
        "/api/webhook",
        "/health"
    };

    private readonly RequestDelegate _siguiente;
    private readonly ApiOpciones _opciones;
    private readonly ILogger<ApiKeyMiddleware> _log;

    public ApiKeyMiddleware(
        RequestDelegate siguiente,
        IOptions<ApiOpciones> opciones,
        ILogger<ApiKeyMiddleware> log)
    {
        _siguiente = siguiente;
        _opciones = opciones.Value;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext contexto)
    {
        var ruta = contexto.Request.Path.Value ?? string.Empty;

        var esPublica = RutasPublicas.Any(r =>
            ruta.StartsWith(r, StringComparison.OrdinalIgnoreCase));

        if (esPublica)
        {
            await _siguiente(contexto);
            return;
        }

        if (!contexto.Request.Headers.TryGetValue(Header, out var recibida) ||
            string.IsNullOrWhiteSpace(recibida))
        {
            await Rechazar(contexto, "Falta el header X-Api-Key.");
            return;
        }

        var sistema = IdentificarSistema(recibida.ToString());

        if (sistema is null)
        {
            _log.LogWarning(
                "X-Api-Key inválida desde {IP}.",
                contexto.Connection.RemoteIpAddress);

            await Rechazar(contexto, "La clave de API no es válida.");
            return;
        }

        contexto.Items["SistemaLlamador"] = sistema;
        await _siguiente(contexto);
    }

    /// <summary>
    /// Comparación en tiempo fijo para no filtrar información por timing.
    /// </summary>
    private string? IdentificarSistema(string clave)
    {
        var bytesRecibidos = Encoding.UTF8.GetBytes(clave);

        foreach (var (nombre, valor) in _opciones.Claves)
        {
            if (string.IsNullOrWhiteSpace(valor)) continue;

            var bytesEsperados = Encoding.UTF8.GetBytes(valor);

            if (bytesRecibidos.Length == bytesEsperados.Length &&
                CryptographicOperations.FixedTimeEquals(bytesRecibidos, bytesEsperados))
            {
                return nombre;
            }
        }

        return null;
    }

    private static async Task Rechazar(HttpContext contexto, string mensaje)
    {
        contexto.Response.StatusCode = StatusCodes.Status401Unauthorized;
        contexto.Response.ContentType = "application/json";

        await contexto.Response.WriteAsJsonAsync(new
        {
            ok = false,
            mensaje
        });
    }
}
