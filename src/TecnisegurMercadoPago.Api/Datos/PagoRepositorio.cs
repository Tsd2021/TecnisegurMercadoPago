using System.Data;
using Microsoft.Data.SqlClient;
using TecnisegurMercadoPago.Api.Modelos.Contratos;

namespace TecnisegurMercadoPago.Api.Datos;

/// <summary>Fila reservada antes de crear la preferencia en MercadoPago.</summary>
public sealed record ReservaPago(int Id, string ExternalReference);

/// <summary>
/// Acceso a datos de los cobros únicos (Checkout Pro). Misma convención que
/// SuscripcionRepositorio: ADO.NET directo, parámetros tipados, conexión en un
/// using, y lecturas contra la vista y no contra la tabla.
/// </summary>
public sealed class PagoRepositorio
{
    private readonly string _cadena;
    private readonly ILogger<PagoRepositorio> _log;

    public PagoRepositorio(
        IConfiguration configuracion,
        ILogger<PagoRepositorio> log)
    {
        _cadena = configuracion.GetConnectionString("TSD")
                  ?? throw new InvalidOperationException(
                      "Falta la cadena de conexión 'TSD'.");
        _log = log;
    }

    private SqlConnection Conexion() => new(_cadena);

    /// <summary>
    /// Reserva la fila local ANTES de crear la preferencia en MercadoPago, y
    /// devuelve el ExternalReference que hay que mandarle.
    ///
    /// El orden está invertido respecto de las suscripciones a propósito. Allá
    /// se crea primero en MercadoPago porque el external_reference sólo depende
    /// de la cotización; acá depende del Id local, que no existe hasta insertar.
    ///
    /// El efecto colateral es favorable: si MercadoPago falla, lo que queda
    /// huérfano es una fila local sin preferencia —inofensiva y que se borra—
    /// en lugar de un recurso vivo en MercadoPago capaz de cobrarle a alguien.
    ///
    /// Devuelve null si el índice único filtrado rechazó el insert, es decir,
    /// si esa cotización ya tiene un pago pendiente sin resolver.
    /// </summary>
    public async Task<ReservaPago?> ReservarAsync(
        int idCotizacion,
        string nombreCliente,
        string payerEmail,
        decimal monto,
        string moneda,
        string? concepto,
        string? usuarioCreacion,
        string? origen,
        CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO dbo.PagoUnico
            (
                IdCotizacion, NombreCliente, PayerEmail, Monto, Moneda,
                Concepto, Estado, FechaCreacion, UsuarioCreacion, Origen,
                FechaActualizacion
            )
            VALUES
            (
                @IdCotizacion, @NombreCliente, @PayerEmail, @Monto, @Moneda,
                @Concepto, 'pendiente', GETDATE(), @UsuarioCreacion, @Origen,
                GETDATE()
            );

            SELECT Id, ExternalReference
            FROM dbo.PagoUnico
            WHERE Id = SCOPE_IDENTITY();";

        try
        {
            await using var cn = Conexion();
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add("@IdCotizacion", SqlDbType.Int).Value = idCotizacion;
            cmd.Parameters.Add("@NombreCliente", SqlDbType.VarChar, 200).Value = nombreCliente;
            cmd.Parameters.Add("@PayerEmail", SqlDbType.VarChar, 200).Value = payerEmail;
            cmd.Parameters.Add("@Monto", SqlDbType.Decimal).Value = monto;
            cmd.Parameters.Add("@Moneda", SqlDbType.Char, 3).Value = moneda;
            cmd.Parameters.Add("@Concepto", SqlDbType.VarChar, 255).Value = (object?)concepto ?? DBNull.Value;
            cmd.Parameters.Add("@UsuarioCreacion", SqlDbType.VarChar, 50).Value = (object?)usuarioCreacion ?? DBNull.Value;
            cmd.Parameters.Add("@Origen", SqlDbType.VarChar, 20).Value = (object?)origen ?? DBNull.Value;

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct)) return null;

            return new ReservaPago(reader.GetInt32(0), reader.GetString(1));
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Índice único filtrado: ya hay un pago pendiente para esa cotización.
            _log.LogWarning(
                "Alta de pago rechazada por índice único para la cotización {Id}.",
                idCotizacion);

