namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Los estados de un preapproval, en un solo lugar.
///
/// EXISTE POR LA DOBLE ORTOGRAFÍA DE "CANCELADA". La documentación actual de
/// MercadoPago para dar de baja una suscripción usa <c>canceled</c> —una sola
/// ele—, mientras que este sistema mandó <c>cancelled</c> desde el principio y
/// MercadoPago lo aceptó (verificado el 29/07/2026: la baja se completó y MP
/// emitió su propia notificación por ella). No sabemos si acepta las dos o si
/// una quedó como sinónimo histórico, y tampoco sabemos con cuál responde el
/// GET. Mientras eso siga abierto, comparar contra un literal suelto es una
/// bomba de tiempo silenciosa: no falla, simplemente deja de reconocer el
/// estado.
///
/// De ahí los DOS valores para lo mismo, que no es una inconsistencia sino la
/// frontera entre lo que decidimos nosotros y lo que ya está desplegado:
///
///   <see cref="Cancelada"/>          "canceled"   — canónico interno y saliente
///   <see cref="CanceladaContrato"/>  "cancelled"  — lo que se persiste
///
/// <b>El valor persistido NO se puede cambiar sin republicar TSD y
/// EmpleadoWeb.</b> Siete lugares fuera de esta API comparan contra
/// <c>'cancelled'</c> y ninguno pasa por acá:
///
///   TSD  COBRANZASMERCADOPAGO.cs         268, 393, 709, 1242
///   TSD  ServicioCobranzasMercadoPago.cs 36   (CanceladasUltimos30Dias)
///   EmpleadoWeb  CotizacionAlarma.cshtml 885  (s.estado !== "cancelled")
///   Base vw_SuscripcionesEstado          el CASE de EstadoDescripcion
///
/// Ninguno rompería con un error: el filtro "Canceladas" devolvería vacío, el
/// contador daría cero, el botón Cancelar quedaría habilitado sobre una
/// suscripción ya cancelada y EmpleadoWeb la mostraría como viva. Es el mismo
/// criterio por el que EstadoLiberacion conserva sus cuatro literales.
///
/// Regla: comparar SIEMPRE con <see cref="EsCancelada"/>, y escribir en la base
/// SIEMPRE a través de <see cref="ParaPersistir"/>.
/// </summary>
public static class EstadoSuscripcion
{
    public const string Pendiente = "pending";
    public const string Autorizada = "authorized";
    public const string Pausada = "paused";

    /// <summary>
    /// Valor canónico interno y el que se le manda a MercadoPago.
    /// Una sola ele, como la documentación vigente.
    /// </summary>
    public const string Cancelada = "canceled";

    /// <summary>
    /// El que se guarda en <c>SuscripcionCotizacion.Estado</c> y viaja a TSD y
    /// EmpleadoWeb. Dos eles. No tocar sin republicar los dos sistemas.
    /// </summary>
    public const string CanceladaContrato = "cancelled";

    /// <summary>
    /// Lleva cualquier ortografía de "cancelada" al valor canónico. El resto de
    /// los estados pasa igual, sólo recortado y en minúsculas: un estado nuevo
    /// que MercadoPago invente tiene que sobrevivir intacto, no convertirse en
    /// null ni en una cadena vacía.
    /// </summary>
    public static string? Normalizar(string? estado)
    {
        if (string.IsNullOrWhiteSpace(estado)) return estado;

        var limpio = estado.Trim().ToLowerInvariant();

        return limpio is CanceladaContrato or Cancelada ? Cancelada : limpio;
    }

    /// <summary>
    /// Si el estado significa "cancelada", venga escrito como venga. Es la
    /// única forma correcta de preguntarlo.
    /// </summary>
    public static bool EsCancelada(string? estado)
        => Normalizar(estado) == Cancelada;

    /// <summary>
    /// Convierte lo que informa MercadoPago al valor que se guarda.
    ///
    /// Sin esto, el día que MercadoPago empiece a devolver <c>canceled</c> en el
    /// GET, la sincronización lo escribiría tal cual en la base —hoy se guarda
    /// <c>remota.Status</c> verbatim— y los siete lugares de arriba dejarían de
    /// reconocer la cancelación sin que nadie se entere.
    /// </summary>
    public static string ParaPersistir(string estado)
        => EsCancelada(estado) ? CanceladaContrato : estado;

    /// <summary>
    /// Estados en los que la suscripción sigue ocupando el índice único
    /// <c>UX_SuscripcionCotizacion_Viva</c>: puede cobrar o llegar a cobrar.
    /// </summary>
    public static bool EstaViva(string? estado)
        => Normalizar(estado) is Pendiente or Autorizada or Pausada;
}
