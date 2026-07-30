using System.Data;
using Microsoft.Data.SqlClient;
using TecnisegurMercadoPago.Api.Modelos.Contratos;

namespace TecnisegurMercadoPago.Api.Datos;

/// <summary>
/// Acceso a datos de suscripciones y cuotas. ADO.NET directo, siguiendo la
/// convención del resto de los sistemas (TecnisegurApi, EmpleadoWeb):
/// sin ORM, parámetros siempre tipados, conexión dentro de un using.
/// </summary>
public sealed class SuscripcionRepositorio
{
    private readonly string _cadena;
    private readonly ILogger<SuscripcionRepositorio> _log;

    public SuscripcionRepositorio(
        IConfiguration configuracion,
        ILogger<SuscripcionRepositorio> log)
    {
        _cadena = configuracion.GetConnectionString("TSD")
                  ?? throw new InvalidOperationException(
                      "Falta la cadena de conexión 'TSD'.");
        _log = log;
    }

    private SqlConnection Conexion() => new(_cadena);

    /// <summary>
    /// Devuelve la suscripción viva (pending/authorized/paused) de una cotización,
    /// o null si no hay. Se consulta antes de crear para no duplicar el cobro.
    /// </summary>
    public async Task<SuscripcionEstadoDto?> ObtenerVivaPorCotizacionAsync(
        int idCotizacion, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TOP 1 *
            FROM dbo.vw_SuscripcionesEstado
            WHERE IdCotizacion = @IdCotizacion
              AND Estado IN ('pending', 'authorized', 'paused')
            ORDER BY FechaCreacion DESC;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@IdCotizacion", SqlDbType.Int).Value = idCotizacion;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Mapear(reader) : null;
    }

    public async Task<SuscripcionEstadoDto?> ObtenerPorIdAsync(
        int idSuscripcion, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM dbo.vw_SuscripcionesEstado WHERE IdSuscripcion = @Id;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = idSuscripcion;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Mapear(reader) : null;
    }

    public async Task<SuscripcionEstadoDto?> ObtenerPorPreapprovalAsync(
        string preapprovalId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM dbo.vw_SuscripcionesEstado WHERE PreapprovalId = @Pre;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Pre", SqlDbType.VarChar, 64).Value = preapprovalId;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Mapear(reader) : null;
    }

    public async Task<List<SuscripcionEstadoDto>> ListarPorCotizacionAsync(
        int idCotizacion, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM dbo.vw_SuscripcionesEstado
            WHERE IdCotizacion = @IdCotizacion
            ORDER BY FechaCreacion DESC;";

        var lista = new List<SuscripcionEstadoDto>();

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@IdCotizacion", SqlDbType.Int).Value = idCotizacion;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            lista.Add(Mapear(reader));

        return lista;
    }

    /// <summary>
    /// Inserta la suscripción y devuelve su Id.
    /// Si el índice único la rechaza (ya hay una viva para esa cotización),
    /// devuelve null en vez de propagar la excepción de SQL.
    /// </summary>
    public async Task<int?> InsertarAsync(
        int idCotizacion,
        string externalReference,
        string preapprovalId,
        string initPoint,
        string nombreCliente,
        string payerEmail,
        decimal montoMensual,
        string moneda,
        int? diasPrueba,
        DateTime? fechaInicio,
        string estado,
        DateTime? fechaProximoPago,
        string? usuarioCreacion,
        string? origen,
        CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO dbo.SuscripcionCotizacion
            (
                IdCotizacion, ExternalReference, PreapprovalId, InitPoint,
                NombreCliente, PayerEmail, MontoMensual, Moneda, DiasPrueba,
                FechaInicio, Estado, FechaCreacion, FechaProximoPago,
                UsuarioCreacion, Origen, FechaActualizacion
            )
            VALUES
            (
                @IdCotizacion, @ExternalReference, @PreapprovalId, @InitPoint,
                @NombreCliente, @PayerEmail, @MontoMensual, @Moneda, @DiasPrueba,
                @FechaInicio, @Estado, GETDATE(), @FechaProximoPago,
                @UsuarioCreacion, @Origen, GETDATE()
            );

            SELECT CAST(SCOPE_IDENTITY() AS INT);";

        try
        {
            await using var cn = Conexion();
            await cn.OpenAsync(ct);

            await using var cmd = new SqlCommand(sql, cn);
            cmd.Parameters.Add("@IdCotizacion", SqlDbType.Int).Value = idCotizacion;
            cmd.Parameters.Add("@ExternalReference", SqlDbType.VarChar, 64).Value = externalReference;
            cmd.Parameters.Add("@PreapprovalId", SqlDbType.VarChar, 64).Value = preapprovalId;
            cmd.Parameters.Add("@InitPoint", SqlDbType.VarChar, 500).Value = initPoint;
            cmd.Parameters.Add("@NombreCliente", SqlDbType.VarChar, 200).Value = nombreCliente;
            cmd.Parameters.Add("@PayerEmail", SqlDbType.VarChar, 200).Value = payerEmail;
            cmd.Parameters.Add("@MontoMensual", SqlDbType.Decimal).Value = montoMensual;
            cmd.Parameters.Add("@Moneda", SqlDbType.Char, 3).Value = moneda;
            cmd.Parameters.Add("@DiasPrueba", SqlDbType.Int).Value = (object?)diasPrueba ?? DBNull.Value;
            cmd.Parameters.Add("@FechaInicio", SqlDbType.DateTime).Value = (object?)fechaInicio ?? DBNull.Value;
            cmd.Parameters.Add("@Estado", SqlDbType.VarChar, 20).Value = estado;
            cmd.Parameters.Add("@FechaProximoPago", SqlDbType.DateTime).Value = (object?)fechaProximoPago ?? DBNull.Value;
            cmd.Parameters.Add("@UsuarioCreacion", SqlDbType.VarChar, 50).Value = (object?)usuarioCreacion ?? DBNull.Value;
            cmd.Parameters.Add("@Origen", SqlDbType.VarChar, 20).Value = (object?)origen ?? DBNull.Value;

            var resultado = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt32(resultado);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Violación de índice único: ya existe una suscripción viva.
            _log.LogWarning(
                "Alta rechazada por índice único para la cotización {Id}.", idCotizacion);
            return null;
        }
    }

