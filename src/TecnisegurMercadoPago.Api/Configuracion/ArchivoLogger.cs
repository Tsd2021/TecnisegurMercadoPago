using System.Text;

namespace TecnisegurMercadoPago.Api.Configuracion;

/// <summary>
/// Opciones del log a archivo. Se leen de la sección <c>Logging:Archivo</c>.
/// </summary>
public sealed class ArchivoLoggerOpciones
{
    /// <summary>
    /// Carpeta donde se escriben los archivos. Relativa al directorio de la
    /// aplicación, que en el servidor es la carpeta del sitio en IIS.
    /// </summary>
    public string Carpeta { get; set; } = "logs";

    /// <summary>Prefijo del archivo: queda <c>mpapi-20260903.log</c>.</summary>
    public string Prefijo { get; set; } = "mpapi";

    /// <summary>
    /// Días que se conservan los archivos viejos. La purga corre una vez por
    /// día, cuando rota el archivo. Cero o menos desactiva la purga.
    /// </summary>
    public int DiasRetencion { get; set; } = 30;

    public LogLevel NivelMinimo { get; set; } = LogLevel.Information;
}

/// <summary>
/// Log a archivo, propio y sin dependencias externas.
///
/// POR QUÉ EXISTE
/// El módulo de ASP.NET Core puede capturar la salida estándar
/// (<c>stdoutLogEnabled</c>), pero con <c>hostingModel="inprocess"</c> eso es
/// poco confiable, y Microsoft lo documenta como diagnóstico de arranque y no
/// como logging de producción. El 03/09/2026 se verificó en el servidor: con la
/// carpeta creada y permisos otorgados a la identidad del pool, no llegó a
/// escribir ni un archivo.
///
/// Y acá el log no es opcional. De él dependen dos cosas concretas:
///   · la simulación del repaso de descuentos, que sólo existe en el log y es
///     lo que hay que leer antes de subirle la cuota a un cliente;
///   · la traza AUDITORIA-PREAPPROVAL, que es la evidencia de que las
///     cancelaciones vienen de MercadoPago y no de nuestro código.
///
/// REGLA DE ORO: escribir un log NUNCA puede tirar la aplicación. Todas las
/// fallas de escritura se tragan en silencio. Un disco lleno degrada la
/// observabilidad; no puede además cortar cobros.
/// </summary>
public sealed class ArchivoLoggerProvider : ILoggerProvider
{
    private readonly ArchivoLoggerOpciones _opciones;
    private readonly EscritorArchivo _escritor;

    public ArchivoLoggerProvider(ArchivoLoggerOpciones opciones, string raiz)
    {
        _opciones = opciones;

        /* NO usar AppContext.BaseDirectory acá. Con hostingModel="inprocess" la
           aplicación corre dentro de w3wp.exe y esa propiedad resuelve a
           C:\Windows\System32\inetsrv, no a la carpeta del sitio. El
           03/09/2026 costó tres despliegues entender por qué no aparecía ningún
           archivo: se intentaba crear la carpeta ahí, fallaba por permisos, y
           el catch de EscritorArchivo se comía el error.

           `raiz` es el ContentRootPath que provee el host, que bajo IIS sí
           apunta a la carpeta del sitio.

           Una ruta absoluta en configuración evita el problema por completo, y
           además saca los logs de la carpeta del sitio, donde cada despliegue
           puede borrarlos. Es la opción recomendada en producción. */
        var carpeta = Path.IsPathRooted(opciones.Carpeta)
            ? opciones.Carpeta
            : Path.Combine(raiz, opciones.Carpeta);

        _escritor = new EscritorArchivo(
            carpeta, opciones.Prefijo, opciones.DiasRetencion);
    }

    public ILogger CreateLogger(string categoryName)
        => new ArchivoLogger(categoryName, _escritor, _opciones);

    public void Dispose() => _escritor.Dispose();
}

/// <summary>
/// Serializa la escritura de todos los loggers a un único archivo por día.
///
/// Abre y cierra en cada línea en vez de mantener un StreamWriter vivo. Es más
/// lento, pero deja cada línea en disco al instante: si el proceso muere —o IIS
/// recicla el pool en el medio— no se pierde lo último, que suele ser
/// justamente lo que explica la muerte. El volumen de esta API —un webhook cada
/// tanto y dos repasos por día— no justifica optimizarlo.
/// </summary>
internal sealed class EscritorArchivo : IDisposable
{
    private readonly object _candado = new();
    private readonly string _carpeta;
    private readonly string _prefijo;
    private readonly int _diasRetencion;

