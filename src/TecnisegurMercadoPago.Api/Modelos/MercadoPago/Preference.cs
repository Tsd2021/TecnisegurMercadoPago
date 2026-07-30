using System.Text.Json.Serialization;

namespace TecnisegurMercadoPago.Api.Modelos.MercadoPago;

/// <summary>
/// Cuerpo de POST /checkout/preferences — Checkout Pro, cobro único.
///
/// Es la primitiva para lo que la suscripción NO cubre: el equipamiento o los
/// insumos que el cliente paga de una sola vez. La suscripción cobra
/// Cotizacion.TotalServiciosMensual; esto cobra Cotizacion.TotalProductos.
/// </summary>
public sealed class PreferenciaSolicitud
{
    [JsonPropertyName("items")]
    public List<PreferenciaItem> Items { get; set; } = new();

    [JsonPropertyName("payer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PreferenciaPagador? Payer { get; set; }

    /// <summary>
    /// "PAGO-{IdCotizacion}-{IdPagoUnico}". El prefijo la distingue de las
    /// suscripciones, que usan "COT-": sin esa distinción la conciliación en
    /// el webhook sería ambigua.
    /// </summary>
    [JsonPropertyName("external_reference")]
    public string ExternalReference { get; set; } = string.Empty;

    [JsonPropertyName("back_urls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PreferenciaBackUrls? BackUrls { get; set; }

    /// <summary>
    /// "approved" devuelve al cliente automáticamente tras pagar.
    /// MercadoPago lo rechaza si no hay back_urls.success, así que se setea
    /// sólo cuando hay BackUrl configurada.
    /// </summary>
    [JsonPropertyName("auto_return")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutoReturn { get; set; }

    /// <summary>
    /// Refuerza la entrega de la notificación de este pago. El webhook
    /// configurado en el panel sigue funcionando igual; esto lo hace explícito
    /// por preferencia.
    /// </summary>
    [JsonPropertyName("notification_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NotificationUrl { get; set; }

    /// <summary>Texto que ve el cliente en el resumen de su tarjeta.</summary>
    [JsonPropertyName("statement_descriptor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatementDescriptor { get; set; }
}

public sealed class PreferenciaItem
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; } = 1;

    [JsonPropertyName("unit_price")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("currency_id")]
    public string CurrencyId { get; set; } = "UYU";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

public sealed class PreferenciaPagador
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}

public sealed class PreferenciaBackUrls
{
    [JsonPropertyName("success")]
    public string Success { get; set; } = string.Empty;

    [JsonPropertyName("pending")]
    public string Pending { get; set; } = string.Empty;

    [JsonPropertyName("failure")]
    public string Failure { get; set; } = string.Empty;
}

/// <summary>Respuesta de POST /checkout/preferences.</summary>
public sealed class PreferenciaRespuesta
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>Link que se le envía al cliente para pagar.</summary>
    [JsonPropertyName("init_point")]
    public string? InitPoint { get; set; }

    [JsonPropertyName("sandbox_init_point")]
    public string? SandboxInitPoint { get; set; }

    [JsonPropertyName("external_reference")]
    public string? ExternalReference { get; set; }

    [JsonPropertyName("date_created")]
    public DateTimeOffset? DateCreated { get; set; }
}

/// <summary>
/// Respuesta de GET /v1/payments/{id}.
///
/// Es el recurso que se consulta cuando llega una notificación de tipo
/// "payment": el webhook sólo trae el id, el external_reference aparece acá.
/// </summary>
public sealed class PagoRespuesta
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    /// <summary>approved | rejected | in_process | cancelled | refunded | pending</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("status_detail")]
    public string? StatusDetail { get; set; }

    [JsonPropertyName("external_reference")]
    public string? ExternalReference { get; set; }

    [JsonPropertyName("transaction_amount")]
    public decimal? TransactionAmount { get; set; }

    [JsonPropertyName("currency_id")]
    public string? CurrencyId { get; set; }

    [JsonPropertyName("date_approved")]
    public DateTimeOffset? DateApproved { get; set; }

    [JsonPropertyName("date_created")]
    public DateTimeOffset? DateCreated { get; set; }

    [JsonPropertyName("payment_method_id")]
    public string? PaymentMethodId { get; set; }

    /// <summary>
    /// Presente en los pagos de una suscripción. Sirve para descartarlos en la
    /// rama "payment" del procesador: esas cuotas ya las maneja
    /// subscription_authorized_payment y registrarlas dos veces duplicaría
    /// la cobranza.
    /// </summary>
    [JsonPropertyName("preapproval_id")]
    public string? PreapprovalId { get; set; }
}