    public async Task ActualizarEstadoAsync(
        string preapprovalId,
        string estado,
        DateTime? fechaProximoPago,
        string? motivoCancelacion,
        CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE dbo.SuscripcionCotizacion
            SET Estado             = @Estado,
                FechaAutorizacion  = CASE WHEN @Estado = 'authorized' AND FechaAutorizacion IS NULL
                                          THEN GETDATE() ELSE FechaAutorizacion END,
                FechaCancelacion   = CASE WHEN @Estado = 'cancelled' AND FechaCancelacion IS NULL
                                          THEN GETDATE() ELSE FechaCancelacion END,
                MotivoCancelacion  = ISNULL(@Motivo, MotivoCancelacion),
                FechaProximoPago   = ISNULL(@FechaProximoPago, FechaProximoPago),
                FechaActualizacion = GETDATE()
            WHERE PreapprovalId = @PreapprovalId;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@PreapprovalId", SqlDbType.VarChar, 64).Value = preapprovalId;
        cmd.Parameters.Add("@Estado", SqlDbType.VarChar, 20).Value = estado;
        cmd.Parameters.Add("@FechaProximoPago", SqlDbType.DateTime).Value = (object?)fechaProximoPago ?? DBNull.Value;
        cmd.Parameters.Add("@Motivo", SqlDbType.VarChar, 200).Value = (object?)motivoCancelacion ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ActualizarMontoAsync(
        int idSuscripcion, decimal montoMensual, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE dbo.SuscripcionCotizacion
            SET MontoMensual = @Monto, FechaActualizacion = GETDATE()
            WHERE Id = @Id;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = idSuscripcion;
        cmd.Parameters.Add("@Monto", SqlDbType.Decimal).Value = montoMensual;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Registra o actualiza una cuota. El MERGE mantiene la idempotencia:
    /// MercadoPago puede notificar la misma cuota más de una vez.
    /// </summary>
    public async Task RegistrarPagoAsync(
        int idSuscripcion,
        string? authorizedPaymentId,
        string? paymentId,
        decimal monto,
        string moneda,
        string estado,
        string? estadoPago,
        string? detalleEstado,
        DateTime? fechaProgramada,
        DateTime? fechaPago,
        string payloadJson,
        CancellationToken ct = default)
    {
        const string sql = @"
            MERGE dbo.SuscripcionPago AS destino
            USING (SELECT @AuthorizedId AS MpAuthorizedPaymentId) AS origen
                ON destino.MpAuthorizedPaymentId = origen.MpAuthorizedPaymentId
               AND origen.MpAuthorizedPaymentId IS NOT NULL
            WHEN MATCHED THEN
                UPDATE SET MpPaymentId   = @PaymentId,
                           Monto         = @Monto,
                           Estado        = @Estado,
                           EstadoPago    = @EstadoPago,
                           DetalleEstado = @DetalleEstado,
                           FechaPago     = @FechaPago,
                           PayloadJson   = @Payload
            WHEN NOT MATCHED THEN
                INSERT (IdSuscripcion, MpAuthorizedPaymentId, MpPaymentId,
                        Monto, Moneda, Estado, EstadoPago, DetalleEstado,
                        FechaProgramada, FechaPago, FechaRegistro, PayloadJson)
                VALUES (@IdSuscripcion, @AuthorizedId, @PaymentId,
                        @Monto, @Moneda, @Estado, @EstadoPago, @DetalleEstado,
                        @FechaProgramada, @FechaPago, GETDATE(), @Payload);

            UPDATE dbo.SuscripcionCotizacion
            SET FechaUltimoPago = CASE WHEN @EstadoPago = 'approved'
                                       THEN ISNULL(@FechaPago, GETDATE())
                                       ELSE FechaUltimoPago END,
                FechaActualizacion = GETDATE()
            WHERE Id = @IdSuscripcion;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@IdSuscripcion", SqlDbType.Int).Value = idSuscripcion;
        cmd.Parameters.Add("@AuthorizedId", SqlDbType.VarChar, 64).Value = (object?)authorizedPaymentId ?? DBNull.Value;
        cmd.Parameters.Add("@PaymentId", SqlDbType.VarChar, 64).Value = (object?)paymentId ?? DBNull.Value;
        cmd.Parameters.Add("@Monto", SqlDbType.Decimal).Value = monto;
        cmd.Parameters.Add("@Moneda", SqlDbType.Char, 3).Value = moneda;
        cmd.Parameters.Add("@Estado", SqlDbType.VarChar, 30).Value = estado;
        cmd.Parameters.Add("@EstadoPago", SqlDbType.VarChar, 30).Value = (object?)estadoPago ?? DBNull.Value;
        cmd.Parameters.Add("@DetalleEstado", SqlDbType.VarChar, 100).Value = (object?)detalleEstado ?? DBNull.Value;
        cmd.Parameters.Add("@FechaProgramada", SqlDbType.DateTime).Value = (object?)fechaProgramada ?? DBNull.Value;
        cmd.Parameters.Add("@FechaPago", SqlDbType.DateTime).Value = (object?)fechaPago ?? DBNull.Value;
        cmd.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = payloadJson;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<PagoSuscripcionDto>> ListarPagosAsync(
        int idSuscripcion, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT Id, MpAuthorizedPaymentId, MpPaymentId, Monto, Moneda,
                   Estado, EstadoPago, DetalleEstado, FechaProgramada, FechaPago
            FROM dbo.SuscripcionPago
            WHERE IdSuscripcion = @Id
            ORDER BY ISNULL(FechaPago, FechaProgramada) DESC;";

        var lista = new List<PagoSuscripcionDto>();

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = idSuscripcion;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lista.Add(new PagoSuscripcionDto
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                MpAuthorizedPaymentId = Texto(reader, "MpAuthorizedPaymentId"),
                MpPaymentId = Texto(reader, "MpPaymentId"),
                Monto = reader.GetDecimal(reader.GetOrdinal("Monto")),
                Moneda = Texto(reader, "Moneda"),
                Estado = Texto(reader, "Estado"),
                EstadoPago = Texto(reader, "EstadoPago"),
                DetalleEstado = Texto(reader, "DetalleEstado"),
                FechaProgramada = Fecha(reader, "FechaProgramada"),
                FechaPago = Fecha(reader, "FechaPago")
            });
        }

        return lista;
    }

    private static SuscripcionEstadoDto Mapear(SqlDataReader r) => new()
    {
        IdSuscripcion = r.GetInt32(r.GetOrdinal("IdSuscripcion")),
        IdCotizacion = r.GetInt32(r.GetOrdinal("IdCotizacion")),
        ExternalReference = Texto(r, "ExternalReference"),
        PreapprovalId = Texto(r, "PreapprovalId"),
        NombreCliente = Texto(r, "NombreCliente"),
        PayerEmail = Texto(r, "PayerEmail"),
        MontoMensual = r.GetDecimal(r.GetOrdinal("MontoMensual")),
        Moneda = Texto(r, "Moneda"),
        DiasPrueba = Entero(r, "DiasPrueba"),
        FechaInicio = Fecha(r, "FechaInicio"),
        Estado = Texto(r, "Estado"),
        EstadoDescripcion = Texto(r, "EstadoDescripcion"),
        InitPoint = Texto(r, "InitPoint"),
        FechaCreacion = r.GetDateTime(r.GetOrdinal("FechaCreacion")),
        FechaAutorizacion = Fecha(r, "FechaAutorizacion"),
        FechaCancelacion = Fecha(r, "FechaCancelacion"),
        MotivoCancelacion = Texto(r, "MotivoCancelacion"),
        FechaProximoPago = Fecha(r, "FechaProximoPago"),
        FechaUltimoPago = Fecha(r, "FechaUltimoPago"),
        Origen = Texto(r, "Origen"),
        UsuarioCreacion = Texto(r, "UsuarioCreacion"),
        CuotasCobradas = r.GetInt32(r.GetOrdinal("CuotasCobradas")),
        CuotasRechazadas = r.GetInt32(r.GetOrdinal("CuotasRechazadas")),
        TotalCobrado = r.GetDecimal(r.GetOrdinal("TotalCobrado"))
    };

    private static string? Texto(SqlDataReader r, string columna)
    {
        var i = r.GetOrdinal(columna);
        return r.IsDBNull(i) ? null : r.GetString(i).Trim();
    }

    private static DateTime? Fecha(SqlDataReader r, string columna)
    {
        var i = r.GetOrdinal(columna);
        return r.IsDBNull(i) ? null : r.GetDateTime(i);
    }

    private static int? Entero(SqlDataReader r, string columna)
    {
        var i = r.GetOrdinal(columna);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }
}
