namespace TecnisegurMercadoPago.Api.Configuracion;

/// <summary>
/// Configuración propia de la API (autenticación de los clientes internos:
/// EmpleadoWeb y TSD Desktop).
/// </summary>
public sealed class ApiOpciones
{
    public const string Seccion = "Api";

    /// <summary>
    /// Claves aceptadas en el header X-Api-Key. Una por sistema consumidor,
    /// para poder revocar el acceso de uno sin afectar al otro.
    /// </summary>
    public Dictionary<string, string> Claves { get; set; } = new();
}
