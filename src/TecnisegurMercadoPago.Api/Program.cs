using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Reflection;
using TecnisegurMercadoPago.Api.Configuracion;
using TecnisegurMercadoPago.Api.Datos;
using TecnisegurMercadoPago.Api.Seguridad;
using TecnisegurMercadoPago.Api.Servicios;
using System.Reflection;
var builder = WebApplication.CreateBuilder(args);

/* ---------------------------------------------------------------------------
 * Log a archivo
 *
 * Va primero, antes que cualquier otro registro: si algo falla en el arranque
 * —por ejemplo el ValidateOnStart de más abajo— queremos que ese fallo quede
 * escrito. Un error de arranque sin log es exactamente el caso que dejó a esta
 * API muda durante semanas.
 *
 * No reemplaza a la consola: en desarrollo se sigue viendo todo por pantalla.
 * Lo que agrega es persistencia, que bajo IIS con hostingModel inprocess la
 * captura de stdout no daba. Ver ArchivoLogger.cs.
 * --------------------------------------------------------------------------- */
builder.Logging.AddArchivo(
    builder.Configuration.GetSection("Logging:Archivo"),
    builder.Environment.ContentRootPath);

/* ---------------------------------------------------------------------------
 * Configuración
 * --------------------------------------------------------------------------- */
builder.Services
    .AddOptions<MercadoPagoOpciones>()
    .Bind(builder.Configuration.GetSection(MercadoPagoOpciones.Seccion))
    .Validate(o => !string.IsNullOrWhiteSpace(o.AccessToken),
        "Falta MercadoPago:AccessToken. Configurarlo por user-secrets o variable de entorno.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.WebhookSecret),
        "Falta MercadoPago:WebhookSecret. Sin él no se puede validar la firma de los webhooks.")
    .ValidateOnStart();

/* Twilio es OPCIONAL a propósito: sin él la aplicación arranca igual y sólo
 * se pierde el envío por WhatsApp. El cobro no depende de esto, así que no
 * lleva ValidateOnStart. */
builder.Services
    .AddOptions<TwilioOpciones>()
    .Bind(builder.Configuration.GetSection(TwilioOpciones.Seccion));

builder.Services
    .AddOptions<ApiOpciones>()
    .Bind(builder.Configuration.GetSection(ApiOpciones.Seccion))
    .Validate(o => o.Claves.Any(c => !string.IsNullOrWhiteSpace(c.Value)),
        "No hay ninguna clave configurada en Api:Claves.")
    .ValidateOnStart();

/* ---------------------------------------------------------------------------
 * Cliente HTTP tipado contra MercadoPago.
 * AddHttpClient reutiliza el handler: no se instancia un HttpClient por llamada
 * (ese patrón agota sockets bajo carga).
 * --------------------------------------------------------------------------- */
builder.Services.AddHttpClient<MercadoPagoCliente>((sp, http) =>
{
    var opciones = sp.GetRequiredService<IOptions<MercadoPagoOpciones>>().Value;

    http.BaseAddress = new Uri(opciones.UrlBase);
    http.Timeout = TimeSpan.FromSeconds(opciones.TimeoutSegundos);
    http.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));
});

/* ---------------------------------------------------------------------------
 * Servicios propios
 * --------------------------------------------------------------------------- */
builder.Services.AddHttpClient<TwilioCliente>((sp, http) =>
{
    var opciones = sp.GetRequiredService<IOptions<TwilioOpciones>>().Value;

    http.BaseAddress = new Uri(opciones.UrlBase);
    http.Timeout = TimeSpan.FromSeconds(opciones.TimeoutSegundos);
});

builder.Services.AddScoped<SuscripcionRepositorio>();
builder.Services.AddScoped<NotificacionRepositorio>();
builder.Services.AddScoped<PagoRepositorio>();
builder.Services.AddScoped<SuscripcionServicio>();
builder.Services.AddScoped<PagoServicio>();
builder.Services.AddScoped<EnvioWhatsAppServicio>();
builder.Services.AddSingleton<ValidadorFirmaWebhook>();

/* Singleton: el estado de facturación es de la cuenta, no de la request. */
builder.Services.AddSingleton<CacheEstadoCuenta>();

builder.Services.AddHostedService<ProcesadorNotificaciones>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

/* ---------------------------------------------------------------------------
 * Pipeline
 * --------------------------------------------------------------------------- */
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    // En producción el webhook debe entrar por HTTPS; MercadoPago lo exige.
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Autenticación de los sistemas internos. Excluye /api/webhook y /health.
app.UseMiddleware<ApiKeyMiddleware>();

try
{
    app.MapControllers();
}
catch (ReflectionTypeLoadException ex)
{
    var detalles = string.Join(
        Environment.NewLine + Environment.NewLine,
        ex.LoaderExceptions
            .Where(e => e != null)
            .Select(e => e!.ToString())
    );

    app.Logger.LogCritical(
        ex,
        "ERROR CARGANDO TIPOS:{Salto}{Detalles}",
        Environment.NewLine,
        detalles);

    throw new Exception(
        "ERROR REAL AL CARGAR ASSEMBLIES:" +
        Environment.NewLine +
        detalles,
        ex);
}
app.MapHealthChecks("/health");

app.Run();