            return null;
        }
    }

    /// <summary>
    /// Completa la fila reservada con los datos que devolvió MercadoPago.
    /// </summary>
    public async Task AsignarPreferenciaAsync(
        int idPago,
        string mpPreferenceId,
        string initPoint,
        CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE dbo.PagoUnico
            SET MpPreferenceId     = @PreferenceId,
                InitPoint          = @InitPoint,
                FechaActualizacion = GETDATE()
            WHERE Id = @Id;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = idPago;
        cmd.Parameters.Add("@PreferenceId", SqlDbType.VarChar, 64).Value = mpPreferenceId;
        cmd.Parameters.Add("@InitPoint", SqlDbType.VarChar, 500).Value = initPoint;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Borra una reserva que nunca llegó a tener preferencia, para que el
    /// índice de "pendiente" no bloquee los intentos siguientes. Sólo borra si
    /// sigue sin preferencia asignada: nunca toca un pago real.
    /// </summary>
    public async Task LiberarReservaAsync(int idPago, CancellationToken ct = default)
    {
        const string sql = @"
            DELETE FROM dbo.PagoUnico
            WHERE Id = @Id
              AND MpPreferenceId IS NULL
              AND MpPaymentId IS NULL
              AND Estado = 'pendiente';";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = idPago;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<PagoUnicoDto?> ObtenerPorIdAsync(
        int idPago, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM dbo.vw_PagosUnicosEstado WHERE IdPago = @Id;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = idPago;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Mapear(reader) : null;
    }

    public async Task<PagoUnicoDto?> ObtenerPorExternalReferenceAsync(
        string externalReference, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM dbo.vw_PagosUnicosEstado
            WHERE ExternalReference = @Referencia;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Referencia", SqlDbType.VarChar, 64).Value = externalReference;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Mapear(reader) : null;
    }

    public async Task<List<PagoUnicoDto>> ListarPorCotizacionAsync(
        int idCotizacion, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM dbo.vw_PagosUnicosEstado
            WHERE IdCotizacion = @IdCotizacion
            ORDER BY FechaCreacion DESC;";

        var lista = new List<PagoUnicoDto>();

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@IdCotizacion", SqlDbType.Int).Value = idCotizacion;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) lista.Add(Mapear(reader));

        return lista;
    }

    /// <summary>
    /// Registra el resultado del cobro que llegó por webhook.
    ///
    /// Se busca por ExternalReference, que es la única pista que trae el pago.
    /// Es idempotente: MercadoPago notifica el mismo pago más de una vez y la
    /// fila simplemente se sobrescribe con el mismo contenido.
    ///
    /// Devuelve false si no hay fila local para esa referencia (pago creado
    /// fuera del sistema); el llamador lo registra y sigue.
    /// </summary>
    public async Task<bool> RegistrarResultadoAsync(
        string externalReference,
        string mpPaymentId,
        string estado,
        string? estadoDetalle,
        decimal? monto,
        string? moneda,
        DateTime? fechaPago,
        string payloadJson,
        CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE dbo.PagoUnico
            SET MpPaymentId        = @MpPaymentId,
                Estado             = @Estado,
                EstadoDetalle      = @EstadoDetalle,
                Monto              = ISNULL(@Monto, Monto),
                Moneda             = ISNULL(@Moneda, Moneda),
                FechaPago          = @FechaPago,
                PayloadJson        = @Payload,
                FechaActualizacion = GETDATE()
            WHERE ExternalReference = @Referencia;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Referencia", SqlDbType.VarChar, 64).Value = externalReference;
        cmd.Parameters.Add("@MpPaymentId", SqlDbType.VarChar, 64).Value = mpPaymentId;
        cmd.Parameters.Add("@Estado", SqlDbType.VarChar, 30).Value = estado;
        cmd.Parameters.Add("@EstadoDetalle", SqlDbType.VarChar, 100).Value = (object?)estadoDetalle ?? DBNull.Value;
        cmd.Parameters.Add("@Monto", SqlDbType.Decimal).Value = (object?)monto ?? DBNull.Value;
        cmd.Parameters.Add("@Moneda", SqlDbType.Char, 3).Value = (object?)moneda ?? DBNull.Value;
        cmd.Parameters.Add("@FechaPago", SqlDbType.DateTime).Value = (object?)fechaPago ?? DBNull.Value;
        cmd.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = payloadJson;

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static PagoUnicoDto Mapear(SqlDataReader r)
    {
        string? Texto(string columna)
        {
            var i = r.GetOrdinal(columna);
            return r.IsDBNull(i) ? null : r.GetString(i);
        }

        DateTime? Fecha(string columna)
        {
            var i = r.GetOrdinal(columna);
            return r.IsDBNull(i) ? null : r.GetDateTime(i);
        }

        return new PagoUnicoDto
        {
            IdPago            = r.GetInt32(r.GetOrdinal("IdPago")),
            IdCotizacion      = r.GetInt32(r.GetOrdinal("IdCotizacion")),
            ExternalReference = Texto("ExternalReference"),
            MpPreferenceId    = Texto("MpPreferenceId"),
            MpPaymentId       = Texto("MpPaymentId"),
            InitPoint         = Texto("InitPoint"),
            Concepto          = Texto("Concepto"),
            NombreCliente     = Texto("NombreCliente"),
            PayerEmail        = Texto("PayerEmail"),
            Monto             = r.GetDecimal(r.GetOrdinal("Monto")),
            Moneda            = Texto("Moneda"),
            Estado            = Texto("Estado"),
            EstadoDescripcion = Texto("EstadoDescripcion"),
            EstadoDetalle     = Texto("EstadoDetalle"),
            FechaCreacion     = r.GetDateTime(r.GetOrdinal("FechaCreacion")),
            FechaPago         = Fecha("FechaPago"),
            Origen            = Texto("Origen"),
            UsuarioCreacion   = Texto("UsuarioCreacion")
        };
    }
}
