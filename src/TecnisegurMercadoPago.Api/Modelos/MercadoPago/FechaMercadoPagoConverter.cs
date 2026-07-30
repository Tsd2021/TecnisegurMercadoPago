using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TecnisegurMercadoPago.Api.Modelos.MercadoPago;

/// <summary>
/// Serializa fechas con el formato exacto que acepta MercadoPago en
/// auto_recurring: ISO 8601 con <b>exactamente tres decimales</b> de
/// milisegundos y offset explícito — "2026-08-10T12:00:00.000-03:00".
///
/// No es un capricho de estilo. Verificado contra la API el 30/07/2026:
///
///   "2026-08-10T12:00:00-03:00"     → 400 Invalid format in auto_recurring...
///   "2026-08-10T12:00:00.000-03:00" → 201 Created
///
/// System.Text.Json serializa DateTimeOffset con el formato "O", que omite los
/// decimales cuando son cero y usa siete cuando no lo son. Las dos variantes
/// rompen: la primera es la que devolvía el 400 al mandar una fecha de
/// adhesión, y la segunda es la que produce DateTimeOffset.Now al calcular
/// end_date desde el plazo del contrato.
///
/// El mensaje de error de MercadoPago nombra start_date y end_date juntos sin
/// decir cuál está mal, así que conviene mantener los dos con este converter.
/// </summary>
public sealed class FechaMercadoPagoConverter : JsonConverter<DateTimeOffset>
{
    private const string Formato = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    public override DateTimeOffset Read(
        ref Utf8JsonReader reader, Type tipo, JsonSerializerOptions opciones)
    {
        /* Al leer se acepta cualquier ISO válido: las respuestas de MercadoPago
           traen precisiones distintas según el recurso. */
        return reader.GetDateTimeOffset();
    }

    public override void Write(
        Utf8JsonWriter writer, DateTimeOffset valor, JsonSerializerOptions opciones)
    {
        writer.WriteStringValue(
            valor.ToString(Formato, CultureInfo.InvariantCulture));
    }
}
