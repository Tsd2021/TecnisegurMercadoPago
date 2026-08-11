using System.Net.Mail;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TecnisegurMercadoPago.Api.Configuracion;
using TecnisegurMercadoPago.Api.Datos;
using TecnisegurMercadoPago.Api.Modelos.Contratos;
using TecnisegurMercadoPago.Api.Modelos.MercadoPago;

namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Se lanza cuando la operación no puede completarse por una regla de negocio
/// (no por un fallo técnico). El controller la traduce a 409.
/// </summary>
public sealed class ReglaNegocioException : Exception
{
    public ReglaNegocioException(string mensaje) : base(mensaje) { }
}

/// <summary>
/// Orquesta el alta y el mantenimiento de suscripciones:
/// valida reglas, llama a MercadoPago y persiste el resultado.
/// </summary>
public sealed class SuscripcionServicio
{
    private readonly MercadoPagoCliente _mercadoPago;
    private readonly SuscripcionRepositorio _repositorio;
    private readonly CacheEstadoCuenta _cacheCuenta;
    private readonly MercadoPagoOpciones _opciones;
    private readonly ILogger<SuscripcionServicio> _log;

    public SuscripcionServicio(
        MercadoPagoCliente mercadoPago,
        SuscripcionRepositorio repositorio,
        CacheEstadoCuenta cacheCuenta,
        IOptions<MercadoPagoOpciones> opciones,
        ILogger<SuscripcionServicio> log)
    {
        _mercadoPago = mercadoPago;
        _repositorio = repositorio;
        _cacheCuenta = cacheCuenta;
        _opciones = opciones.Value;
        _log = log;
    }

