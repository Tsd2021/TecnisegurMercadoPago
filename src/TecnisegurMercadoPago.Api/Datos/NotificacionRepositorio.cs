using System.Data;
using Microsoft.Data.SqlClient;

namespace TecnisegurMercadoPago.Api.Datos;

public sealed record NotificacionPendiente(
    int Id,
    string MpNotificationId,
    string Tipo,
    string? Accion,
    string? DataId,
    string PayloadJson,
    int IntentosProceso);

/// <summary>
/// Log crudo de webhooks. El endpoint guarda acá y responde 200 de inmediato;
/// el procesamiento real corre después, desacoplado (MercadoPago exige respuesta
/// en menos de 22 segundos y reintenta cada 15 minutos si no la recibe).
/// </summary>
public sealed class NotificacionRepositorio
{
    private readonly string _cadena;

    public NotificacionRepositorio(IConfiguration configuracion)
    {
        _cadena = configuracion.GetConnectionString("TSD")
                  ?? throw new InvalidOperationException(
                      "Falta la cadena de conexión 'TSD'.");
    }

    private SqlConnection Conexion() => new(_cadena);

    /// <summary>
    /// Guarda la notificación. Devuelve false si ya estaba registrada
    /// (MercadoPago reenvía la misma notificación ante cualquier duda).
    /// </summary>
    public async Task<bool> GuardarAsync(
        string mpNotificationId,
        string tipo,
        string? accion,
        string? dataId,
        string payloadJson,
        bool firmaValida,
        CancellationToken ct = default)
    {
        const string sql = @"
            IF NOT EXISTS (SELECT 1 FROM dbo.MercadoPagoNotificacion
                           WHERE MpNotificationId = @MpId)
            BEGIN
                INSERT INTO dbo.MercadoPagoNotificacion
                    (MpNotificationId, Tipo, Accion, DataId,
                     PayloadJson, FirmaValida, FechaRecepcion, Procesado, IntentosProceso)
                VALUES
                    (@MpId, @Tipo, @Accion, @DataId,
                     @Payload, @FirmaValida, GETDATE(), 0, 0);

                SELECT 1;
            END
            ELSE
                SELECT 0;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@MpId", SqlDbType.VarChar, 64).Value = mpNotificationId;
        cmd.Parameters.Add("@Tipo", SqlDbType.VarChar, 50).Value = tipo;
        cmd.Parameters.Add("@Accion", SqlDbType.VarChar, 50).Value = (object?)accion ?? DBNull.Value;
        cmd.Parameters.Add("@DataId", SqlDbType.VarChar, 64).Value = (object?)dataId ?? DBNull.Value;
        cmd.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = payloadJson;
        cmd.Parameters.Add("@FirmaValida", SqlDbType.Bit).Value = firmaValida;

        var resultado = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(resultado) == 1;
    }

    /// <summary>
    /// Notificaciones sin procesar. Se limita a 5 intentos para que un payload
    /// defectuoso no quede reintentándose para siempre.
    /// </summary>
    public async Task<List<NotificacionPendiente>> ObtenerPendientesAsync(
        int maximo, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TOP (@Maximo)
                   Id, MpNotificationId, Tipo, Accion, DataId,
                   PayloadJson, IntentosProceso
            FROM dbo.MercadoPagoNotificacion
            WHERE Procesado = 0
              AND IntentosProceso < 5
              AND FirmaValida = 1
            ORDER BY FechaRecepcion;";

        var lista = new List<NotificacionPendiente>();

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Maximo", SqlDbType.Int).Value = maximo;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lista.Add(new NotificacionPendiente(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6)));
        }

        return lista;
    }

    /// <summary>
    /// <paramref name="nota"/> registra por qué una notificación se procesó sin
    /// cambiar nada —típicamente que el preapproval no tiene fila local—. No es
    /// un error: la notificación se procesó bien, sólo que no había a quién
    /// aplicarla. Sin esta columna ese caso es indistinguible en la base de una
    /// sincronización normal, y el único rastro queda en el stdout del servidor,
    /// que es rotativo.
    /// </summary>
    public async Task MarcarProcesadaAsync(
        int id, bool exito, string? error, string? nota = null, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE dbo.MercadoPagoNotificacion
            SET Procesado       = @Exito,
                FechaProceso    = CASE WHEN @Exito = 1 THEN GETDATE() ELSE FechaProceso END,
                IntentosProceso = IntentosProceso + 1,
                ErrorProceso    = @Error,
                NotaProceso     = @Nota
            WHERE Id = @Id;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = id;
        cmd.Parameters.Add("@Exito", SqlDbType.Bit).Value = exito;
        cmd.Parameters.Add("@Error", SqlDbType.NVarChar, 1000).Value = (object?)error ?? DBNull.Value;
        cmd.Parameters.Add("@Nota", SqlDbType.NVarChar, 400).Value = (object?)nota ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
