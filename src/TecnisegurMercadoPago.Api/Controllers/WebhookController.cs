using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TecnisegurMercadoPago.Api.Datos;
using TecnisegurMercadoPago.Api.Modelos.MercadoPago;
using TecnisegurMercadoPago.Api.Seguridad;

namespace TecnisegurMercadoPago.Api.Controllers;

/// <summary>
/// Receptor de notificaciones de MercadoPago.
///
/// Endpoint PÚBLICO y anónimo — está excluido del ApiKeyMiddleware porque
/// MercadoPago no puede enviar la clave. Se protege validando la firma
/// x-signature.
///
/// Regla de oro: guardar y responder 200 lo antes posible. MercadoPago corta
/// a los 22 segundos y reintenta cada 15 minutos. Todo el trabajo real lo hace
/// <see cref="Servicios.ProcesadorNotificaciones"/> en segundo plano.
/// </summary>
[ApiController]
[Route("api/webhook")]
public sealed class WebhookController : ControllerBase
{
    private readonly NotificacionRepositorio _repositorio;
    private readonly ValidadorFirmaWebhook _validador;
    private readonly ILogger<WebhookController> _log;

    private static readonly JsonSerializerOptions JsonOpciones =
        new() { PropertyNameCaseInsensitive = true };

    public WebhookController(
        NotificacionRepositorio repositorio,
        ValidadorFirmaWebhook validador,
        ILogger<WebhookController> log)
    {
        _repositorio = repositorio;
        _validador = validador;
        _log = log;
    }

    [HttpPost]
    public async Task<IActionResult> Recibir(CancellationToken ct)
    {
        string cuerpo;

        using (var lector = new StreamReader(Request.Body))
        {
            cuerpo = await lector.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(cuerpo))
        {
            _log.LogWarning("Notificación con cuerpo vacío.");
            return Ok();
        }

        NotificacionWebhook? notificacion;

        try
        {
            notificacion = JsonSerializer.Deserialize<NotificacionWebhook>(
                cuerpo, JsonOpciones);
        }
        catch (JsonException ex)
        {
            // Se responde 200 igual: reintentar no va a arreglar un JSON inválido.
            _log.LogError(ex, "Notificación con JSON inválido: {Cuerpo}", cuerpo);
            return Ok();
        }

        if (notificacion is null)
        {
            return Ok();
        }

        /* ---------------------------------------------------------------
         * IPN viejo: se descarta sin persistir.
         *
         * MercadoPago manda DOS notificaciones por el mismo evento: el
         * webhook moderno —{"type":"payment","data":{"id":"..."}}— y el IPN
         * heredado —{"topic":"payment","resource":"..."}—, más un
         * merchant_order que no usamos. Verificado el 30/07/2026: un solo
         * pago generó las notificaciones 47, 48 y 49.
         *
         * El IPN viejo no viaja firmado —el manifiesto x-signature se arma
         * sobre data.id, que este formato no trae—, así que siempre quedaba
         * con FirmaValida = 0. Guardarlo acumulaba unas dos filas basura por
         * cada pago real y, peor, escondía una firma inválida de verdad —un
         * intento de suplantación— entre el ruido esperable.
         *
         * No se pierde nada: el evento ya llega por el webhook moderno, que
         * es el único formato que ProcesadorNotificaciones sabe manejar.
         * --------------------------------------------------------------- */
        if (string.IsNullOrWhiteSpace(notificacion.Type) &&
            !string.IsNullOrWhiteSpace(notificacion.Topic))
        {
            _log.LogDebug(
                "IPN heredado descartado (topic {Topic}). El evento llega " +
                "igual por el webhook moderno.",
                notificacion.Topic);

            return Ok();
        }

        /* ---------------------------------------------------------------
         * Validación de firma. Si no es válida se registra igual (con
         * FirmaValida = 0) para poder auditar intentos de suplantación,
         * pero el procesador nunca la va a tomar.
         * --------------------------------------------------------------- */
        var xSignature = Request.Headers["x-signature"].FirstOrDefault();
        var xRequestId = Request.Headers["x-request-id"].FirstOrDefault();

        // MercadoPago firma el data.id, que también viaja por query string.
        var dataId = Request.Query["data.id"].FirstOrDefault()
                     ?? notificacion.Data?.Id;

        var firmaValida = _validador.EsValida(xSignature, xRequestId, dataId);

        if (!firmaValida)
        {
            _log.LogWarning(
                "Firma inválida para la notificación {Id} desde {IP}.",
                notificacion.Id, HttpContext.Connection.RemoteIpAddress);
        }

        var mpNotificationId = notificacion.Id?.ToString();

        if (string.IsNullOrWhiteSpace(mpNotificationId))
        {
            // Sin id no hay clave de idempotencia: se compone una estable
            // a partir del tipo y el dato afectado.
            mpNotificationId = $"{notificacion.TipoEfectivo}-{dataId}";
        }

        try
        {
            var esNueva = await _repositorio.GuardarAsync(
                mpNotificationId,
                notificacion.TipoEfectivo,
                notificacion.Action,
                dataId,
                cuerpo,
                firmaValida,
                ct);

            if (!esNueva)
            {
                _log.LogInformation(
                    "Notificación {Id} ya registrada: se ignora.", mpNotificationId);
            }
        }
        catch (Exception ex)
        {
            /* Si falla la persistencia SÍ conviene devolver error: así
             * MercadoPago reintenta y no se pierde la notificación. */
            _log.LogError(ex,
                "No se pudo registrar la notificación {Id}.", mpNotificationId);

            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        return Ok();
    }

    /// <summary>
    /// MercadoPago valida la URL con un GET al configurarla en el panel.
    /// </summary>
    [HttpGet]
    public IActionResult Verificar() => Ok(new { estado = "activo" });
}
