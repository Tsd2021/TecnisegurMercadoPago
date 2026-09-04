using System.Data;
using Microsoft.Data.SqlClient;
using TecnisegurMercadoPago.Api.Modelos.Contratos;

namespace TecnisegurMercadoPago.Api.Datos;

/// <summary>Fila reservada antes de crear la preferencia en MercadoPago.</summary>
public sealed record ReservaPago(int Id, string ExternalReference);

/// <summary>
/// Cobro único pendiente de confirmar su liberación, para el repaso periódico.
/// El equivalente de CuotaAReconsultar del lado de los pagos únicos.
/// </summary>
public sealed record PagoAReconsultar(int Id, int IdCotizacion, string PaymentId);

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
    /// El cobro pendiente de una cotización, si lo hay.
    ///
    /// Es el que ocupa el índice único filtrado y el que hay que resolver antes
    /// de poder generar otro link para la misma cotización. Puede haber uno
    /// solo por definición del índice, así que no hace falta desempatar.
    /// </summary>
    public async Task<PagoUnicoDto?> ObtenerPendientePorCotizacionAsync(
        int idCotizacion, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT * FROM dbo.vw_PagosUnicosEstado
            WHERE IdCotizacion = @IdCotizacion
              AND Estado = 'pendiente';";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@IdCotizacion", SqlDbType.Int).Value = idCotizacion;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Mapear(reader) : null;
    }

    /// <summary>
    /// Marca cancelado un cobro que sigue pendiente, para que deje de bloquear
    /// el índice y se pueda generar otro link para la misma cotización.
    ///
    /// El UPDATE exige que siga en 'pendiente'. Devuelve false si ya no lo
    /// estaba —se acreditó, se rechazó, o alguien lo canceló mientras tanto— y
    /// en ese caso no toca nada: cancelar un cobro ya acreditado sería borrar
    /// plata que entró.
    ///
    /// Queda en 'cancelled', con dos eles, que es el literal de MercadoPago y el
    /// que las vistas ya traducen como "Cancelado". Inventar un estado propio
    /// obligaría a tocar vw_PagosUnicosEstado, vw_CobranzasMercadoPago y la
    /// lista blanca del módulo de TSD para expresar lo mismo. El porqué de la
    /// cancelación va en EstadoDetalle, que para eso está.
    /// </summary>
    public async Task<bool> CancelarPendienteAsync(
        int idPago, string? motivo, CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE dbo.PagoUnico
               SET Estado             = 'cancelled',
                   EstadoDetalle      = @Motivo,
                   FechaActualizacion = GETDATE()
             WHERE Id     = @Id
               AND Estado = 'pendiente';";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = idPago;

        cmd.Parameters.Add("@Motivo", SqlDbType.VarChar, 100).Value =
            (object?)motivo ?? DBNull.Value;

        return await cmd.ExecuteNonQueryAsync(ct) == 1;
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
        DateTime? fechaLiberacion = null,
        decimal? montoNeto = null,
        decimal? comision = null,
        decimal? retenciones = null,
        string? estadoLiberacionMp = null,
        CancellationToken ct = default)
    {
        /* ISNULL en los campos de liberación: una notificación posterior que no
           los traiga no debe borrar los que ya estaban. Perder el dato es peor
           que no actualizarlo. */
        const string sql = @"
            UPDATE dbo.PagoUnico
            SET MpPaymentId        = @MpPaymentId,
                Estado             = @Estado,
                EstadoDetalle      = @EstadoDetalle,
                Monto              = ISNULL(@Monto, Monto),
                Moneda             = ISNULL(@Moneda, Moneda),
                /* ISNULL como sus vecinas, y por el mismo motivo: MercadoPago
                   sólo informa date_approved mientras el pago está aprobado. Si
                   después pasa a in_mediation o refunded lo manda vacío, y
                   escribirlo verbatim borra la fecha real del cobro. Verificado
                   con el pago 173370259108 el 12/08/2026: se acreditó 11:15:19 y
                   la notificación de mediación de 14 minutos después dejó la
                   columna en NULL. Perder el dato es peor que no actualizarlo. */
                FechaPago          = ISNULL(@FechaPago, FechaPago),
                PayloadJson        = @Payload,
                FechaLiberacion    = ISNULL(@FechaLiberacion, FechaLiberacion),
                MontoNeto          = ISNULL(@MontoNeto, MontoNeto),
                Comision           = ISNULL(@Comision, Comision),
                Retenciones        = ISNULL(@Retenciones, Retenciones),
                EstadoLiberacionMp = ISNULL(@EstadoLiberacionMp, EstadoLiberacionMp),
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
        cmd.Parameters.Add("@FechaLiberacion", SqlDbType.DateTime).Value = (object?)fechaLiberacion ?? DBNull.Value;
        cmd.Parameters.Add("@MontoNeto", SqlDbType.Decimal).Value = (object?)montoNeto ?? DBNull.Value;
        cmd.Parameters.Add("@Comision", SqlDbType.Decimal).Value = (object?)comision ?? DBNull.Value;
        cmd.Parameters.Add("@Retenciones", SqlDbType.Decimal).Value = (object?)retenciones ?? DBNull.Value;
        cmd.Parameters.Add("@EstadoLiberacionMp", SqlDbType.VarChar, 20).Value = (object?)estadoLiberacionMp ?? DBNull.Value;

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Cobros únicos aprobados cuya liberación todavía no confirmó MercadoPago.
    ///
    /// Es el gemelo de SuscripcionRepositorio.ListarCuotasSinLiberacionConfirmadaAsync,
    /// y existe por la misma razón: no hay webhook de liberación, así que el
    /// único modo de enterarse de una reversión es volver a preguntar.
    ///
    /// Faltaba. El repaso diario cubría las cuotas mensuales pero no los pagos
    /// únicos, que son los de importe más alto —el equipamiento— y por lo tanto
    /// donde un contracargo silencioso duele más.
    /// </summary>
    public async Task<List<PagoAReconsultar>> ListarPagosSinLiberacionConfirmadaAsync(
        int diasDeGracia = 10, int tope = 200, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TOP (@Tope) p.Id, p.IdCotizacion, p.MpPaymentId
            FROM dbo.PagoUnico AS p
            WHERE p.Estado = 'approved'
              AND p.MpPaymentId IS NOT NULL
              AND (
                    p.FechaLiberacion IS NULL
                 OR p.MontoNeto IS NULL
                 OR p.Comision IS NULL
                 OR p.EstadoLiberacionMp IS NULL
                 OR p.EstadoLiberacionMp <> 'released'
                 OR p.FechaLiberacion > DATEADD(day, -@DiasGracia, GETDATE())
              )
            ORDER BY ISNULL(p.FechaActualizacion, p.FechaCreacion);";

        var lista = new List<PagoAReconsultar>();

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Tope", SqlDbType.Int).Value = tope;
        cmd.Parameters.Add("@DiasGracia", SqlDbType.Int).Value = diasDeGracia;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lista.Add(new PagoAReconsultar(
                reader.GetInt32(reader.GetOrdinal("Id")),
                reader.GetInt32(reader.GetOrdinal("IdCotizacion")),
                reader.GetString(reader.GetOrdinal("MpPaymentId"))));
        }

        return lista;
    }

    /// <summary>
    /// Actualiza sólo lo que cambia al reconsultar un cobro ya registrado. No
    /// toca el importe bruto ni la fecha de pago: eso ya ocurrió.
    /// </summary>
    public async Task ActualizarLiberacionAsync(
        int idPago,
        string? estado,
        string? estadoDetalle,
        DateTime? fechaLiberacion,
        decimal? montoNeto,
        decimal? comision,
        decimal? retenciones,
        string? estadoLiberacionMp = null,
        CancellationToken ct = default)
    {
        const string sql = @"
            UPDATE dbo.PagoUnico
            SET Estado             = ISNULL(@Estado, Estado),
                EstadoDetalle      = ISNULL(@EstadoDetalle, EstadoDetalle),
                FechaLiberacion    = ISNULL(@FechaLiberacion, FechaLiberacion),
                MontoNeto          = ISNULL(@MontoNeto, MontoNeto),
                Comision           = ISNULL(@Comision, Comision),
                Retenciones        = ISNULL(@Retenciones, Retenciones),
                EstadoLiberacionMp = ISNULL(@EstadoLiberacionMp, EstadoLiberacionMp),
                FechaActualizacion = GETDATE()
            WHERE Id = @Id;";

        await using var cn = Conexion();
        await cn.OpenAsync(ct);

        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.Add("@Id", SqlDbType.Int).Value = idPago;
        cmd.Parameters.Add("@Estado", SqlDbType.VarChar, 30).Value = (object?)estado ?? DBNull.Value;
        cmd.Parameters.Add("@EstadoDetalle", SqlDbType.VarChar, 100).Value = (object?)estadoDetalle ?? DBNull.Value;
        cmd.Parameters.Add("@FechaLiberacion", SqlDbType.DateTime).Value = (object?)fechaLiberacion ?? DBNull.Value;
        cmd.Parameters.Add("@MontoNeto", SqlDbType.Decimal).Value = (object?)montoNeto ?? DBNull.Value;
        cmd.Parameters.Add("@Comision", SqlDbType.Decimal).Value = (object?)comision ?? DBNull.Value;
        cmd.Parameters.Add("@Retenciones", SqlDbType.Decimal).Value = (object?)retenciones ?? DBNull.Value;
        cmd.Parameters.Add("@EstadoLiberacionMp", SqlDbType.VarChar, 20).Value = (object?)estadoLiberacionMp ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct);
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

        /* Null y no cero: cero es "no cobró comisión", null es "no se sabe". */
        decimal? Importe(string columna)
        {
            var i = r.GetOrdinal(columna);
            return r.IsDBNull(i) ? null : r.GetDecimal(i);
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
            MontoNeto         = Importe("MontoNeto"),
            Comision          = Importe("Comision"),
            Retenciones       = Importe("Retenciones"),
            Moneda            = Texto("Moneda"),
            Estado            = Texto("Estado"),
            EstadoDescripcion = Texto("EstadoDescripcion"),
            EstadoDetalle     = Texto("EstadoDetalle"),
            FechaCreacion     = r.GetDateTime(r.GetOrdinal("FechaCreacion")),
            FechaPago         = Fecha("FechaPago"),
            FechaLiberacion   = Fecha("FechaLiberacion"),
            EstadoLiberacionMp = Texto("EstadoLiberacionMp"),
            Origen            = Texto("Origen"),
            UsuarioCreacion   = Texto("UsuarioCreacion")
        };
    }
}
