# MercadoPago MCP Server

Servidor MCP oficial de MercadoPago. Expone las APIs como herramientas que
Claude Code puede invocar directamente, sin salir del editor.

**URL:** `https://mcp.mercadopago.com/mcp` (remoto, transporte HTTP)

---

## Para qué nos sirve concretamente

No es un juguete: hay tres cosas que hoy nos están frenando y que esto destraba.

| Herramienta | Qué destraba |
|---|---|
| **Usuarios de prueba con fondos** | Todo lo construido está sin ejercitar porque no hay cobros reales y la cuenta de prueba está vacía. Con esto se corre una suscripción completa —adhesión, cuota, webhook, liberación— sin plata real. |
| **Gestión de credenciales** (sólo OAuth) | Rotar el access token de producción, que quedó expuesto el 31/07/2026. |
| **Configuración y monitoreo de webhooks** | Verificar la configuración del webhook y ver los intentos de entrega sin entrar al panel. |
| Búsqueda de documentación | Consultar la doc oficial sin cambiar de ventana. |
| Medición de calidad de integración | El puntaje oficial de MercadoPago antes de salir a producción. |

---

## Configuración

El archivo `.mcp.json` de la raíz del repositorio ya está listo:

```json
{
  "mcpServers": {
    "mercadopago": {
      "type": "http",
      "url": "https://mcp.mercadopago.com/mcp",
      "headers": {
        "Authorization": "Bearer ${MERCADOPAGO_MCP_TOKEN}"
      }
    }
  }
}
```

**El token va en una variable de entorno, no en el archivo.** `.mcp.json` se
versiona y se comparte con el equipo; escribir la credencial ahí la mandaría al
historial de git, que es exactamente el problema que ya tuvimos una vez.

### 1. Cargar el token

En PowerShell, sólo para la sesión actual:

```powershell
$env:MERCADOPAGO_MCP_TOKEN = "TEST-..."
```

Para que persista entre reinicios:

```powershell
[Environment]::SetEnvironmentVariable("MERCADOPAGO_MCP_TOKEN", "TEST-...", "User")
```

> Después de setearla como `User` hay que **cerrar y reabrir la terminal** para
> que la tome.

### 2. Reiniciar Claude Code

Al abrirlo en este repositorio va a pedir aprobación para el servidor MCP del
proyecto. Verificá con `/mcp` que aparezca conectado.

### Alternativa sin archivo

Si preferís no versionar nada:

```powershell
claude mcp add --transport http mercadopago https://mcp.mercadopago.com/mcp --header "Authorization: Bearer TEST-..."
```

Queda en la configuración local del usuario, no en el repositorio. Contra: el
token queda en el historial de PowerShell.

---

## Qué token usar

**Empezar con las credenciales de PRUEBA** (`TEST-...`), del panel:
*Tus integraciones → la aplicación → Credenciales de prueba*.

Tres razones:

1. Lo que más nos sirve —crear usuarios de prueba y darles fondos— no necesita
   producción.
2. Evita una copia más del token de producción dando vueltas.
3. Un error con herramientas que escriben (configurar webhooks, por ejemplo) no
   toca la cuenta real.

Las herramientas de **gestión de aplicaciones y credenciales funcionan sólo por
OAuth**, no con token en el header. Para esas, al conectar te redirige al sitio
de MercadoPago a autorizar, y ahí elegís el país.

---

## Advertencia

Este servidor puede **modificar la cuenta**: crear aplicaciones, configurar
webhooks, generar usuarios de prueba. Con el token de producción, además, opera
sobre la cuenta real de Tecnisegur.

Antes de aceptar cualquier herramienta que escriba, leé qué va a hacer. Vale
sobre todo para la configuración de webhooks: **si se pisa la URL o la clave
secreta del webhook productivo, las notificaciones dejan de procesarse en
silencio** —siguen llegando y guardándose con `FirmaValida = 0`, la API responde
200 y MercadoPago queda conforme—. Ya nos pasó una vez, en julio, y costó
encontrarlo.

---

## Referencias

- [MCP Server — MercadoPago Developers (Uruguay)](https://www.mercadopago.com.uy/developers/en/docs/checkout-api/additional-content/mcp-server)
- [Repositorio oficial](https://github.com/mercadolibre/mercadopago-mcp-server)
