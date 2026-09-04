using Microsoft.Extensions.Options;
using TecnisegurMercadoPago.Api.Configuracion;
using TecnisegurMercadoPago.Api.Modelos.Contratos;

namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Arma y manda por WhatsApp los links de cobro.
///
/// Vive acá y no en EmpleadoWeb por el mismo motivo que el access token de
/// MercadoPago: las credenciales en un solo lugar. Además TSD Desktop necesita
/// el mismo envío, y duplicarlo obligaría a mantener dos implementaciones.
/// </summary>
public sealed class EnvioWhatsAppServicio
{
    private readonly TwilioCliente _twilio;
    private readonly SuscripcionServicio _suscripciones;
    private readonly PagoServicio _pagos;
    private readonly TwilioOpciones _opciones;
    private readonly ILogger<EnvioWhatsAppServicio> _log;

    public EnvioWhatsAppServicio(
        TwilioCliente twilio,
        SuscripcionServicio suscripciones,
        PagoServicio pagos,
        IOptions<TwilioOpciones> opciones,
        ILogger<EnvioWhatsAppServicio> log)
    {
        _twilio = twilio;
        _suscripciones = suscripciones;
        _pagos = pagos;
        _opciones = opciones.Value;
        _log = log;
    }

    public async Task<EnviarWhatsAppRespuesta> EnviarLinkSuscripcionAsync(
        int idSuscripcion,
        EnviarWhatsAppSolicitud solicitud,
        CancellationToken ct = default)
    {
        VerificarConfiguracion();

        var suscripcion = await _suscripciones.ObtenerAsync(idSuscripcion, ct)
            ?? throw new ReglaNegocioException(
                $"No existe la suscripción {idSuscripcion}.");

        if (string.IsNullOrWhiteSpace(suscripcion.InitPoint))
        {
            throw new ReglaNegocioException(
                "La suscripción no tiene link de adhesión para enviar.");
        }

        if (EstadoSuscripcion.EsCancelada(suscripcion.Estado))
        {
            throw new ReglaNegocioException(
                "La suscripción está cancelada: su link ya no sirve para adherirse.");
        }

        var nombre = PrimerNombre(
            solicitud.NombreCliente ?? suscripcion.NombreCliente);

        /* Etiqueta corta, por decisión del negocio. La plantilla no tiene hueco
           para el importe y no se quiso agregar un {{4}}, así que el cliente ve
           cuánto se le cobra recién al abrir el checkout de MercadoPago. */
        const string tipo = "Suscripción mensual";

        var mensaje =
            $"Estimado {nombre}," +
            $"\nAdjuntamos link para pagos a Tecnisegur Alarmas" +
            $"\nLink: {suscripcion.InitPoint}" +
            $"\nTipo: {tipo}" +
            $"\nGracias por confiar en nosotros.";

        return await EnviarAsync(
            solicitud.Telefono,
            mensaje,
            ResolverPlantilla(_opciones.ContentSidSuscripcion),
            new Dictionary<string, string>
            {
                ["1"] = nombre,
                ["2"] = suscripcion.InitPoint,
                ["3"] = tipo
            },
            ct);
    }

    public async Task<EnviarWhatsAppRespuesta> EnviarLinkPagoAsync(
        int idPago,
        EnviarWhatsAppSolicitud solicitud,
        CancellationToken ct = default)
    {
        VerificarConfiguracion();

        var pago = await _pagos.ObtenerAsync(idPago, ct)
            ?? throw new ReglaNegocioException($"No existe el pago {idPago}.");

        if (string.IsNullOrWhiteSpace(pago.InitPoint))
            throw new ReglaNegocioException("El pago no tiene link para enviar.");

        if (pago.Estado == "approved")
        {
            throw new ReglaNegocioException(
                "Ese pago ya está acreditado: no corresponde volver a enviar el link.");
        }

        var nombre = PrimerNombre(solicitud.NombreCliente ?? pago.NombreCliente);

        var tipo = TipoDeCobro(pago.Concepto);

        var mensaje =
            $"Estimado {nombre}," +
            $"\nAdjuntamos link para pagos a Tecnisegur Alarmas" +
            $"\nLink: {pago.InitPoint}" +
            $"\nTipo: {tipo}" +
            $"\nGracias por confiar en nosotros.";

        return await EnviarAsync(
            solicitud.Telefono,
            mensaje,
            ResolverPlantilla(_opciones.ContentSidPago),
            new Dictionary<string, string>
            {
                ["1"] = nombre,
                ["2"] = pago.InitPoint,
                ["3"] = tipo
            },
            ct);
    }

    /// <summary>
    /// Qué se está cobrando, para la línea "Tipo:" del WhatsApp.
    ///
    /// Sale del concepto porque un pago único ya no es siempre equipamiento:
    /// EmpleadoWeb también cobra por acá una cuota mensual suelta, para el
    /// cliente que no quiere débito automático. Anunciarle "Equipamiento" un
    /// cobro de su cuota lo manda a preguntar por qué le cobran un equipo.
    ///
    /// El concepto viene como "{qué se cobra} - {cliente}"; el nombre no va en
    /// el mensaje, que ya arranca saludando al cliente por su nombre. Sin
    /// concepto se conserva el texto histórico: todos los pagos anteriores a
    /// este cambio son de equipamiento.
    /// </summary>
    private static string TipoDeCobro(string? concepto)
    {
        if (string.IsNullOrWhiteSpace(concepto))
            return "Equipamiento";

        var corte = concepto.IndexOf(" - ", StringComparison.Ordinal);

        var tipo = corte > 0 ? concepto[..corte] : concepto;

        return tipo.Trim();
    }

    /// <summary>
    /// Usa el override si está configurado; si no, la plantilla general.
    /// Hoy hay una sola aprobada y sirve para los dos casos, porque distingue
    /// qué se cobra con la variable 3.
    /// </summary>
    private string ResolverPlantilla(string especifica)
        => string.IsNullOrWhiteSpace(especifica) ? _opciones.ContentSid : especifica;

    private async Task<EnviarWhatsAppRespuesta> EnviarAsync(
        string telefono,
        string mensaje,
        string contentSid,
        Dictionary<string, string> variables,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(contentSid))
        {
            /* Sin plantilla aprobada WhatsApp sólo acepta texto libre dentro de
               la ventana de 24 h desde el último mensaje del cliente. Queda
               registrado porque explica el error 63016 de Twilio, que de otro
               modo parece un problema de credenciales. */
            _log.LogWarning(
                "Envío sin plantilla aprobada: sólo funciona dentro de la " +
                "ventana de 24 horas o contra el sandbox de Twilio.");
        }

        var sid = await _twilio.EnviarWhatsAppAsync(
            telefono, mensaje, contentSid, variables, ct);

        return new EnviarWhatsAppRespuesta
        {
            Ok = true,
            MessageSid = sid,
            Telefono = _twilio.NormalizarTelefono(telefono),
            Mensaje = mensaje
        };
    }

    private void VerificarConfiguracion()
    {
        if (!_twilio.Configurado)
            throw new ReglaNegocioException(_twilio.MotivoNoConfigurado());
    }

    /// <summary>
    /// El saludo queda mejor con el primer nombre que con la razón social
    /// completa que suele venir cargada en la cotización.
    /// </summary>
    private static string PrimerNombre(string? nombreCompleto)
    {
        if (string.IsNullOrWhiteSpace(nombreCompleto)) return "cliente";

        var primero = nombreCompleto.Trim().Split(' ')[0];

        return string.IsNullOrWhiteSpace(primero) ? "cliente" : primero;
    }
}
