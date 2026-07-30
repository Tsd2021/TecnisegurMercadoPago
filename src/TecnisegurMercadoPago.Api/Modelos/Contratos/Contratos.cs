using System.ComponentModel.DataAnnotations;

namespace TecnisegurMercadoPago.Api.Modelos.Contratos;

/// <summary>
/// Alta de suscripción. Lo envía EmpleadoWeb o el form de TSD Desktop.
/// Los importes los calcula el sistema llamador a partir de la cotización
/// (ver CotizacionAlarmaController.Guardar).
/// </summary>
public sealed class CrearSuscripcionSolicitud
{
    [Range(1, int.MaxValue, ErrorMessage = "IdCotizacion es obligatorio.")]
    public int IdCotizacion { get; set; }

    [Required(ErrorMessage = "El correo del cliente es obligatorio.")]
    [EmailAddress(ErrorMessage = "El correo del cliente no es válido.")]
    public string PayerEmail { get; set; } = string.Empty;

    /// <summary>Equivale a Cotizacion.TotalServiciosMensual.</summary>
    [Range(0.01, 9_999_999, ErrorMessage = "El monto mensual debe ser mayor a cero.")]
    public decimal MontoMensual { get; set; }

    [Required(ErrorMessage = "El nombre del cliente es obligatorio.")]
    public string NombreCliente { get; set; } = string.Empty;

    /// <summary>
    /// Días de prueba antes de la primera cuota (free_trial).
    /// Si no se envía se aplica el valor por defecto de configuración, salvo
    /// que venga FechaInicio: son excluyentes, ver más abajo.
    /// </summary>
    [Range(0, 365, ErrorMessage = "Los días de prueba deben estar entre 0 y 365.")]
    public int? DiasPrueba { get; set; }

    /// <summary>
    /// Día de la adhesión: desde cuándo MercadoPago empieza a cobrar la cuota.
    /// Viaja como auto_recurring.start_date.
    ///
    /// EXCLUYENTE con DiasPrueba. Los dos posponen la primera cuota y
    /// MercadoPago no documenta cómo se combinan, así que mandar ambos deja el
    /// resultado librado a un comportamiento que no podemos garantizar:
    /// SuscripcionServicio rechaza la combinación en vez de adivinar.
    ///
    /// Como las cuotas siguientes caen mes a mes a partir de esta fecha, es
    /// además la única forma de controlar en qué día del mes se cobra —
    /// billing_day sólo existe en preapproval_plan, no en preapproval suelto.
    ///
    /// Null o la fecha de hoy = se cobra apenas el cliente autoriza.
    /// </summary>
    public DateTime? FechaInicio { get; set; }

    /// <summary>Derivado de ContratoCotizacionAlarma.PlazoContrato (meses).</summary>
    [Range(1, 600)]
    public int? PlazoMeses { get; set; }

    public string? UsuarioCreacion { get; set; }

    /// <summary>"WEBEMPLEADO" o "TSD".</summary>
    public string? Origen { get; set; }

    /// <summary>
    /// Si viene, el link se manda por WhatsApp apenas se crea la suscripción.
    /// Vacío = no se envía nada y el link queda para copiar a mano.
    /// </summary>
    public string? Telefono { get; set; }
}

public sealed class CrearSuscripcionRespuesta
{
    public int IdSuscripcion { get; set; }
    public int IdCotizacion { get; set; }
    public string PreapprovalId { get; set; } = string.Empty;

    /// <summary>Link que se envía al cliente para que autorice la suscripción.</summary>
    public string InitPoint { get; set; } = string.Empty;

    public string Estado { get; set; } = string.Empty;
    public decimal MontoMensual { get; set; }
    public string Moneda { get; set; } = string.Empty;
    public int? DiasPrueba { get; set; }

    /// <summary>Null = se cobra apenas el cliente autoriza.</summary>
    public DateTime? FechaInicio { get; set; }

    /// <summary>
    /// null = no se pidió envío. true/false = resultado del intento.
    ///
    /// El envío es "mejor esfuerzo" y NUNCA hace fallar el alta: cuando se
    /// llega a este punto la suscripción ya existe en MercadoPago, así que
    /// devolver un error borraría el link y dejaría el recurso huérfano.
    /// </summary>
    public bool? WhatsAppEnviado { get; set; }

    /// <summary>Motivo cuando WhatsAppEnviado es false.</summary>
    public string? WhatsAppMensaje { get; set; }
}

public sealed class ActualizarMontoSolicitud
{
    [Range(0.01, 9_999_999, ErrorMessage = "El monto mensual debe ser mayor a cero.")]
    public decimal MontoMensual { get; set; }
}

public sealed class CancelarSuscripcionSolicitud
{
    public string? Motivo { get; set; }
}

/// <summary>Proyección de dbo.vw_SuscripcionesEstado.</summary>
public sealed class SuscripcionEstadoDto
{
    public int IdSuscripcion { get; set; }
    public int IdCotizacion { get; set; }
    public string? ExternalReference { get; set; }
    public string? PreapprovalId { get; set; }
    public string? NombreCliente { get; set; }
    public string? PayerEmail { get; set; }
    public decimal MontoMensual { get; set; }
    public string? Moneda { get; set; }
    public int? DiasPrueba { get; set; }
    public DateTime? FechaInicio { get; set; }
    public string? Estado { get; set; }
    public string? EstadoDescripcion { get; set; }
    public string? InitPoint { get; set; }
    public DateTime FechaCreacion { get; set; }
    public DateTime? FechaAutorizacion { get; set; }
    public DateTime? FechaCancelacion { get; set; }
    public string? MotivoCancelacion { get; set; }
    public DateTime? FechaProximoPago { get; set; }
    public DateTime? FechaUltimoPago { get; set; }
    public string? Origen { get; set; }
    public string? UsuarioCreacion { get; set; }
    public int CuotasCobradas { get; set; }
    public int CuotasRechazadas { get; set; }
    public decimal TotalCobrado { get; set; }

