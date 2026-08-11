using Microsoft.AspNetCore.Mvc;
using TecnisegurMercadoPago.Api.Modelos.Contratos;
using TecnisegurMercadoPago.Api.Servicios;

namespace TecnisegurMercadoPago.Api.Controllers;

/// <summary>
/// Endpoints consumidos por EmpleadoWeb y por el form de TSD Desktop.
/// Protegidos por X-Api-Key (ver ApiKeyMiddleware).
/// </summary>
[ApiController]
[Route("api/suscripciones")]
[Produces("application/json")]
public sealed class SuscripcionesController : ControllerBase
{
    private readonly SuscripcionServicio _servicio;
    private readonly EnvioWhatsAppServicio _whatsApp;
    private readonly ILogger<SuscripcionesController> _log;

    public SuscripcionesController(
        SuscripcionServicio servicio,
        EnvioWhatsAppServicio whatsApp,
        ILogger<SuscripcionesController> log)
    {
        _servicio = servicio;
        _whatsApp = whatsApp;
        _log = log;
    }

    /// <summary>
    /// Crea la suscripción en MercadoPago y devuelve el link (init_point)
    /// que hay que enviarle al cliente.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CrearSuscripcionRespuesta), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ErrorRespuesta), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Crear(
        [FromBody] CrearSuscripcionSolicitud solicitud, CancellationToken ct)
    {
        try
        {
            solicitud.Origen ??= HttpContext.Items["SistemaLlamador"] as string;

            var resultado = await _servicio.CrearAsync(solicitud, ct);

            await IntentarEnviarWhatsAppAsync(resultado, solicitud.Telefono, ct);

            return CreatedAtAction(
                nameof(Obtener),
                new { idSuscripcion = resultado.IdSuscripcion },
                resultado);
        }
        catch (ReglaNegocioException ex)
        {
            return Conflict(new ErrorRespuesta(ex.Message));
        }
        catch (MercadoPagoException ex)
        {
            _log.LogError(ex,
                "MercadoPago rechazó el alta de la cotización {Id}.",
                solicitud.IdCotizacion);

            return StatusCode(StatusCodes.Status502BadGateway,
                new ErrorRespuesta(
                    "MercadoPago rechazó la creación de la suscripción.",
                    ex.CuerpoRespuesta));
        }
    }

    [HttpGet("{idSuscripcion:int}")]
    [ProducesResponseType(typeof(SuscripcionEstadoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorRespuesta), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obtener(int idSuscripcion, CancellationToken ct)
    {
        var suscripcion = await _servicio.ObtenerAsync(idSuscripcion, ct);

        return suscripcion is null
            ? NotFound(new ErrorRespuesta($"No existe la suscripción {idSuscripcion}."))
            : Ok(suscripcion);
    }

    /// <summary>
    /// Estado de las suscripciones de una cotización. Es el endpoint que
    /// consume el listado de cotizaciones para mostrar Pendiente/Activa/Cancelada.
    /// </summary>
    [HttpGet("cotizacion/{idCotizacion:int}")]
    [ProducesResponseType(typeof(List<SuscripcionEstadoDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarPorCotizacion(
        int idCotizacion, CancellationToken ct)
    {
        return Ok(await _servicio.ListarPorCotizacionAsync(idCotizacion, ct));
    }

    /// <summary>
    /// Cambia el importe mensual. Hay que llamarlo cuando se edita una
    /// cotización que ya tiene suscripción viva: si no, MercadoPago sigue
    /// cobrando el importe original.
    /// </summary>
    [HttpPut("{idSuscripcion:int}/monto")]
    [ProducesResponseType(typeof(SuscripcionEstadoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorRespuesta), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ActualizarMonto(
        int idSuscripcion,
        [FromBody] ActualizarMontoSolicitud solicitud,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _servicio.ActualizarMontoAsync(
                idSuscripcion, solicitud.MontoMensual, ct));
        }
        catch (ReglaNegocioException ex)
        {
            return Conflict(new ErrorRespuesta(ex.Message));
        }
        catch (MercadoPagoException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new ErrorRespuesta(
                    "MercadoPago rechazó el cambio de importe.", ex.CuerpoRespuesta));
        }
    }

    [HttpPost("{idSuscripcion:int}/cancelar")]
    [ProducesResponseType(typeof(SuscripcionEstadoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorRespuesta), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancelar(
        int idSuscripcion,
        [FromBody] CancelarSuscripcionSolicitud? solicitud,
        CancellationToken ct)
    {
        try
        {
            /* Qué sistema pidió la baja. Va al rastro de auditoría: una
               cancelación sin origen identificable es exactamente lo que costó
               días de diagnóstico. */
            var origen = HttpContext.Items["SistemaLlamador"] as string;

            return Ok(await _servicio.CancelarAsync(
                idSuscripcion, solicitud?.Motivo, origen, ct));
        }
        catch (ReglaNegocioException ex)
        {
            return Conflict(new ErrorRespuesta(ex.Message));
        }
        catch (MercadoPagoException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new ErrorRespuesta(
                    "MercadoPago rechazó la cancelación.", ex.CuerpoRespuesta));
        }
    }

    /// <summary>
    /// Envío automático al crear. Es "mejor esfuerzo" por una razón concreta:
    /// cuando se llega acá la suscripción YA existe en MercadoPago. Propagar un
    /// fallo de Twilio como error del alta le devolvería al usuario un mensaje
    /// de fracaso junto con una suscripción viva y sin link a la vista, que es
    /// la peor combinación posible.
    ///
    /// El resultado viaja en la respuesta para que la interfaz pueda avisar y
    /// ofrecer el reenvío manual.
    /// </summary>
    private async Task IntentarEnviarWhatsAppAsync(
        CrearSuscripcionRespuesta resultado, string? telefono, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(telefono)) return;

        try
        {
            await _whatsApp.EnviarLinkSuscripcionAsync(
                resultado.IdSuscripcion,
                new EnviarWhatsAppSolicitud { Telefono = telefono },
                ct);

            resultado.WhatsAppEnviado = true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Suscripción {Id} creada pero no se pudo enviar el WhatsApp.",
                resultado.IdSuscripcion);

            resultado.WhatsAppEnviado = false;
            resultado.WhatsAppMensaje = ex.Message;
        }
    }

    /// <summary>
    /// Manda el link de adhesión al cliente por WhatsApp.
    ///
    /// Devuelve 409 si Twilio no está configurado: es una condición de negocio
    /// esperable, no un fallo del servicio. El cobro sigue funcionando igual y
    /// el link se puede enviar por otro canal.
    /// </summary>
    [HttpPost("{idSuscripcion:int}/enviar-whatsapp")]
    [ProducesResponseType(typeof(EnviarWhatsAppRespuesta), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorRespuesta), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EnviarWhatsApp(
        int idSuscripcion,
        [FromBody] EnviarWhatsAppSolicitud solicitud,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _whatsApp.EnviarLinkSuscripcionAsync(
                idSuscripcion, solicitud, ct));
        }
        catch (ReglaNegocioException ex)
        {
            return Conflict(new ErrorRespuesta(ex.Message));
        }
        catch (TwilioException ex)
        {
            _log.LogError(ex,
                "Twilio rechazó el envío de la suscripción {Id}.", idSuscripcion);

            return StatusCode(StatusCodes.Status502BadGateway,
                new ErrorRespuesta(
                    "Twilio rechazó el envío del WhatsApp.", ex.CuerpoRespuesta));
        }
    }

    /// <summary>
    /// Fuerza una relectura del estado en MercadoPago.
    /// Red de seguridad por si se perdió alguna notificación.
    /// </summary>
    [HttpPost("{idSuscripcion:int}/sincronizar")]
    [ProducesResponseType(typeof(SuscripcionEstadoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorRespuesta), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Sincronizar(int idSuscripcion, CancellationToken ct)
    {
        var suscripcion = await _servicio.SincronizarAsync(idSuscripcion, ct);

        return suscripcion is null
            ? NotFound(new ErrorRespuesta($"No existe la suscripción {idSuscripcion}."))
            : Ok(suscripcion);
    }
}
