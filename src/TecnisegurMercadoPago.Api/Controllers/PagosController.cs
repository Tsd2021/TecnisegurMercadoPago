using Microsoft.AspNetCore.Mvc;
using TecnisegurMercadoPago.Api.Modelos.Contratos;
using TecnisegurMercadoPago.Api.Servicios;

namespace TecnisegurMercadoPago.Api.Controllers;

/// <summary>
/// Cobros únicos (Checkout Pro), consumidos por EmpleadoWeb y TSD Desktop.
/// Protegidos por X-Api-Key (ver ApiKeyMiddleware).
///
/// Es el complemento de SuscripcionesController: la suscripción cobra la cuota
/// mensual del servicio, esto cobra el equipamiento de una sola vez.
/// </summary>
[ApiController]
[Route("api/pagos")]
[Produces("application/json")]
public sealed class PagosController : ControllerBase
{
    private readonly PagoServicio _servicio;
    private readonly EnvioWhatsAppServicio _whatsApp;
    private readonly ILogger<PagosController> _log;

    public PagosController(
        PagoServicio servicio,
        EnvioWhatsAppServicio whatsApp,
        ILogger<PagosController> log)
    {
        _servicio = servicio;
        _whatsApp = whatsApp;
        _log = log;
    }

    /// <summary>
    /// Crea la preferencia de Checkout Pro y devuelve el link (init_point)
    /// que hay que enviarle al cliente.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CrearPagoRespuesta), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorRespuesta), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Crear(
        [FromBody] CrearPagoSolicitud solicitud, CancellationToken ct)
    {
        try
        {
            solicitud.Origen ??= HttpContext.Items["SistemaLlamador"] as string;

            var resultado = await _servicio.CrearAsync(solicitud, ct);

            await IntentarEnviarWhatsAppAsync(resultado, solicitud.Telefono, ct);

            return CreatedAtAction(
                nameof(Obtener),
                new { idPago = resultado.IdPago },
                resultado);
        }
        catch (ReglaNegocioException ex)
        {
            return Conflict(new ErrorRespuesta(ex.Message));
        }
        catch (MercadoPagoException ex)
        {
            _log.LogError(ex,
                "MercadoPago rechazó el link de pago de la cotización {Id}.",
                solicitud.IdCotizacion);

            return StatusCode(StatusCodes.Status502BadGateway,
                new ErrorRespuesta(
                    "MercadoPago rechazó la creación del link de pago.",
                    ex.CuerpoRespuesta));
        }
    }

    /// <summary>
    /// Envío automático al crear, "mejor esfuerzo": la preferencia ya existe en
    /// MercadoPago, así que un fallo de Twilio no puede invalidar el alta.
    /// </summary>
    private async Task IntentarEnviarWhatsAppAsync(
        CrearPagoRespuesta resultado, string? telefono, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(telefono)) return;

        try
        {
            await _whatsApp.EnviarLinkPagoAsync(
                resultado.IdPago,
                new EnviarWhatsAppSolicitud { Telefono = telefono },
                ct);

            resultado.WhatsAppEnviado = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Pago {Id} creado pero no se pudo enviar el WhatsApp.",
                resultado.IdPago);

            resultado.WhatsAppEnviado = false;
            resultado.WhatsAppMensaje = ex.Message;
        }
    }

    [HttpGet("{idPago:int}")]
    [ProducesResponseType(typeof(PagoUnicoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorRespuesta), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obtener(int idPago, CancellationToken ct)
    {
        var pago = await _servicio.ObtenerAsync(idPago, ct);

        return pago is null
            ? NotFound(new ErrorRespuesta($"No existe el pago {idPago}."))
            : Ok(pago);
    }

    /// <summary>
    /// Todos los cobros únicos de una cotización. A diferencia de las
    /// suscripciones, puede haber varios: cada venta de insumos genera uno.
    /// </summary>
    [HttpGet("cotizacion/{idCotizacion:int}")]
    [ProducesResponseType(typeof(List<PagoUnicoDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarPorCotizacion(
        int idCotizacion, CancellationToken ct)
    {
        return Ok(await _servicio.ListarPorCotizacionAsync(idCotizacion, ct));
    }

    /// <summary>
    /// Cancela el cobro pendiente de una cotización y vence su link en
    /// MercadoPago, para que el cliente no pueda pagarlo después.
    ///
    /// Es la forma de destrabar una cotización cuyo link anterior quedó sin
    /// pagar. Crear el cobro nuevo con ReemplazarPendiente hace lo mismo en un
    /// solo paso; este endpoint sirve para cancelar sin generar nada a cambio.
    ///
    /// Devuelve 404 si no había ningún cobro pendiente: no es un error, pero el
    /// llamador tiene que poder distinguirlo de haber cancelado algo.
    /// </summary>
    [HttpPost("cotizacion/{idCotizacion:int}/cancelar-pendiente")]
    [ProducesResponseType(typeof(PagoUnicoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorRespuesta), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelarPendiente(
        int idCotizacion, CancellationToken ct)
    {
        var motivo = HttpContext.Items["SistemaLlamador"] as string;

        var cancelado = await _servicio.CancelarPendienteAsync(
            idCotizacion,
            string.IsNullOrWhiteSpace(motivo)
                ? "Cancelado a pedido"
                : $"Cancelado desde {motivo}",
            ct);

        if (cancelado is null)
        {
            return NotFound(new ErrorRespuesta(
                $"La cotización {idCotizacion} no tiene ningún cobro pendiente."));
        }

        return Ok(cancelado);
    }

    /// <summary>
    /// Manda el link de pago al cliente por WhatsApp.
    /// Devuelve 409 si Twilio no está configurado o si el pago ya se acreditó.
    /// </summary>
    [HttpPost("{idPago:int}/enviar-whatsapp")]
    [ProducesResponseType(typeof(EnviarWhatsAppRespuesta), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorRespuesta), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EnviarWhatsApp(
        int idPago,
        [FromBody] EnviarWhatsAppSolicitud solicitud,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _whatsApp.EnviarLinkPagoAsync(idPago, solicitud, ct));
        }
        catch (ReglaNegocioException ex)
        {
            return Conflict(new ErrorRespuesta(ex.Message));
        }
        catch (TwilioException ex)
        {
            _log.LogError(ex, "Twilio rechazó el envío del pago {Id}.", idPago);

            return StatusCode(StatusCodes.Status502BadGateway,
                new ErrorRespuesta(
                    "Twilio rechazó el envío del WhatsApp.", ex.CuerpoRespuesta));
        }
    }
}