    public List<PagoSuscripcionDto> Pagos { get; set; } = new();
}

public sealed class PagoSuscripcionDto
{
    public int Id { get; set; }
    public string? MpAuthorizedPaymentId { get; set; }
    public string? MpPaymentId { get; set; }
    public decimal Monto { get; set; }
    public string? Moneda { get; set; }
    public string? Estado { get; set; }
    public string? EstadoPago { get; set; }
    public string? DetalleEstado { get; set; }
    public DateTime? FechaProgramada { get; set; }
    public DateTime? FechaPago { get; set; }
}

/// <summary>
/// Alta de un cobro único (Checkout Pro). Es el equipamiento o los insumos que
/// el cliente paga de una vez, lo que la suscripción no cubre.
/// </summary>
public sealed class CrearPagoSolicitud
{
    [Range(1, int.MaxValue, ErrorMessage = "IdCotizacion es obligatorio.")]
    public int IdCotizacion { get; set; }

    [Required(ErrorMessage = "El correo del cliente es obligatorio.")]
    [EmailAddress(ErrorMessage = "El correo del cliente no es válido.")]
    public string PayerEmail { get; set; } = string.Empty;

    /// <summary>Equivale a Cotizacion.TotalProductos.</summary>
    [Range(0.01, 9_999_999, ErrorMessage = "El monto debe ser mayor a cero.")]
    public decimal Monto { get; set; }

    [Required(ErrorMessage = "El nombre del cliente es obligatorio.")]
    public string NombreCliente { get; set; } = string.Empty;

    /// <summary>
    /// Qué se está cobrando. Si no se envía se arma uno por defecto con el
    /// nombre del cliente. Es lo que el cliente ve en el checkout.
    /// </summary>
    public string? Concepto { get; set; }

    public string? UsuarioCreacion { get; set; }

    /// <summary>"WEBEMPLEADO" o "TSD".</summary>
    public string? Origen { get; set; }

    /// <summary>
    /// Si viene, el link se manda por WhatsApp apenas se crea el cobro.
    /// </summary>
    public string? Telefono { get; set; }
}

public sealed class CrearPagoRespuesta
{
    public int IdPago { get; set; }
    public int IdCotizacion { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public string MpPreferenceId { get; set; } = string.Empty;

    /// <summary>Link que se envía al cliente para que pague.</summary>
    public string InitPoint { get; set; } = string.Empty;

    public string Estado { get; set; } = string.Empty;
    public decimal Monto { get; set; }
    public string Moneda { get; set; } = string.Empty;

    /// <summary>null = no se pidió envío. Ver CrearSuscripcionRespuesta.</summary>
    public bool? WhatsAppEnviado { get; set; }

    public string? WhatsAppMensaje { get; set; }
}

/// <summary>Proyección de dbo.vw_PagosUnicosEstado.</summary>
public sealed class PagoUnicoDto
{
    public int IdPago { get; set; }
    public int IdCotizacion { get; set; }
    public string? ExternalReference { get; set; }
    public string? MpPreferenceId { get; set; }
    public string? MpPaymentId { get; set; }
    public string? InitPoint { get; set; }
    public string? Concepto { get; set; }
    public string? NombreCliente { get; set; }
    public string? PayerEmail { get; set; }
    public decimal Monto { get; set; }
    public string? Moneda { get; set; }
    public string? Estado { get; set; }
    public string? EstadoDescripcion { get; set; }
    public string? EstadoDetalle { get; set; }
    public DateTime FechaCreacion { get; set; }
    public DateTime? FechaPago { get; set; }
    public string? Origen { get; set; }
    public string? UsuarioCreacion { get; set; }
}

/// <summary>
/// Envío del link de pago al cliente por WhatsApp.
/// El teléfono se normaliza del lado del servicio.
/// </summary>
public sealed class EnviarWhatsAppSolicitud
{
    [Required(ErrorMessage = "El teléfono es obligatorio.")]
    public string Telefono { get; set; } = string.Empty;

    /// <summary>
    /// Sobrescribe el nombre usado en el saludo. Si no se envía se toma el
    /// que está guardado en la suscripción o el pago.
    /// </summary>
    public string? NombreCliente { get; set; }
}

public sealed class EnviarWhatsAppRespuesta
{
    public bool Ok { get; set; }
    public string? MessageSid { get; set; }
    public string? Telefono { get; set; }
    public string? Mensaje { get; set; }
}

/// <summary>Error uniforme para todos los endpoints.</summary>
public sealed class ErrorRespuesta
{
    public bool Ok => false;
    public string Mensaje { get; set; } = string.Empty;
    public string? Detalle { get; set; }

    public ErrorRespuesta() { }

    public ErrorRespuesta(string mensaje, string? detalle = null)
    {
        Mensaje = mensaje;
        Detalle = detalle;
    }
}
