using System.Text.Json;
using Microsoft.Data.SqlClient;
using TecnisegurMercadoPago.Api.Datos;
using TecnisegurMercadoPago.Api.Modelos.MercadoPago;

namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Procesa en segundo plano las notificaciones que el webhook dejó guardadas.
///
/// El webhook NO procesa en línea: MercadoPago exige respuesta en menos de
/// 22 segundos y reintenta cada 15 minutos si no la recibe. Consultar la API
/// de MercadoPago dentro del request pondría esa ventana en riesgo.
/// </summary>
public sealed class ProcesadorNotificaciones : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(15);
    private const int LotePorCiclo = 20;

    /// <summary>
    /// Cada cuánto se repasan las cuotas cuya liberación todavía no se puede dar
    /// por cumplida. Una vez por día alcanza: la liberación tarda 21 días y un
    /// contracargo no se resuelve en horas.
    /// </summary>
    private static readonly TimeSpan IntervaloRepasoLiberacion = TimeSpan.FromHours(24);

    private const int LoteRepasoLiberacion = 100;

    private DateTime _proximoRepasoLiberacion = DateTime.MinValue;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProcesadorNotificaciones> _log;

    public ProcesadorNotificaciones(
        IServiceScopeFactory scopeFactory,
        ILogger<ProcesadorNotificaciones> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Procesador de notificaciones iniciado.");

        using var timer = new PeriodicTimer(Intervalo);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcesarLoteAsync(ct);
                await RepasarLiberacionesAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SqlException ex)
            {
                /* La base caída o mal configurada es un problema de entorno, no
                 * un bug. Como el ciclo corre cada 15 s, volcar el stack trace
                 * completo cada vez inunda la ventana de salida y tapa lo que
                 * sí importa. Una línea alcanza; el detalle queda en Debug. */
                _log.LogWarning(
                    "No se pudo consultar la base ({Numero}): {Mensaje}",
                    ex.Number, ex.Message);

                _log.LogDebug(ex, "Detalle del error de base de datos.");
            }
            catch (Exception ex)
            {
                // Nunca dejar morir el BackgroundService por un error de un ciclo.
                _log.LogError(ex, "Error en el ciclo de procesamiento.");
            }

            try
            {
                await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _log.LogInformation("Procesador de notificaciones detenido.");
    }

    /// <summary>
    /// Repasa contra MercadoPago los cobros cuya liberación no se puede dar por
    /// cumplida —cuotas mensuales y pagos únicos— y actualiza fecha, neto,
    /// comisión, retenciones y estado de liberación.
    ///
    /// EXISTE PORQUE NO HAY WEBHOOK DE LIBERACIÓN. Se verificó contra la lista
    /// oficial de tópicos el 31/07/2026: MercadoPago avisa de pagos, órdenes,
    /// suscripciones, contracargos y fraude, pero no de que el dinero se liberó.
    ///
    /// Lo que sí hace, y no sabíamos hasta el 31/07, es informar
    /// money_release_status en el pago. Así que reconsultar no sólo detecta la
    /// reversión: confirma la liberación. Sigue sin ser un asiento contable
    /// —para eso está el reporte de Liberaciones— pero deja de ser una promesa
    /// que nadie vuelve a verificar.
    ///
    /// Sin este repaso, la pantalla de cobranzas diría "disponible" el día
    /// previsto aunque el dinero se hubiera revertido tres semanas antes.
    /// </summary>
    private async Task RepasarLiberacionesAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _proximoRepasoLiberacion) return;

        /* Se agenda ANTES de trabajar: si el repaso falla, el próximo intento va
           dentro de 24 h y no en el ciclo siguiente, que serían 15 segundos. */
        _proximoRepasoLiberacion = DateTime.UtcNow.Add(IntervaloRepasoLiberacion);

        using var scope = _scopeFactory.CreateScope();

        var mercadoPago = scope.ServiceProvider
            .GetRequiredService<MercadoPagoCliente>();

        await RepasarCuotasAsync(scope, mercadoPago, ct);
        await RepasarPagosUnicosAsync(scope, mercadoPago, ct);
    }

    private async Task RepasarCuotasAsync(
        IServiceScope scope, MercadoPagoCliente mercadoPago, CancellationToken ct)
    {
        var suscripciones = scope.ServiceProvider
            .GetRequiredService<SuscripcionRepositorio>();

        var cuotas = await suscripciones.ListarCuotasSinLiberacionConfirmadaAsync(
            tope: LoteRepasoLiberacion, ct: ct);

        if (cuotas.Count == 0) return;

        _log.LogInformation(
            "Repasando la liberación de {Cantidad} cuotas.", cuotas.Count);

        var actualizadas = 0;

        foreach (var cuota in cuotas)
        {
            if (ct.IsCancellationRequested) break;

            var datos = await mercadoPago.ObtenerDatosLiberacionAsync(cuota.PaymentId, ct);

            /* Sin datos no se toca nada: ObtenerDatosLiberacionAsync ya devolvió
               Vacio ante un fallo, y escribir null sobre lo que había sería
               perder información por un problema de red. */
            if (!datos.HayDatos && datos.Estado is null) continue;

            await suscripciones.ActualizarLiberacionAsync(
                cuota.Id,
                datos.Estado,
                datos.DetalleEstado,
                datos.FechaLiberacion,
                datos.MontoNeto,
                datos.Comision,
                datos.Retenciones,
                datos.EstadoLiberacionMp,
                ct);

            actualizadas++;

            /* Una reversión es plata que se creía cobrada y no entró. Merece
               warning y no debug: es exactamente lo que este repaso existe para
               encontrar, y alguien tiene que enterarse. */
            if (datos.Estado is "refunded" or "charged_back" or "cancelled")
            {
                _log.LogWarning(
                    "La cuota {Id} de la suscripción {Suscripcion} pasó a {Estado}: " +
                    "el dinero no se va a liberar.",
                    cuota.Id, cuota.IdSuscripcion, datos.Estado);
            }
        }

        _log.LogInformation(
            "Repaso de cuotas terminado. {Actualizadas} de {Total} actualizadas.",
            actualizadas, cuotas.Count);
    }

    /// <summary>
    /// Lo mismo para los cobros de equipamiento. Se repasan aparte porque viven
    /// en otra tabla, pero por la misma razón —y con más motivo: son los de
    /// importe más alto, así que un contracargo que pase inadvertido acá cuesta
    /// bastante más que una cuota mensual.
    /// </summary>
    private async Task RepasarPagosUnicosAsync(
        IServiceScope scope, MercadoPagoCliente mercadoPago, CancellationToken ct)
    {
        var pagos = scope.ServiceProvider.GetRequiredService<PagoRepositorio>();

        var pendientes = await pagos.ListarPagosSinLiberacionConfirmadaAsync(
            tope: LoteRepasoLiberacion, ct: ct);

        if (pendientes.Count == 0) return;

        _log.LogInformation(
            "Repasando la liberación de {Cantidad} pagos únicos.", pendientes.Count);

        var actualizados = 0;

        foreach (var pago in pendientes)
        {
            if (ct.IsCancellationRequested) break;

            var datos = await mercadoPago.ObtenerDatosLiberacionAsync(pago.PaymentId, ct);

            if (!datos.HayDatos && datos.Estado is null) continue;

            await pagos.ActualizarLiberacionAsync(
                pago.Id,
                datos.Estado,
                datos.DetalleEstado,
                datos.FechaLiberacion,
                datos.MontoNeto,
                datos.Comision,
                datos.Retenciones,
                datos.EstadoLiberacionMp,
                ct);

            actualizados++;

            if (datos.Estado is "refunded" or "charged_back" or "cancelled")
            {
                _log.LogWarning(
                    "El pago único {Id} de la cotización {Cotizacion} pasó a {Estado}: " +
                    "el dinero no se va a liberar.",
                    pago.Id, pago.IdCotizacion, datos.Estado);
            }
        }

        _log.LogInformation(
            "Repaso de pagos únicos terminado. {Actualizados} de {Total} actualizados.",
            actualizados, pendientes.Count);
    }

    private async Task ProcesarLoteAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        var notificaciones = scope.ServiceProvider
            .GetRequiredService<NotificacionRepositorio>();

        var pendientes = await notificaciones.ObtenerPendientesAsync(LotePorCiclo, ct);

        if (pendientes.Count == 0) return;

        _log.LogInformation("Procesando {Cantidad} notificaciones.", pendientes.Count);

        var suscripciones = scope.ServiceProvider
            .GetRequiredService<SuscripcionRepositorio>();

        var mercadoPago = scope.ServiceProvider
            .GetRequiredService<MercadoPagoCliente>();

        var pagos = scope.ServiceProvider
            .GetRequiredService<PagoServicio>();

        foreach (var pendiente in pendientes)
        {
            try
            {
                /* La nota describe un proceso correcto que no cambió nada: no
                   es un error y por eso no va en ErrorProceso. NULL es el caso
                   normal, en que la notificación sí encontró a quién aplicarse. */
                var nota = await ProcesarUnaAsync(
                    pendiente, suscripciones, mercadoPago, pagos, ct);

                await notificaciones.MarcarProcesadaAsync(pendiente.Id, true, null, nota, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "No se pudo procesar la notificación {Id} ({Tipo}). Intento {Intento}.",
                    pendiente.MpNotificationId, pendiente.Tipo, pendiente.IntentosProceso + 1);

                var error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                await notificaciones.MarcarProcesadaAsync(pendiente.Id, false, error, null, ct);
            }
        }
    }

    /// <summary>
    /// Devuelve NULL cuando la notificación se aplicó sobre algo, y una nota
    /// cuando se procesó correctamente pero sin efecto. Ese segundo caso no es
    /// un error —no hay nada que reintentar— pero tiene que quedar registrado:
    /// sin la nota, en la base es idéntico a una sincronización normal.
    /// </summary>
    private async Task<string?> ProcesarUnaAsync(
        NotificacionPendiente pendiente,
        SuscripcionRepositorio suscripciones,
        MercadoPagoCliente mercadoPago,
        PagoServicio pagos,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pendiente.DataId))
        {
            _log.LogWarning(
                "Notificación {Id} sin data.id: se descarta.", pendiente.MpNotificationId);
            return "Descartada: la notificación no trae data.id.";
        }

        switch (pendiente.Tipo)
        {
            /* Alta o cambio de estado de la suscripción */
            case "subscription_preapproval":
                return await SincronizarSuscripcionAsync(
                    pendiente.DataId, suscripciones, mercadoPago, ct);

            /* Cuota generada o cobrada */
            case "subscription_authorized_payment":
                await RegistrarCuotaAsync(
                    pendiente.DataId, suscripciones, mercadoPago, ct);
                break;

            /* Cobro único de Checkout Pro (equipamiento, insumos).
             *
             * Este tipo también llega por las cuotas de una suscripción, que ya
             * se registran arriba. PagoServicio descarta esos casos mirando el
             * preapproval_id y el prefijo del external_reference: sin ese filtro
             * el mismo cobro se contaría dos veces. */
            case "payment":
                await pagos.RegistrarPagoNotificadoAsync(pendiente.DataId, ct);
                break;

            default:
                _log.LogInformation(
                    "Tipo de notificación no manejado: {Tipo}.", pendiente.Tipo);
                return $"Tipo no manejado: {pendiente.Tipo}.";
        }

        return null;
    }

    private async Task<string?> SincronizarSuscripcionAsync(
        string preapprovalId,
        SuscripcionRepositorio suscripciones,
        MercadoPagoCliente mercadoPago,
        CancellationToken ct)
    {
        var remota = await mercadoPago.ObtenerSuscripcionAsync(preapprovalId, ct);

        var local = await suscripciones.ObtenerPorPreapprovalAsync(preapprovalId, ct);

        if (local is null)
        {
            /* Suscripción creada fuera del sistema (a mano en el panel, o por el
               script de diagnóstico), o cuya fila ya no está. Se registra el
               hecho y no se inventa una fila. La nota vuelve hasta el UPDATE de
               la notificación: el warning del log no alcanza, porque el stdout
               del servidor rota y esto se necesita meses después. */
            return AuditoriaPreapproval.SinRegistroLocal(
                _log,
                preapprovalId,
                remota.ExternalReference,
                remota.Status,
                AuditoriaPreapproval.Correlacion());
        }

        /* El estado que se escribe sale SIEMPRE del GET que se acaba de hacer,
           no del cuerpo de la notificación. Por eso una notificación vieja
           reprocesada no puede degradar un estado más nuevo: escribe lo que
           MercadoPago dice ahora, no lo que decía cuando se emitió. */
        AuditoriaPreapproval.Recibida(
            _log,
            preapprovalId,
            remota.ExternalReference ?? local.ExternalReference,
            local.IdCotizacion,
            local.Estado,
            remota.Status,
            AuditoriaPreapproval.Correlacion());

        /* ParaPersistir traduce cualquier ortografía de "cancelada" al valor de
           contrato: si MercadoPago empezara a responder 'canceled', escribirlo
           verbatim dejaría a TSD y a EmpleadoWeb sin reconocer la baja. */
        await suscripciones.ActualizarEstadoAsync(
            preapprovalId,
            EstadoSuscripcion.ParaPersistir(remota.Status ?? local.Estado!),
            remota.NextPaymentDate?.LocalDateTime,
            EstadoSuscripcion.EsCancelada(remota.Status) ? "Cancelada en MercadoPago" : null,
            ct);

        _log.LogInformation(
            "Suscripción {Preapproval} sincronizada. Estado: {Estado} (antes {Anterior}).",
            preapprovalId, remota.Status, local.Estado);

        return null;
    }

    private async Task RegistrarCuotaAsync(
        string authorizedPaymentId,
        SuscripcionRepositorio suscripciones,
        MercadoPagoCliente mercadoPago,
        CancellationToken ct)
    {
        var cuota = await mercadoPago.ObtenerPagoAutorizadoAsync(authorizedPaymentId, ct);

        if (string.IsNullOrWhiteSpace(cuota.PreapprovalId))
        {
            _log.LogWarning(
                "La cuota {Id} no trae preapproval_id.", authorizedPaymentId);
            return;
        }

        var local = await suscripciones
            .ObtenerPorPreapprovalAsync(cuota.PreapprovalId, ct);

        if (local is null)
        {
            _log.LogWarning(
                "Cuota {Id} de una suscripción sin registro local ({Preapproval}).",
                authorizedPaymentId, cuota.PreapprovalId);
            return;
        }

        /* La cuota no trae ni el neto ni la fecha de liberación: hay que pedir el
           pago completo. Sólo para las aprobadas — una cuota rechazada no libera
           nada y la llamada sería puro gasto. */
        var liberacion = cuota.Payment?.Status == "approved"
            ? await mercadoPago.ObtenerDatosLiberacionAsync(
                cuota.Payment?.Id?.ToString(), ct)
            : DatosLiberacion.Vacio;

        await suscripciones.RegistrarPagoAsync(
            local.IdSuscripcion,
            authorizedPaymentId,
            cuota.Payment?.Id?.ToString(),
            cuota.TransactionAmount ?? local.MontoMensual,
            cuota.CurrencyId ?? local.Moneda ?? "UYU",
            cuota.Status ?? "desconocido",
            cuota.Payment?.Status,
            cuota.Payment?.StatusDetail,
            cuota.DebitDate?.LocalDateTime,
            cuota.Payment?.Status == "approved" ? cuota.DebitDate?.LocalDateTime : null,
            JsonSerializer.Serialize(cuota),
            liberacion.FechaLiberacion,
            liberacion.MontoNeto,
            liberacion.Comision,
            liberacion.Retenciones,
            liberacion.EstadoLiberacionMp,
            ct);

        _log.LogInformation(
            "Cuota {Id} registrada en la suscripción {Suscripcion}. Estado: {Estado}.",
            authorizedPaymentId, local.IdSuscripcion, cuota.Payment?.Status);
    }
}