    public async Task<CrearSuscripcionRespuesta> CrearAsync(
        CrearSuscripcionSolicitud solicitud, CancellationToken ct = default)
    {
        /* -------------------------------------------------------------------
         * 1) No puede haber dos suscripciones vivas para la misma cotización.
         *    Se chequea acá para dar un mensaje claro; el índice único de la
         *    base es la garantía real ante llamadas concurrentes.
         * ------------------------------------------------------------------- */
        var existente = await _repositorio
            .ObtenerVivaPorCotizacionAsync(solicitud.IdCotizacion, ct);

        if (existente is not null)
        {
            throw new ReglaNegocioException(
                $"La cotización {solicitud.IdCotizacion} ya tiene una suscripción " +
                $"en estado '{existente.EstadoDescripcion}'. " +
                "Cancele la existente antes de generar una nueva.");
        }

        /* -------------------------------------------------------------------
         * 1 bis) El correo es el único dato del pagador que viaja en un
         *    preapproval: identifica al suscriptor y es la vía por la que
         *    MercadoPago avisa cobros, rechazos y la cancelación automática
         *    tras tres cuotas caídas. Si es inventado el cliente no se entera
         *    de nada, así que se corta acá y no se crea la suscripción.
         * ------------------------------------------------------------------- */
        ValidarCorreoPagador(solicitud.PayerEmail);

        /* -------------------------------------------------------------------
         * 1 ter) Con la facturación bloqueada, MercadoPago igual devuelve 201 y
         *    un init_point válido: el preapproval se crea, el link se manda, y
         *    el cliente descubre el problema recién al confirmar la tarjeta,
         *    con un "Tuvimos un problema" que no explica nada. Preguntar antes
         *    mueve el aviso al vendedor, que es quien puede hacer algo.
         * ------------------------------------------------------------------- */
        await VerificarCuentaHabilitadaAsync(ct);

        var externalReference = $"COT-{solicitud.IdCotizacion}";

        /* -------------------------------------------------------------------
         * Cuándo arranca el cobro. FechaInicio (start_date) y DiasPrueba
         * (free_trial) hacen lo mismo y MercadoPago no documenta cómo los
         * combina, así que se rechaza la mezcla en vez de mandar algo cuyo
         * resultado no podemos predecir.
         * ------------------------------------------------------------------- */
        var fechaInicio = NormalizarFechaInicio(solicitud.FechaInicio);

        if (fechaInicio.HasValue && solicitud.DiasPrueba > 0)
        {
            throw new ReglaNegocioException(
                "No se puede indicar fecha de inicio y días de prueba a la vez: " +
                "los dos posponen la primera cuota. Elija uno de los dos.");
        }

        /* El default de configuración sólo aplica cuando no hay fecha: si no,
           una llamada que manda FechaInicio y omite DiasPrueba se comería los
           15 días por defecto sin que nadie los haya pedido. */
        var diasPrueba = fechaInicio.HasValue
            ? 0
            : solicitud.DiasPrueba ?? _opciones.DiasPruebaPorDefecto;

        /* -------------------------------------------------------------------
         * El plazo del contrato se cuenta desde que empieza a cobrarse, no
         * desde hoy. Calcularlo con DateTimeOffset.Now hacía que una adhesión
         * lejana con plazo corto produjera un end_date ANTERIOR al start_date:
         * MercadoPago acepta ese preapproval en "pending" sin chistar y recién
         * falla cuando el cliente entra al init_point a autorizar, que es el
         * peor momento posible para enterarse.
         *
         * Los días de prueba también corren el arranque del cobro, así que
         * entran en la cuenta: si no, el plazo se comería el free_trial.
         * ------------------------------------------------------------------- */
        var inicioCobro = (fechaInicio ?? DateTimeOffset.Now).AddDays(diasPrueba);

        var fechaFin = solicitud.PlazoMeses.HasValue
            ? inicioCobro.AddMonths(solicitud.PlazoMeses.Value)
            : (DateTimeOffset?)null;

        /* Con el cálculo de arriba la inversión no puede darse —PlazoMeses es
           como mínimo 1—, pero la comprobación se deja puesta: es barata y deja
           el invariante escrito para quien toque estas fechas más adelante. */
        if (fechaFin.HasValue && fechaFin.Value <= inicioCobro)
        {
            throw new ReglaNegocioException(
                "El plazo del contrato deja la suscripción terminando antes de " +
                "empezar. Revise el plazo y la fecha de adhesión.");
        }

        var preapproval = new PreapprovalSolicitud
        {
            Reason = ArmarConcepto(solicitud.NombreCliente),
            ExternalReference = externalReference,
            PayerEmail = solicitud.PayerEmail.Trim(),
            BackUrl = ArmarBackUrl(),
            NotificationUrl = ArmarNotificationUrl(),
            Status = "pending",
            AutoRecurring = new AutoRecurring
            {
                Frequency = 1,
                FrequencyType = "months",
                TransactionAmount = solicitud.MontoMensual,
                CurrencyId = _opciones.Moneda,
                StartDate = fechaInicio,
                EndDate = fechaFin,
                FreeTrial = diasPrueba > 0
                    ? new FreeTrial { Frequency = diasPrueba, FrequencyType = "days" }
                    : null
            }
        };

        /* -------------------------------------------------------------------
         * 2) La clave de idempotencia se deriva de la cotización y el importe.
         *    Si el usuario hace doble click, MercadoPago devuelve la misma
         *    suscripción en vez de crear una segunda.
         * ------------------------------------------------------------------- */
        var claveIdempotencia = $"{externalReference}-{solicitud.MontoMensual:0.00}";

        var respuesta = await _mercadoPago
            .CrearSuscripcionAsync(preapproval, claveIdempotencia, ct);

        if (string.IsNullOrWhiteSpace(respuesta.Id) ||
            string.IsNullOrWhiteSpace(respuesta.InitPoint))
        {
            throw new InvalidOperationException(
                "MercadoPago no devolvió id o init_point para la suscripción.");
        }

        /* -------------------------------------------------------------------
         * 3) Persistir. Si el índice único rechaza el insert significa que otra
         *    llamada concurrente ganó la carrera: se cancela la suscripción
         *    recién creada en MercadoPago para no dejarla huérfana.
         * ------------------------------------------------------------------- */
        var idSuscripcion = await _repositorio.InsertarAsync(
            solicitud.IdCotizacion,
            externalReference,
            respuesta.Id,
            respuesta.InitPoint,
            solicitud.NombreCliente.Trim(),
            solicitud.PayerEmail.Trim(),
            solicitud.MontoMensual,
            _opciones.Moneda,
            diasPrueba,
            fechaInicio?.LocalDateTime,
            respuesta.Status ?? "pending",
            respuesta.NextPaymentDate?.LocalDateTime,
            solicitud.UsuarioCreacion,
            solicitud.Origen,
            /* El payload tal cual salió. Es la única forma de reconstruir por
               qué un cliente no pudo autorizar: el preapproval queda en
               "pending" para siempre y MercadoPago no guarda el intento
               fallido. Con esto se ve el start_date y el end_date que se
               mandaron, que es donde estuvieron los errores. */
            JsonSerializer.Serialize(preapproval),
            ct);

        if (idSuscripcion is null)
        {
            _log.LogWarning(
                "Carrera detectada en la cotización {Id}. Se cancela {Preapproval}.",
                solicitud.IdCotizacion, respuesta.Id);

            await CancelarEnMercadoPagoSilencioso(
                respuesta.Id, solicitud.IdCotizacion, ct);

            throw new ReglaNegocioException(
                "Otra operación creó la suscripción al mismo tiempo. " +
                "Vuelva a consultar el estado de la cotización.");
        }

        _log.LogInformation(
            "Suscripción {Id} creada para la cotización {Cotizacion} ({Preapproval}).",
            idSuscripcion, solicitud.IdCotizacion, respuesta.Id);

        return new CrearSuscripcionRespuesta
        {
            IdSuscripcion = idSuscripcion.Value,
            IdCotizacion = solicitud.IdCotizacion,
            PreapprovalId = respuesta.Id,
            InitPoint = respuesta.InitPoint,
            Estado = respuesta.Status ?? "pending",
            MontoMensual = solicitud.MontoMensual,
            Moneda = _opciones.Moneda,
            DiasPrueba = diasPrueba,
            FechaInicio = fechaInicio?.LocalDateTime
        };
    }

