namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Normaliza la URL a la que MercadoPago devuelve al cliente después del
/// checkout.
///
/// El retorno es SÓLO navegación. No lleva la cotización, ni el preapproval, ni
/// el estado, ni ninguna referencia al cobro: el cliente vuelve al sitio
/// institucional y nada más. La sincronización del estado es enteramente
/// server-side —webhook → GET /preapproval/{id} → base—, así que el circuito
/// cierra igual si el cliente cierra el navegador apenas autoriza, nunca vuelve,
/// o vuelve con la pestaña recargada.
///
/// Antes se le pegaba "?cotizacion=N". Se quitó por requisito funcional: ese
/// parámetro no lo consumía nadie —no existe ninguna ruta de retorno en
/// EmpleadoWeb ni en el sitio institucional— y exponía el número de cotización
/// en la barra de direcciones del cliente sin ningún beneficio.
///
/// Se sigue usando UriBuilder y no concatenación de texto: pegar texto a un
/// dominio sin barra final producía "https://sitio.com?cotizacion=1", válido por
/// RFC pero rechazado por bastantes validadores, y fue uno de los sospechosos
/// del 500 al crear suscripciones. UriBuilder normaliza el path a "/", de modo
/// que "https://www.tecnisegur.com.uy" configurado en el servidor sale como
/// "https://www.tecnisegur.com.uy/".
/// </summary>
public static class UrlRetorno
{
    /// <summary>
    /// Devuelve null —no cadena vacía— cuando no hay base configurada o cuando
    /// no es una URL absoluta. MercadoPago valida estos campos como URL y
    /// responde 400 si recibe "", así que en ese caso hay que omitirlos.
    ///
    /// Query y fragmento se descartan aunque vengan en la configuración: el
    /// requisito es que el cliente aterrice en una URL limpia.
    /// </summary>
    public static string? Normalizada(string? urlBase)
    {
        if (string.IsNullOrWhiteSpace(urlBase))
            return null;

        if (!Uri.TryCreate(urlBase.Trim(), UriKind.Absolute, out var baseUri))
            return null;

        var constructor = new UriBuilder(baseUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        return constructor.Uri.AbsoluteUri;
    }
}
