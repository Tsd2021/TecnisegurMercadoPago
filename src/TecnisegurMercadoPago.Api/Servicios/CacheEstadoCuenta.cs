namespace TecnisegurMercadoPago.Api.Servicios;

/// <summary>
/// Guarda el estado de facturación de la cuenta por un rato, para no consultar
/// a MercadoPago en cada alta.
///
/// Es singleton: el estado es de la cuenta, no de la request. La ventana es
/// corta a propósito —el día que se destrabe la cuenta nadie va a querer
/// reiniciar la API para que se entere—, pero suficiente para que una ráfaga de
/// altas no dispare una llamada por cada una.
/// </summary>
public sealed class CacheEstadoCuenta
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(10);

    private readonly Lock _candado = new();

    private EstadoCuenta? _estado;
    private DateTimeOffset _vence = DateTimeOffset.MinValue;

    /// <summary>
    /// Devuelve el estado guardado, o null si no hay o ya venció.
    /// </summary>
    public EstadoCuenta? Leer()
    {
        lock (_candado)
        {
            return DateTimeOffset.UtcNow < _vence ? _estado : null;
        }
    }

    /// <summary>
    /// Sólo se guarda lo que MercadoPago respondió de verdad. Un
    /// <see cref="EstadoCuenta.Desconocido"/> —producto de un fallo de red— no
    /// se cachea: si se guardara, un corte de diez segundos dejaría la
    /// verificación apagada diez minutos.
    /// </summary>
    public void Guardar(EstadoCuenta estado)
    {
        if (estado.Habilitada is null) return;

        lock (_candado)
        {
            _estado = estado;
            _vence = DateTimeOffset.UtcNow.Add(Vigencia);
        }
    }

    /// <summary>
    /// Olvida lo guardado. Se usa al detectar el bloqueo para que el próximo
    /// intento vuelva a preguntar en vez de repetir el rechazo por inercia.
    /// </summary>
    public void Invalidar()
    {
        lock (_candado)
        {
            _estado = null;
            _vence = DateTimeOffset.MinValue;
        }
    }
}
