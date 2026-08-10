namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Arma la URL a la que MercadoPago devuelve al cliente, agregándole la
/// cotización como parámetro.
///
/// Existe para no repetir el armado en suscripciones y pagos únicos, pero sobre
/// todo para no volver a componerla concatenando texto: pegar "?cotizacion=N" a
/// un dominio sin barra final produce "https://sitio.com?cotizacion=1", que es
/// válido por RFC pero lo rechazan bastantes validadores —y quedó como uno de
/// los sospechosos del 500 al crear suscripciones—. UriBuilder normaliza el
/// path a "/" y compone el query aunque la base ya traiga uno.
/// </summary>
public static class UrlRetorno
{
    /// <summary>
    /// Devuelve null —no cadena vacía— cuando no hay base configurada o cuando
    /// no es una URL absoluta. MercadoPago valida estos campos como URL y
    /// responde 400 si recibe "", así que en ese caso hay que omitirlos.
    /// </summary>
    public static string? ConCotizacion(string? urlBase, int idCotizacion)
    {
        if (string.IsNullOrWhiteSpace(urlBase))
            return null;

        if (!Uri.TryCreate(urlBase.Trim(), UriKind.Absolute, out var baseUri))
            return null;

        var constructor = new UriBuilder(baseUri);

        var query = constructor.Query.TrimStart('?');

        constructor.Query = string.IsNullOrEmpty(query)
            ? $"cotizacion={idCotizacion}"
            : $"{query}&cotizacion={idCotizacion}";

        return constructor.Uri.AbsoluteUri;
    }
}
