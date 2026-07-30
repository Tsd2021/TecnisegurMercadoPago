namespace TecnisegurMercadoPago.Api.Configuracion;

/// <summary>
/// Configuración del envío de WhatsApp por Twilio.
///
/// A diferencia de MercadoPago, esta sección es OPCIONAL: si no está
/// configurada la aplicación arranca igual y los endpoints de envío devuelven
/// un error de negocio explicando qué falta. El cobro no depende de esto.
/// </summary>
public sealed class TwilioOpciones
{
    public const string Seccion = "Twilio";

    /// <summary>
    /// Interruptor general. En false los endpoints de envío responden 409 sin
    /// intentar nada, que es lo que corresponde mientras no esté aprobada la
    /// plantilla de Meta.
    /// </summary>
    public bool Habilitado { get; set; }

    /// <summary>Empieza con AC. NUNCA se versiona.</summary>
    public string AccountSid { get; set; } = string.Empty;

    /// <summary>NUNCA se versiona: va por variable de entorno.</summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>
    /// Número emisor en formato E.164, sin el prefijo "whatsapp:" (se agrega
    /// solo). En el sandbox de Twilio es +14155238886.
    /// </summary>
    public string NumeroOrigen { get; set; } = string.Empty;

    /// <summary>
    /// SID de la plantilla aprobada (empieza con HX). Sirve para los dos tipos
    /// de link, porque la plantilla los distingue con la variable 3.
    ///
    /// La plantilla en uso es:
    ///
    ///     Estimado {{1}},
    ///     Adjuntamos link para pagos a Tecnisegur Alarmas
    ///     Link: {{2}}
    ///     Tipo: {{3}}
    ///     Gracias por confiar en nosotros.
    ///
    ///     {{1}} = nombre del cliente
    ///     {{2}} = link de pago
    ///     {{3}} = qué se está cobrando, con el importe
    ///
    /// WhatsApp no permite texto libre en mensajes iniciados por la empresa
    /// fuera de la ventana de 24 horas desde el último mensaje del cliente:
    /// hace falta una plantilla aprobada por Meta. Si esto queda vacío se
    /// manda texto libre, que sólo funciona dentro de esa ventana o contra el
    /// sandbox de Twilio.
    /// </summary>
    public string ContentSid { get; set; } = string.Empty;

    /// <summary>
    /// Override opcional: sólo si algún día se aprueba una plantilla distinta
    /// para las suscripciones. Vacío = se usa <see cref="ContentSid"/>.
    /// </summary>
    public string ContentSidSuscripcion { get; set; } = string.Empty;

    /// <summary>Ídem para el link de pago único.</summary>
    public string ContentSidPago { get; set; } = string.Empty;

    public string UrlBase { get; set; } = "https://api.twilio.com";

    public int TimeoutSegundos { get; set; } = 20;

    /// <summary>
    /// Prefijo telefónico del país, para normalizar números locales.
    /// Uruguay = 598.
    /// </summary>
    public string PrefijoPais { get; set; } = "598";
}
