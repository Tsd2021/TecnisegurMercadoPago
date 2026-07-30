using System.Text.Json.Serialization;

namespace TecnisegurMercadoPago.Api.Modelos.MercadoPago;

/// <summary>
/// Payload que MercadoPago envía al webhook.
/// </summary>
public sealed class NotificacionWebhook
{
    /// <summary>Id de la notificación. Se usa como clave de idempotencia.</summary>
    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("live_mode")]
    public bool LiveMode { get; set; }

    /// <summary>
    /// subscription_preapproval | subscription_authorized_payment | payment
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Marca del IPN heredado, que trae "topic" en vez de "type".
    /// WebhookController lo usa para descartar esas notificaciones sin
    /// persistirlas: MercadoPago manda el mismo evento en los dos formatos y
    /// el viejo no viaja firmado.
    /// </summary>
    [JsonPropertyName("topic")]
    public string? Topic { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("date_created")]
    public DateTimeOffset? DateCreated { get; set; }

    [JsonPropertyName("user_id")]
    public object? UserId { get; set; }

    [JsonPropertyName("data")]
    public NotificacionData? Data { get; set; }

    public string TipoEfectivo => Type ?? Topic ?? "desconocido";
}

public sealed class NotificacionData
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

/// <summary>Respuesta de GET /authorized_payments/{id} — una cuota.</summary>
public sealed class PagoAutorizadoRespuesta
{
    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("preapproval_id")]
    public string? PreapprovalId { get; set; }

    [JsonPropertyName("external_reference")]
    public string? ExternalReference { get; set; }

    [JsonPropertyName("transaction_amount")]
    public decimal? TransactionAmount { get; set; }

    [JsonPropertyName("currency_id")]
    public string? CurrencyId { get; set; }

    /// <summary>processed | recycling | scheduled | cancelled</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("debit_date")]
    public DateTimeOffset? DebitDate { get; set; }

    [JsonPropertyName("payment")]
    public PagoAutorizadoDetalle? Payment { get; set; }
}

/// <summary>Respuesta de GET /authorized_payments/search.</summary>
public sealed class BusquedaPagosAutorizados
{
    [JsonPropertyName("results")]
    public List<PagoAutorizadoRespuesta> Results { get; set; } = new();
}

public sealed class PagoAutorizadoDetalle
{
    [JsonPropertyName("id")]
    public object? Id { get; set; }

    /// <summary>approved | rejected | pending | refunded</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("status_detail")]
    public string? StatusDetail { get; set; }
}