    public async Task<SuscripcionEstadoDto?> ObtenerAsync(
        int idSuscripcion, CancellationToken ct = default)
    {
        var suscripcion = await _repositorio.ObtenerPorIdAsync(idSuscripcion, ct);
        if (suscripcion is null) return null;

        suscripcion.Pagos = await _repositorio.ListarPagosAsync(idSuscripcion, ct);
        return suscripcion;
    }

    public async Task<List<SuscripcionEstadoDto>> ListarPorCotizacionAsync(
        int idCotizacion, CancellationToken ct = default)
    {
        var lista = await _repositorio.ListarPorCotizacionAsync(idCotizacion, ct);

        foreach (var item in lista)
            item.Pagos = await _repositorio.ListarPagosAsync(item.IdSuscripcion, ct);

        return lista;
    }

    /// <summary>
    /// Cambia el importe de una suscripción viva. Es lo que hay que llamar
    /// cuando se edita una cotización que ya tiene suscripción: sin esto,
    /// MercadoPago sigue cobrando el importe original indefinidamente.
    /// </summary>
    public async Task<SuscripcionEstadoDto> ActualizarMontoAsync(
        int idSuscripcion, decimal montoMensual, CancellationToken ct = default)
    {
        var suscripcion = await _repositorio.ObtenerPorIdAsync(idSuscripcion, ct)
            ?? throw new ReglaNegocioException(
                $"No existe la suscripción {idSuscripcion}.");

        if (string.IsNullOrWhiteSpace(suscripcion.PreapprovalId))
        {
            throw new ReglaNegocioException(
                "La suscripción no tiene identificador de MercadoPago.");
        }

        if (EstadoSuscripcion.EsCancelada(suscripcion.Estado))
        {
            throw new ReglaNegocioException(
                "No se puede modificar el importe de una suscripción cancelada.");
        }

        await _mercadoPago.ActualizarMontoAsync(
            suscripcion.PreapprovalId, montoMensual, ct);

        await _repositorio.ActualizarMontoAsync(idSuscripcion, montoMensual, ct);

        _log.LogInformation(
            "Importe de la suscripción {Id} actualizado a {Monto}.",
            idSuscripcion, montoMensual);

        return (await ObtenerAsync(idSuscripcion, ct))!;
    }

