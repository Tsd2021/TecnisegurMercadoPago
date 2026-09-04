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

    /// <summary>Lo que se le cobró al cliente.</summary>
    public decimal Monto { get; set; }

    /// <summary>
    /// Lo que efectivamente entra, ya descontada la comisión de MercadoPago
    /// (4,99% + IVA = 6,09% con la configuración actual de la cuenta).
    /// Null = MercadoPago todavía no lo informó. Nunca cero.
    /// </summary>
    public decimal? MontoNeto { get; set; }

    /// <summary>Comisión propia de MercadoPago: 6,09 % con la tarifa actual.</summary>
    public decimal? Comision { get; set; }

    /// <summary>
    /// Retenciones impositivas que MercadoPago aplica como agente de retención
    /// en Uruguay. Separadas de la comisión a propósito: son adelantos de
    /// impuestos acreditables contra DGI, no un costo perdido. Sobre un cobro
    /// real con débito fueron 5 % (uruguay) + 2 % (LIF débito).
    /// </summary>
    public decimal? Retenciones { get; set; }

    public string? Moneda { get; set; }
    public string? Estado { get; set; }
    public string? EstadoPago { get; set; }
    public string? DetalleEstado { get; set; }
    public DateTime? FechaProgramada { get; set; }

    /// <summary>Cuándo se le cobró al cliente.</summary>
    public DateTime? FechaPago { get; set; }

    /// <summary>
    /// Cuándo el dinero queda disponible: 21 días después del cobro con la
    /// configuración actual.
    ///
    /// Es una PREVISIÓN informada por MercadoPago al aprobar el pago, no una
    /// confirmación de que se liberó — no existe webhook de liberación. Un
    /// contracargo dentro de esos 21 días la deja sin efecto sin avisar.
    ///
    /// Para saber si efectivamente se liberó, mirar <see cref="EstadoLiberacionMp"/>.
    /// </summary>
    public DateTime? FechaLiberacion { get; set; }

    /// <summary>
    /// Lo que informa MercadoPago sobre la liberación: "released" o "pending".
    /// Null mientras el repaso diario no haya reconsultado el pago.
    ///
    /// A diferencia de <see cref="FechaLiberacion"/>, esto es un hecho y no una
    /// previsión: la fecha dice cuándo se esperaba liberar, esto dice si pasó.
    /// </summary>
    public string? EstadoLiberacionMp { get; set; }

    /// <summary>
    /// True sólo cuando MercadoPago confirmó la liberación. Null es "todavía no
    /// se sabe", que no es lo mismo que false.
    /// </summary>
    public bool? LiberacionConfirmada => EstadoLiberacionMp is null
        ? null
        : EstadoLiberacionMp == "released";
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

    /// <summary>
    /// Cédula del cliente, de ContratoCotizacionAlarma.Documento. Opcional:
    /// precarga el documento en el formulario de tarjeta del checkout, que el
    /// emisor valida contra el titular. No aplica a las suscripciones — la API
    /// de preapproval no tiene campo de identificación.
    /// </summary>
    public string? Documento { get; set; }

    public string? UsuarioCreacion { get; set; }

    /// <summary>"WEBEMPLEADO" o "TSD".</summary>
    public string? Origen { get; set; }

    /// <summary>
    /// Si viene, el link se manda por WhatsApp apenas se crea el cobro.
    /// </summary>
    public string? Telefono { get; set; }

    /// <summary>
    /// Reemplaza el cobro pendiente de la cotización, si lo hay: lo cancela
    /// —venciendo además su link en MercadoPago— y crea el nuevo.
    ///
    /// Va en false por defecto a propósito. Sin esta bandera, un segundo intento
    /// para la misma cotización se rechaza, que es lo que impide que un doble
    /// click deje dos links vivos. Ponerla en true es afirmar que el reemplazo
    /// es deliberado: el caso típico es volver a cobrarle al mismo cliente el
    /// mes siguiente cuando el link anterior quedó sin pagar.
    /// </summary>
    public bool ReemplazarPendiente { get; set; }
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

    /// <summary>Neto acreditado. Null = MercadoPago no lo informó. Ver PagoSuscripcionDto.</summary>
    public decimal? MontoNeto { get; set; }

    public decimal? Comision { get; set; }

    /// <summary>Ver PagoSuscripcionDto.Retenciones.</summary>
    public decimal? Retenciones { get; set; }

    public string? Moneda { get; set; }
    public string? Estado { get; set; }
    public string? EstadoDescripcion { get; set; }
    public string? EstadoDetalle { get; set; }
    public DateTime FechaCreacion { get; set; }
    public DateTime? FechaPago { get; set; }

    /// <summary>Previsión de liberación (21 días). Ver PagoSuscripcionDto.</summary>
    public DateTime? FechaLiberacion { get; set; }

    /// <summary>Ver PagoSuscripcionDto.EstadoLiberacionMp.</summary>
    public string? EstadoLiberacionMp { get; set; }

    /// <summary>Ver PagoSuscripcionDto.LiberacionConfirmada.</summary>
    public bool? LiberacionConfirmada => EstadoLiberacionMp is null
        ? null
        : EstadoLiberacionMp == "released";

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
