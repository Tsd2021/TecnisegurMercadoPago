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
                await ProcesarUnaAsync(pendiente, suscripciones, mercadoPago, pagos, ct);
                await notificaciones.MarcarProcesadaAsync(pendiente.Id, true, null, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "No se pudo procesar la notificación {Id} ({Tipo}). Intento {Intento}.",
                    pendiente.MpNotificationId, pendiente.Tipo, pendiente.IntentosProceso + 1);

                var error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                await notificaciones.MarcarProcesadaAsync(pendiente.Id, false, error, ct);
            }
        }
    }

    private async Task ProcesarUnaAsync(
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
            return;
        }

        switch (pendiente.Tipo)
        {
            /* Alta o cambio de estado de la suscripción */
            case "subscription_preapproval":
                await SincronizarSuscripcionAsync(
                    pendiente.DataId, suscripciones, mercadoPago, ct);
                break;

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
                break;
        }
    }

    private async Task SincronizarSuscripcionAsync(
        string preapprovalId,
        SuscripcionRepositorio suscripciones,
        MercadoPagoCliente mercadoPago,
        CancellationToken ct)
    {
        var remota = await mercadoPago.ObtenerSuscripcionAsync(preapprovalId, ct);

        var local = await suscripciones.ObtenerPorPreapprovalAsync(preapprovalId, ct);

        if (local is null)
        {
            // Suscripción creada fuera del sistema (por ejemplo, a mano en el
            // panel). Se registra el hecho pero no se inventa una fila.
            _log.LogWarning(
                "Notificación de la suscripción {Preapproval} sin registro local " +
                "(external_reference={Referencia}).",
                preapprovalId, remota.ExternalReference);
            return;
        }

        await suscripciones.ActualizarEstadoAsync(
            preapprovalId,
            remota.Status ?? local.Estado!,
            remota.NextPaymentDate?.LocalDateTime,
            remota.Status == "cancelled" ? "Cancelada en MercadoPago" : null,
            ct);

        _log.LogInformation(
            "Suscripción {Preapproval} sincronizada. Estado: {Estado}.",
            preapprovalId, remota.Status);
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
            ct);

        _log.LogInformation(
            "Cuota {Id} registrada en la suscripción {Suscripcion}. Estado: {Estado}.",
            authorizedPaymentId, local.IdSuscripcion, cuota.Payment?.Status);
    }
}