    /// <summary>
    /// Baja explícita, pedida por un usuario desde EmpleadoWeb o TSD. Es la
    /// ÚNICA cancelación legítima del sistema, y la única además de la carrera
    /// del índice único que llega a mandar un PUT.
    ///
    /// <paramref name="origen"/> identifica al sistema llamador
    /// (X-Api-Key → SistemaLlamador) y queda en el rastro de auditoría junto
    /// con el estado que tenía la suscripción antes de tocarla.
    ///
    /// Ya cancelada devuelve el estado sin llamar a MercadoPago: la operación
    /// es idempotente, refrescar la pantalla o repetir el pedido no vuelve a
    /// mutar nada.
    /// </summary>
    public async Task<SuscripcionEstadoDto> CancelarAsync(
        int idSuscripcion,
        string? motivo,
        string? origen = null,
        CancellationToken ct = default)
    {
        var suscripcion = await _repositorio.ObtenerPorIdAsync(idSuscripcion, ct)
            ?? throw new ReglaNegocioException(
                $"No existe la suscripción {idSuscripcion}.");

        if (EstadoSuscripcion.EsCancelada(suscripcion.Estado))
            return (await ObtenerAsync(idSuscripcion, ct))!;

        if (string.IsNullOrWhiteSpace(suscripcion.PreapprovalId))
        {
            throw new ReglaNegocioException(
                "La suscripción no tiene identificador de MercadoPago.");
        }

        /* El estado que MercadoPago tiene AHORA, antes de que lo pisemos. Es el
           dato que después permite decir si la baja cortó un cobro vivo o cerró
           un link que nadie había autorizado. Nunca lanza: no vale la pena
           frustrar una baja pedida por un usuario porque falló una consulta. */
        var estadoRemoto = await ConsultarEstadoRemotoAsync(
            suscripcion.PreapprovalId, ct);

        var correlacion = AuditoriaPreapproval.Correlacion();

        AuditoriaPreapproval.Emitida(
            _log,
            operacion: "CANCELAR",
            origen: $"{AuditoriaPreapproval.Origen.CancelacionExplicita}:{origen ?? "desconocido"}",
            preapprovalId: suscripcion.PreapprovalId,
            externalReference: suscripcion.ExternalReference,
            idCotizacion: suscripcion.IdCotizacion,
            estadoLocalAnterior: suscripcion.Estado,
            estadoRemotoAnterior: estadoRemoto,
            estadoNuevo: EstadoSuscripcion.Cancelada,
            detalle: motivo,
            correlacion: correlacion);

        await _mercadoPago.CancelarSuscripcionAsync(
            suscripcion.PreapprovalId,
            $"{AuditoriaPreapproval.Origen.CancelacionExplicita}:{origen ?? "desconocido"} " +
            $"(correlacion={correlacion})",
            ct);

        /* Se guarda el valor de CONTRATO, no el canónico: TSD y EmpleadoWeb
           comparan contra 'cancelled' y no pasan por acá. Ver EstadoSuscripcion. */
        await _repositorio.ActualizarEstadoAsync(
            suscripcion.PreapprovalId, EstadoSuscripcion.CanceladaContrato, null,
            motivo ?? "Cancelada desde el sistema", ct);

        _log.LogInformation("Suscripción {Id} cancelada. Motivo: {Motivo}",
            idSuscripcion, motivo);

        return (await ObtenerAsync(idSuscripcion, ct))!;
    }