    private DateOnly _diaDelArchivo;

    public EscritorArchivo(string carpeta, string prefijo, int diasRetencion)
    {
        _carpeta = carpeta;
        _prefijo = prefijo;
        _diasRetencion = diasRetencion;
    }

    public void Escribir(string linea)
    {
        lock (_candado)
        {
            try
            {
                var hoy = DateOnly.FromDateTime(DateTime.Now);

                /* La rotación es implícita: el nombre lleva la fecha, así que al
                   cambiar el día se escribe en otro archivo solo. Este bloque
                   existe para crear la carpeta y purgar una vez por día, no en
                   cada línea. */
                if (hoy != _diaDelArchivo)
                {
                    Directory.CreateDirectory(_carpeta);
                    _diaDelArchivo = hoy;
                    Purgar();
                }

                File.AppendAllText(Ruta(hoy), linea + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                /* Ver la regla de oro en ArchivoLoggerProvider: el logging no
                   puede tirar la aplicación. Sin un canal alternativo adonde
                   avisar, no queda más que seguir. */
            }
        }
    }

    private string Ruta(DateOnly dia)
        => Path.Combine(_carpeta, _prefijo + "-" + dia.ToString("yyyyMMdd") + ".log");

    private void Purgar()
    {
        if (_diasRetencion <= 0) return;

        var limite = DateTime.Now.AddDays(-_diasRetencion);

        foreach (var archivo in Directory.EnumerateFiles(_carpeta, _prefijo + "-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(archivo) < limite) File.Delete(archivo);
            }
            catch
            {
                /* Uno que no se pueda borrar —abierto por otro proceso— no debe
                   impedir que se borren los demás. */
            }
        }
    }

    public void Dispose() { }
}

internal sealed class ArchivoLogger : ILogger
{
    private readonly string _categoria;
    private readonly EscritorArchivo _escritor;
    private readonly ArchivoLoggerOpciones _opciones;

    public ArchivoLogger(
        string categoria, EscritorArchivo escritor, ArchivoLoggerOpciones opciones)
    {
        _categoria = categoria;
        _escritor = escritor;
        _opciones = opciones;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel nivel)
        => nivel != LogLevel.None && nivel >= _opciones.NivelMinimo;

    public void Log<TState>(
        LogLevel nivel,
        EventId eventId,
        TState state,
        Exception? excepcion,
        Func<TState, Exception?, string> formateador)
    {
        if (!IsEnabled(nivel)) return;

        var mensaje = formateador(state, excepcion);

        if (string.IsNullOrEmpty(mensaje) && excepcion is null) return;

        var texto = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(Abreviatura(nivel)).Append("] ")
            .Append(_categoria)
            .Append(": ")
            .Append(mensaje);

        /* La excepción va completa, con stack: es el motivo por el que alguien
           abre este archivo. */
        if (excepcion is not null)
        {
            texto.AppendLine().Append(excepcion);
        }

        _escritor.Escribir(texto.ToString());
    }

    private static string Abreviatura(LogLevel nivel) => nivel switch
    {
        LogLevel.Trace       => "TRC",
        LogLevel.Debug       => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning     => "WRN",
        LogLevel.Error       => "ERR",
        LogLevel.Critical    => "CRT",
        _                    => "???"
    };
}

public static class ArchivoLoggerExtensiones
{
    /// <summary>
    /// Registra el log a archivo leyendo <c>Logging:Archivo</c>. Si la sección
    /// no existe se usan los valores por defecto: carpeta <c>logs</c> junto a la
    /// aplicación, 30 días de retención, desde Information.
    ///
    /// <paramref name="raiz"/> tiene que ser el ContentRootPath del host
    /// (<c>builder.Environment.ContentRootPath</c>), NO AppContext.BaseDirectory:
    /// ver el comentario del constructor de <see cref="ArchivoLoggerProvider"/>.
    /// </summary>
    public static ILoggingBuilder AddArchivo(
        this ILoggingBuilder builder, IConfiguration seccion, string raiz)
    {
        var opciones = new ArchivoLoggerOpciones();
        seccion.Bind(opciones);

        builder.AddProvider(new ArchivoLoggerProvider(opciones, raiz));
        return builder;
    }
}
