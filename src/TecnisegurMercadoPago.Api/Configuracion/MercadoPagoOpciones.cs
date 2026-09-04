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
    /// preferencias de Checkout Pro y en los preapproval, para reforzar la
    /// entrega de ese aviso. Si queda vacía se omite el campo y rige sólo el
    /// webhook del panel.
    /// </summary>
    public string UrlWebhook { get; set; } = string.Empty;

    /// <summary>
    /// Dominios que se rechazan como correo del pagador. Son los rellenos que
    /// se cargan cuando la cotización no tiene correo: pasan la validación de
    /// formato pero no existen, así que el cliente nunca recibe el aviso de
    /// cobro ni el de rechazo.
    ///
    /// Va por configuración y no en el código porque la lista crece cada vez
    /// que alguien inventa un relleno nuevo.
    /// </summary>
    public List<string> DominiosCorreoVetados { get; set; } = new();

    /// <summary>
    /// Correo de la cuenta cobradora. MercadoPago no permite que el pagador y
    /// el cobrador sean el mismo; teniéndolo acá se rechaza antes de gastar una
    /// llamada y se da un mensaje que se entiende. Opcional: si queda vacío no
    /// se comprueba.
    /// </summary>
    public string CorreoCobrador { get; set; } = string.Empty;

    /// <summary>
    /// Habilita el repaso diario que restituye el importe pleno cuando el
    /// descuento de una cotización vence (ver
    /// Database/18_DescuentosVencidos.sql).
    ///
    /// <b>Arranca apagada a propósito.</b> Cada fila que toca es un cliente al
    /// que se le sube la cuota sin que él haya hecho nada, y la primera corrida
    /// alcanza de una vez a todos los descuentos vencidos acumulados. Con la
    /// bandera en false el repaso igual corre y deja en el log exactamente lo
    /// que haría, sin llamar a MercadoPago: eso permite revisar la lista antes
    /// de mover plata.
    ///
    /// La misma lista se puede mirar en SQL con
    /// SELECT * FROM dbo.vw_SuscripcionesDescuentoVencido.
    /// </summary>
    public bool RestituirDescuentosVencidos { get; set; }

    /// <summary>
    /// Consultar GET /users/me antes de dar de alta una suscripción y rechazar
    /// si la cuenta tiene la facturación bloqueada.
    ///
    /// Con la facturación bloqueada MercadoPago igual devuelve 201 y un
    /// init_point válido, y el cliente descubre el problema recién al confirmar
    /// la tarjeta. Esto adelanta el aviso al vendedor.
    ///
    /// Se puede apagar: la relación entre billing.allow y la autorización de
    /// suscripciones está establecida por experimento, no por documentación de
    /// MercadoPago. Ver ANALISIS-COBROS.md §2 bis.
    /// </summary>
    public bool VerificarFacturacionHabilitada { get; set; } = true;

    public string Moneda { get; set; } = "UYU";

    public int DiasPruebaPorDefecto { get; set; } = 15;

    /// <summary>
    /// Cuando es true se rechaza toda operación contra credenciales productivas.
    /// Sirve de red de seguridad mientras se prueba en sandbox.
    /// </summary>
    public bool ModoSandbox { get; set; } = true;

    public int TimeoutSegundos { get; set; } = 30;
}
