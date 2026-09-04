namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Los estados de un preapproval, en un solo lugar.
///
/// EXISTE POR LA DOBLE ORTOGRAFÍA DE "CANCELADA". La documentación de
/// MercadoPago para dar de baja una suscripción usa <c>canceled</c> —una sola
/// ele—, pero la API **no lo acepta**: medido el 02/09/2026 contra producción,
/// el <c>PUT</c> responde <c>400 "invalid preapproval status parm canceled"</c>.
/// Lo que funciona es <c>cancelled</c>, con dos eles, que es lo que este
/// sistema mandó desde el principio (verificado también el 29/07/2026).
///
/// MercadoPago sí *responde* <c>cancelled</c> en el GET y en sus
/// notificaciones. Aun así, comparar contra un literal suelto sigue siendo una
/// bomba de tiempo silenciosa —no falla, simplemente deja de reconocer el
/// estado— y por eso la normalización se mantiene.
///
/// De ahí los valores separados, que no son una inconsistencia sino la
/// frontera entre lo interno, lo que viaja a MercadoPago y lo que ya está
/// desplegado en otros sistemas:
///
///   <see cref="Cancelada"/>          "canceled"   — canónico SÓLO interno
///   <see cref="CanceladaSaliente"/>  "cancelled"  — lo que acepta MercadoPago
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
    /// Valor canónico INTERNO, el que devuelve <see cref="Normalizar"/> para
    /// poder comparar sin importar con qué ortografía llegue el estado.
    /// Una sola ele.
    ///
    /// <b>No es el que se le manda a MercadoPago</b>: para eso está
    /// <see cref="CanceladaSaliente"/>. Ver ahí por qué.
    /// </summary>
    public const string Cancelada = "canceled";

    /// <summary>
    /// El que se guarda en <c>SuscripcionCotizacion.Estado</c> y viaja a TSD y
    /// EmpleadoWeb. Dos eles. No tocar sin republicar los dos sistemas.
    /// </summary>
    public const string CanceladaContrato = "cancelled";

    /// <summary>
    /// El que acepta MercadoPago en el <c>PUT /preapproval/{id}</c>. Dos eles.
    ///
    /// <b>MEDIDO el 02/09/2026 contra producción</b>, cancelando desde TSD la
    /// suscripción de la cotización 45: con <c>canceled</c> —una sola ele, que
    /// es lo que dice la documentación— MercadoPago responde
    /// <c>400 "invalid preapproval status parm canceled"</c>. Con
    /// <c>cancelled</c> funciona, y es lo que este sistema mandó desde el
    /// principio (verificado también el 29/07/2026).
    ///
    /// O sea que la documentación y la API no coinciden. Manda la API. Es un
    /// alias de <see cref="CanceladaContrato"/> y no un literal nuevo, porque
    /// son el mismo valor; existe aparte para que el día que MercadoPago
    /// arregle la documentación se vea de un vistazo cuál de los dos usos hay
    /// que revisar.
    /// </summary>
    public const string CanceladaSaliente = CanceladaContrato;

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
