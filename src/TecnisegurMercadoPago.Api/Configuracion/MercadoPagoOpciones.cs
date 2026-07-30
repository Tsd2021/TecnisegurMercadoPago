namespace TecnisegurMercadoPago.Api.Configuracion;

/// <summary>
/// Configuración de la integración con MercadoPago.
/// Se enlaza desde la sección "MercadoPago" de appsettings / variables de entorno.
/// </summary>
public sealed class MercadoPagoOpciones
{
    public const string Seccion = "MercadoPago";

    /// <summary>
    /// Access token del vendedor. NUNCA se versiona: va por user-secrets en
    /// desarrollo y por variable de entorno en el servidor.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Clave secreta de la aplicación usada para validar la firma x-signature
    /// de los webhooks. Se obtiene en "Tus integraciones" → Webhooks.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public string UrlBase { get; set; } = "https://api.mercadopago.com";

    /// <summary>
    /// URL a la que MercadoPago devuelve al cliente después de autorizar.
    /// </summary>
    public string BackUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL pública del webhook. Se manda como notification_url en las
    /// preferencias de Checkout Pro para reforzar la entrega de ese aviso.
    /// Si queda vacía se omite el campo y rige sólo el webhook del panel.
    /// </summary>
    public string UrlWebhook { get; set; } = string.Empty;

    public string Moneda { get; set; } = "UYU";

    public int DiasPruebaPorDefecto { get; set; } = 15;

    /// <summary>
    /// Cuando es true se rechaza toda operación contra credenciales productivas.
    /// Sirve de red de seguridad mientras se prueba en sandbox.
    /// </summary>
    public bool ModoSandbox { get; set; } = true;

    public int TimeoutSegundos { get; set; } = 30;
}
