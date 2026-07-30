using System.Text.Json.Serialization;

namespace TecnisegurMercadoPago.Api.Modelos.MercadoPago;

/// <summary>
/// Cuerpo de POST /preapproval — suscripción SIN plan asociado.
/// En este modo MercadoPago exige "reason" y "external_reference".
/// </summary>
public sealed class PreapprovalSolicitud
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Referencia al sistema propio: "COT-{IdCotizacion}".
    /// MercadoPago la devuelve en cada notificación, lo que permite conciliar
    /// automáticamente el pago con la cotización que lo originó.
    /// </summary>
    [JsonPropertyName("external_reference")]
    public string ExternalReference { get; set; } = string.Empty;

    [JsonPropertyName("payer_email")]
    public string PayerEmail { get; set; } = string.Empty;

    /// <summary>
    /// MercadoPago valida esto como URL. Si no hay una configurada hay que
    /// OMITIR el campo: mandar cadena vacía devuelve 400.
    /// </summary>
    [JsonPropertyName("back_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackUrl { get; set; }

    /// <summary>
    /// "pending" para obtener init_point y que el cliente autorice en el
    /// entorno de MercadoPago. Con "authorized" haría falta card_token_id,
    /// lo que implicaría manejar datos de tarjeta (carga PCI).
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("auto_recurring")]
    public AutoRecurring AutoRecurring { get; set; } = new();
}

public sealed class AutoRecurring
{
    [JsonPropertyName("frequency")]
    public int Frequency { get; set; } = 1;

    /// <summary>"months" o "days".</summary>
    [JsonPropertyName("frequency_type")]
    public string FrequencyType { get; set; } = "months";

    [JsonPropertyName("transaction_amount")]
    public decimal TransactionAmount { get; set; }

    [JsonPropertyName("currency_id")]
    public string CurrencyId { get; set; } = "UYU";

    /* Las dos fechas van con FechaMercadoPagoConverter: MercadoPago exige
       exactamente tres decimales de milisegundos y rechaza con 400 el formato
       que System.Text.Json produce por defecto. Ver el converter. */

    [JsonPropertyName("start_date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(FechaMercadoPagoConverter))]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("end_date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(FechaMercadoPagoConverter))]
    public DateTimeOffset? EndDate { get; set; }

    [JsonPropertyName("free_trial")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FreeTrial? FreeTrial { get; set; }
}

public sealed class FreeTrial
{
    [JsonPropertyName("frequency")]
    public int Frequency { get; set; }

    [JsonPropertyName("frequency_type")]
    public string FrequencyType { get; set; } = "days";
}

/// <summary>Respuesta de POST/GET/PUT /preapproval.</summary>
public sealed class PreapprovalRespuesta
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("init_point")]
    public string? InitPoint { get; set; }

    [JsonPropertyName("external_reference")]
    public string? ExternalReference { get; set; }

    [JsonPropertyName("payer_email")]
    public string? PayerEmail { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("date_created")]
    public DateTimeOffset? DateCreated { get; set; }

    [JsonPropertyName("last_modified")]
    public DateTimeOffset? LastModified { get; set; }

    [JsonPropertyName("next_payment_date")]
    public DateTimeOffset? NextPaymentDate { get; set; }

    [JsonPropertyName("auto_recurring")]
    public AutoRecurring? AutoRecurring { get; set; }
}

/// <summary>Cuerpo de PUT /preapproval/{id} para cambiar el importe.</summary>
public sealed class PreapprovalActualizacion
{
    [JsonPropertyName("auto_recurring")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AutoRecurringMonto? AutoRecurring { get; set; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; set; }
}

public sealed class AutoRecurringMonto
{
    [JsonPropertyName("transaction_amount")]
    public decimal TransactionAmount { get; set; }

    [JsonPropertyName("currency_id")]
    public string CurrencyId { get; set; } = "UYU";
}
