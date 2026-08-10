using System.Text.Json.Serialization;

namespace TecnisegurMercadoPago.Api.Modelos.MercadoPago;

/// <summary>
/// Respuesta de GET /users/me — sólo la parte que interesa.
///
/// No es un recurso de MercadoPago sino el usuario de MercadoLibre: la respuesta
/// trae un permalink a perfil.mercadolibre.com.uy. Por eso conviven datos que
/// parecen duplicados con los del panel de MercadoPago y no siempre coinciden.
/// </summary>
public sealed class UsuarioCuentaRespuesta
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("status")]
    public EstadoUsuario? Status { get; set; }
}

public sealed class EstadoUsuario
{
    /// <summary>
    /// Habilitación de facturación de la cuenta. Cuando <c>allow</c> es false,
    /// <c>codes</c> dice por qué —el caso visto es "address_pending"—.
    /// </summary>
    [JsonPropertyName("billing")]
    public PermisoUsuario? Billing { get; set; }

    [JsonPropertyName("sell")]
    public PermisoUsuario? Sell { get; set; }

    [JsonPropertyName("site_status")]
    public string? SiteStatus { get; set; }
}

public sealed class PermisoUsuario
{
    [JsonPropertyName("allow")]
    public bool Allow { get; set; }

    [JsonPropertyName("codes")]
    public List<string> Codes { get; set; } = new();
}