    /// <summary>
    /// Estado actual del preapproval en MercadoPago, o null si no se pudo
    /// averiguar. Se usa sólo para auditoría y para la guarda de la carrera:
    /// en ninguno de los dos casos un fallo de red debe cortar la operación.
    /// </summary>
    private async Task<string?> ConsultarEstadoRemotoAsync(
        string preapprovalId, CancellationToken ct)
    {
        try
        {
            var remota = await _mercadoPago.ObtenerSuscripcionAsync(preapprovalId, ct);
            return remota.Status;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "No se pudo leer el estado del preapproval {Preapproval} en " +
                "MercadoPago antes de mutarlo.", preapprovalId);

            return null;
        }
    }

    /// <summary>
    /// Vuelve a consultar MercadoPago y sincroniza estado Y cuotas.
    ///
    /// Es la red de seguridad ante notificaciones perdidas: las que ocurrieron
    /// antes de configurar el webhook, las que fallaron tras los 5 reintentos,
    /// o cualquier ventana en que el servicio estuvo caído. Por eso recupera
    /// también los pagos: sincronizar sólo el estado dejaría la cobranza en
    /// cero aunque MercadoPago ya hubiera cobrado.
    ///
    /// Es idempotente: el MERGE sobre MpAuthorizedPaymentId evita duplicar
    /// cuotas que ya estaban registradas.
    /// </summary>
    public async Task<SuscripcionEstadoDto?> SincronizarAsync(
        int idSuscripcion, CancellationToken ct = default)
    {
        var suscripcion = await _repositorio.ObtenerPorIdAsync(idSuscripcion, ct);

        if (suscripcion?.PreapprovalId is null) return suscripcion;

        var remota = await _mercadoPago
            .ObtenerSuscripcionAsync(suscripcion.PreapprovalId, ct);

        /* Sincronizar COPIA el estado de MercadoPago; nunca lo decide. Queda
           auditado con la misma línea que el webhook para que la reconstrucción
           de "quién cambió qué" no dependa de por dónde entró el cambio. */
        AuditoriaPreapproval.Recibida(
            _log,
            suscripcion.PreapprovalId,
            remota.ExternalReference ?? suscripcion.ExternalReference,
            suscripcion.IdCotizacion,
            suscripcion.Estado,
            remota.Status,
            AuditoriaPreapproval.Correlacion());

        await _repositorio.ActualizarEstadoAsync(
            suscripcion.PreapprovalId,
            EstadoSuscripcion.ParaPersistir(remota.Status ?? suscripcion.Estado!),
            remota.NextPaymentDate?.LocalDateTime,
            null,
            ct);

        await SincronizarPagosAsync(suscripcion, ct);

        return await ObtenerAsync(idSuscripcion, ct);
    }

    /// <summary>
    /// Trae de MercadoPago todas las cuotas de la suscripción y las registra.
    /// Un fallo acá no invalida la sincronización del estado, que es lo
    /// principal; se registra y se sigue.
    /// </summary>
    private async Task SincronizarPagosAsync(
        SuscripcionEstadoDto suscripcion, CancellationToken ct)
    {
        try
        {
            var busqueda = await _mercadoPago
                .BuscarPagosAutorizadosAsync(suscripcion.PreapprovalId!, ct);

            /* Las cuotas ya registradas con neto no se vuelven a consultar: una
               suscripción de 36 meses son 36 GET extra por sincronización, y las
               cuotas viejas ya liberadas no cambian más. Sólo se pide el pago
               completo de las aprobadas que todavía no tienen el dato. */
            var yaTienenNeto = (await _repositorio
                    .ListarPagosAsync(suscripcion.IdSuscripcion, ct))
                .Where(p => p.MontoNeto is not null && p.MpAuthorizedPaymentId is not null)
                .Select(p => p.MpAuthorizedPaymentId!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var cuota in busqueda.Results)
            {
                var idCuota = cuota.Id?.ToString();

                var liberacion =
                    cuota.Payment?.Status == "approved" &&
                    (idCuota is null || !yaTienenNeto.Contains(idCuota))
                        ? await _mercadoPago.ObtenerDatosLiberacionAsync(
                            cuota.Payment?.Id?.ToString(), ct)
                        : DatosLiberacion.Vacio;

                await _repositorio.RegistrarPagoAsync(
                    suscripcion.IdSuscripcion,
                    idCuota,
                    cuota.Payment?.Id?.ToString(),
                    cuota.TransactionAmount ?? suscripcion.MontoMensual,
                    cuota.CurrencyId ?? suscripcion.Moneda ?? _opciones.Moneda,
                    cuota.Status ?? "desconocido",
                    cuota.Payment?.Status,
                    cuota.Payment?.StatusDetail,
                    cuota.DebitDate?.LocalDateTime,
                    cuota.Payment?.Status == "approved"
                        ? cuota.DebitDate?.LocalDateTime
                        : null,
                    JsonSerializer.Serialize(cuota),
                    liberacion.FechaLiberacion,
                    liberacion.MontoNeto,
                    liberacion.Comision,
                    liberacion.Retenciones,
                    liberacion.EstadoLiberacionMp,
                    ct);
            }

            if (busqueda.Results.Count > 0)
            {
                _log.LogInformation(
                    "Sincronizadas {Cantidad} cuotas de la suscripción {Id}.",
                    busqueda.Results.Count, suscripcion.IdSuscripcion);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "No se pudieron sincronizar las cuotas de la suscripción {Id}.",
                suscripcion.IdSuscripcion);
        }
    }

    /// <summary>
    /// Convierte el día elegido por el vendedor en el instante que espera
    /// MercadoPago, o null si corresponde cobrar apenas el cliente autorice.
    ///
    /// Se manda el mediodía y no la medianoche: MercadoPago interpreta
    /// start_date en el huso del envío, y una medianoche exacta queda a un
    /// desfasaje de horas de caer en el día anterior. El mediodía deja seis
    /// horas de margen para cada lado y el día del mes nunca se corre —que es
    /// justamente lo que se quiere fijar.
    ///
    /// Una fecha pasada o de hoy se descarta: no se puede empezar a cobrar
    /// ayer, y MercadoPago devuelve 400 si start_date ya pasó.
    /// </summary>
    private static DateTimeOffset? NormalizarFechaInicio(DateTime? fechaInicio)
    {
        if (!fechaInicio.HasValue)
            return null;

        var dia = fechaInicio.Value.Date;

        if (dia <= DateTime.Today)
            return null;

        return new DateTimeOffset(
            dia.AddHours(12),
            TimeZoneInfo.Local.GetUtcOffset(dia.AddHours(12)));
    }

    private string ArmarConcepto(string nombreCliente)
    {
        var concepto = $"TECNISEGUR ALARMAS - {nombreCliente.Trim()}";

        // MercadoPago trunca los conceptos largos; mejor controlarlo acá.
        return concepto.Length > 255 ? concepto[..255] : concepto;
    }

    /// <summary>
    /// Corta el alta si MercadoPago dice que la cuenta no puede facturar.
    ///
    /// Se apaga con MercadoPago:VerificarFacturacionHabilitada. El interruptor
    /// existe porque la relación entre billing.allow y la autorización de
    /// suscripciones está establecida por experimento —el mismo payload
    /// autoriza contra una cuenta habilitada y falla contra una que no— pero no
    /// por documentación de MercadoPago. Si algún día resulta que no era eso,
    /// esto se apaga por configuración y no hay que publicar nada.
    ///
    /// Sólo bloquea ante un "no" explícito. Si la consulta falla, el alta sigue:
    /// ver <see cref="MercadoPagoCliente.ObtenerEstadoCuentaAsync"/>.
    /// </summary>
    private async Task VerificarCuentaHabilitadaAsync(CancellationToken ct)
    {
        if (!_opciones.VerificarFacturacionHabilitada) return;

        var estado = _cacheCuenta.Leer();

        if (estado is null)
        {
            estado = await _mercadoPago.ObtenerEstadoCuentaAsync(ct);
            _cacheCuenta.Guardar(estado);
        }

        if (!estado.Bloqueada) return;

        /* Se olvida lo cacheado: si la cuenta se destraba en el minuto
           siguiente, el próximo intento vuelve a preguntar en vez de repetir
           el rechazo durante diez minutos. */
        _cacheCuenta.Invalidar();

        _log.LogError(
            "Alta bloqueada: la cuenta de MercadoPago no tiene habilitada la " +
            "facturación ({Motivos}). Las suscripciones no se pueden autorizar.",
            estado.MotivosTexto);

        throw new ReglaNegocioException(
            "La cuenta de MercadoPago de Tecnisegur no tiene habilitada la " +
            $"facturación ({estado.MotivosTexto}), así que el cliente no podría " +
            "completar la suscripción aunque reciba el link. " +
            "Avise a administración antes de generar el cobro.");
    }

    /// <summary>
    /// Rechaza los correos que no sirven como identidad del suscriptor: los mal
    /// formados, los rellenos tipo NOTIENE@NOTIENE.COM que pone el vendedor
    /// cuando la cotización no tiene correo, y el de la propia cuenta cobradora
    /// —nadie puede suscribirse a sí mismo—.
    ///
    /// Sin esto, MercadoPago contesta un 500 sin cuerpo útil, o peor: crea la
    /// suscripción y el cliente nunca recibe un aviso.
    /// </summary>
    private void ValidarCorreoPagador(string correo)
    {
        var limpio = (correo ?? string.Empty).Trim();

        /* MailAddress es más estricto que el [EmailAddress] del DTO, que da por
           bueno cosas como "a@b". El try/catch es la única forma de usarlo:
           TryCreate existe pero acepta lo mismo que el atributo. */
        string dominio;

        try
        {
            dominio = new MailAddress(limpio).Host.ToLowerInvariant();
        }
        catch (FormatException)
        {
            throw new ReglaNegocioException(
                $"El correo '{limpio}' no es una dirección válida. " +
                "Es el correo al que MercadoPago le avisa cada cobro.");
        }

        if (_opciones.DominiosCorreoVetados
                .Any(d => string.Equals(d.Trim(), dominio, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ReglaNegocioException(
                $"El correo '{limpio}' es un relleno, no una dirección real. " +
                "Cargue el correo del cliente en el contrato antes de generar " +
                "el cobro: es por donde MercadoPago avisa cobros y rechazos.");
        }

        if (!string.IsNullOrWhiteSpace(_opciones.CorreoCobrador) &&
            string.Equals(limpio, _opciones.CorreoCobrador.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ReglaNegocioException(
                "El correo del comprador es el de la cuenta de Tecnisegur. " +
                "MercadoPago no permite que el pagador y el cobrador sean el mismo.");
        }
    }

    /// <summary>
    /// Devuelve null —no cadena vacía— cuando no hay BackUrl configurada, para
    /// que el campo se omita del JSON. MercadoPago valida back_url como URL y
    /// responde 400 si recibe "". El armado en sí vive en UrlRetorno, que lo
    /// comparte con los pagos únicos.
    /// </summary>
    private string? ArmarBackUrl()
    {
        var url = UrlRetorno.Normalizada(_opciones.BackUrl);

        if (url is null)
        {
            _log.LogWarning(
                "MercadoPago:BackUrl ausente o no absoluta ('{BackUrl}'). La " +
                "suscripción se crea igual, pero el cliente no vuelve a ningún " +
                "lado tras autorizar.",
                _opciones.BackUrl);
        }

        return url;
    }

    /// <summary>
    /// El webhook configurado en el panel ya cubre las suscripciones, pero
    /// mandarlo también en el preapproval lo deja atado a esta suscripción en
    /// particular: si alguien toca la configuración del panel, las que ya
    /// existen siguen notificando. Mismo criterio que PagoServicio con las
    /// preferencias de Checkout Pro.
    /// </summary>
    private string? ArmarNotificationUrl()
        => string.IsNullOrWhiteSpace(_opciones.UrlWebhook)
            ? null
            : _opciones.UrlWebhook.Trim();

    /// <summary>
    /// Cancela la suscripción que acaba de crearse cuando el índice único
    /// rechazó su insert. Es el ÚNICO PUT de cancelación que este sistema manda
    /// sin que un usuario lo pida, así que lleva dos protecciones.
    ///
    /// La primera es la guarda de estado: la suscripción huérfana tiene
    /// segundos de vida y no puede estar en otra cosa que "pending". Si
    /// MercadoPago informa cualquier otro estado, el supuesto de esta rama —que
    /// se está limpiando un sobrante que nadie autorizó— no se cumple, y se
    /// prefiere dejar el preapproval vivo para limpieza manual antes que
    /// arriesgarse a cortarle el cobro a un cliente que ya puso la tarjeta.
    /// Recuperar una suscripción cancelada no se puede; borrar una huérfana a
    /// mano sí.
    ///
    /// La segunda es el rastro de auditoría, que deja escrito que la baja salió
    /// de acá y no de MercadoPago.
    /// </summary>
    private async Task CancelarEnMercadoPagoSilencioso(
        string preapprovalId, int idCotizacion, CancellationToken ct)
    {
        var correlacion = AuditoriaPreapproval.Correlacion();
        var estadoRemoto = await ConsultarEstadoRemotoAsync(preapprovalId, ct);

        if (estadoRemoto is not null and not "pending")
        {
            _log.LogError(
                "AUDITORIA-PREAPPROVAL {InstanteUtc:o} operacion=CANCELAR " +
                "direccion=ABORTADA origen={Origen} preapproval={Preapproval} " +
                "estado_remoto_anterior={EstadoRemoto} cotizacion={Cotizacion} " +
                "correlacion={Correlacion} — NO se cancela: la suscripción no " +
                "está 'pending'. Revisar a mano si quedó huérfana.",
                DateTime.UtcNow, AuditoriaPreapproval.Origen.CarreraIndiceUnico,
                preapprovalId, estadoRemoto, idCotizacion, correlacion);

            return;
        }

        AuditoriaPreapproval.Emitida(
            _log,
            operacion: "CANCELAR",
            origen: AuditoriaPreapproval.Origen.CarreraIndiceUnico,
            preapprovalId: preapprovalId,
            externalReference: $"COT-{idCotizacion}",
            idCotizacion: idCotizacion,
            estadoLocalAnterior: "(sin fila: el insert fue rechazado)",
            estadoRemotoAnterior: estadoRemoto,
            estadoNuevo: "cancelled",
            detalle: "El índice único rechazó el insert; se limpia el sobrante.",
            correlacion: correlacion);

        try
        {
            await _mercadoPago.CancelarSuscripcionAsync(
                preapprovalId,
                $"{AuditoriaPreapproval.Origen.CarreraIndiceUnico} (correlacion={correlacion})",
                ct);
        }
        catch (Exception ex)
        {
            // Queda registrado para limpieza manual: no vale la pena romper
            // la respuesta al usuario por esto.
            _log.LogError(ex,
                "No se pudo cancelar la suscripción huérfana {Preapproval}.",
                preapprovalId);
        }
    }
}
