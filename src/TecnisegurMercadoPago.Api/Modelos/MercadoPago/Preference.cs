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

    /// <summary>
    /// Cuándo el dinero queda disponible en la cuenta de Tecnisegur.
    ///
    /// NO es date_approved. La cuenta está configurada con liberación a 21 días,
    /// así que esta fecha cae tres semanas después del cobro.
    ///
    /// MercadoPago la informa al aprobar el pago y NUNCA vuelve a avisar si la
    /// liberación efectivamente ocurrió: no existe un webhook de liberación (se
    /// verificó contra la lista de tópicos el 31/07/2026). Es una previsión firme,
    /// no una confirmación — por eso hay que reconsultar los pagos no liberados
    /// en vez de darla por cumplida. Ver ProcesadorNotificaciones.
    /// </summary>
    [JsonPropertyName("money_release_date")]
    public DateTimeOffset? MoneyReleaseDate { get; set; }

    /// <summary>
    /// Si MercadoPago ya liberó el dinero. Toma dos valores: "pending" y
    /// "released".
    ///
    /// Es el campo que convierte la liberación de inferencia en dato informado.
    /// Hasta el 31/07/2026 se daba por hecho que el pago no decía nada del
    /// destino del dinero y que la única forma de saberlo era comparar
    /// money_release_date contra la fecha de hoy —así está escrito en
    /// 08_EstadoLiberacionExplicito.sql—. La verificación contra el cobro real
    /// 166657246137 mostró que sí lo dice: liberado el 27/07, el pago devuelve
    /// money_release_status = "released".
    ///
    /// Sigue sin ser un asiento contable: confirma que MercadoPago liberó, no
    /// que el importe se acreditó en la cuenta bancaria. Para eso está el
    /// reporte de Liberaciones. Pero es estrictamente mejor que mirar el
    /// almanaque, que no distingue "se liberó" de "ya pasó la fecha prevista".
    /// </summary>
    [JsonPropertyName("money_release_status")]
    public string? MoneyReleaseStatus { get; set; }

    [JsonPropertyName("fee_details")]
    public List<DetalleComision>? FeeDetails { get; set; }

    [JsonPropertyName("transaction_details")]
    public DetalleTransaccion? TransactionDetails { get; set; }

    /// <summary>
    /// Comisión propia de MercadoPago, con su IVA incluido. Sale de fee_details.
    ///
    /// NO se deriva de (bruto − neto): eso da la suma de la comisión MÁS las
    /// retenciones impositivas, que son cosas distintas. Verificado contra un
    /// cobro real el 31/07/2026 (pago 166657246137, $20 con débito):
    ///
    ///   mercadopago_fee              rate 6,09 %   $1,22
    ///   tax_withholding-uruguay      rate 5 %      $0,98
    ///   tax_withholding-lif_debito   rate 2 %      $0,33
    ///   ------------------------------------------------
    ///   bruto $20,00  −  $2,53  =  neto $17,47
    ///
    /// Sumarlas en un solo campo "Comision" sobreestimaría el costo de
    /// MercadoPago en más del doble, y además mezclaría un costo perdido con
    /// retenciones que son adelantos de impuestos.
    ///
    /// Null cuando MercadoPago no informó el desglose — nunca cero. Un cero acá
    /// se leería como "no cobró comisión", que es falso.
    /// </summary>
    public decimal? ComisionCalculada =>
        FeeDetails is null || FeeDetails.Count == 0
            ? null
            : FeeDetails.Sum(f => f.Amount ?? 0m);

    /// <summary>
    /// Retenciones impositivas que MercadoPago aplica como agente de retención
    /// en Uruguay. Es lo que queda entre la comisión y el neto acreditado.
    ///
    /// Se separan de la comisión porque económicamente no son lo mismo: la
    /// comisión es un costo perdido, las retenciones son adelantos de impuestos
    /// que la empresa acredita contra DGI. Presentarlas juntas da una idea
    /// equivocada de cuánto cuesta cobrar por MercadoPago.
    /// </summary>
    public decimal? RetencionesCalculadas
    {
        get
        {
            if (TransactionAmount is null ||
                TransactionDetails?.NetReceivedAmount is null) return null;

            var descuentoTotal = TransactionAmount.Value -
                                 TransactionDetails.NetReceivedAmount.Value;

            return descuentoTotal - (ComisionCalculada ?? 0m);
        }
    }
}

public sealed class DetalleComision
{
    /// <summary>mercadopago_fee | financing_fee | ...</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }
}

public sealed class DetalleTransaccion
{
    /// <summary>Lo que efectivamente se acredita: bruto menos comisiones.</summary>
    [JsonPropertyName("net_received_amount")]
    public decimal? NetReceivedAmount { get; set; }
}
