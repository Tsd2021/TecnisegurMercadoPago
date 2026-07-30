# Publicación en IIS

Guía para dejar la API accesible desde internet, que es lo que MercadoPago
necesita para poder enviar los webhooks.

> Para **probar** no hace falta publicar: alcanza con un túnel (`ngrok http 5199`).
> Ver README §3.2. Esta guía es para dejarlo funcionando de forma permanente.

---

## 0. Requisito previo — ASP.NET Core Hosting Bundle

Esta API es **ASP.NET Core / .NET 10**, distinta a `TecnisegurApi` y `EmpleadoWeb`
que son .NET Framework. IIS no sabe hostear .NET moderno por sí solo: necesita un
módulo adicional.

En el servidor, instalar el **ASP.NET Core 10 Hosting Bundle**:

https://dotnet.microsoft.com/download/dotnet/10.0 → *ASP.NET Core Runtime* →
**Hosting Bundle** (el instalador de Windows).

Instala tres cosas: el runtime de .NET, el runtime de ASP.NET Core y el módulo de
IIS (`AspNetCoreModuleV2`). Después:

```powershell
net stop was /y
net start w3svc
iisreset
```

Verificar que quedó instalado:

```powershell
dotnet --list-runtimes | Select-String "AspNetCore"
# debe listar Microsoft.AspNetCore.App 10.x
```

> **Esto no afecta a las aplicaciones existentes.** El Hosting Bundle convive sin
> problemas con los sitios .NET Framework que ya corren en el mismo IIS.

---

## 1. Generar el paquete

Desde la máquina de desarrollo:

```bash
cd C:\Users\<usuario>\source\repos\Tsd2021\TecnisegurMercadoPago
dotnet publish src/TecnisegurMercadoPago.Api/TecnisegurMercadoPago.Api.csproj -c Release -o publish
```

Queda todo en `publish\`, incluido un `web.config` generado automáticamente que
le dice a IIS cómo arrancar la aplicación.

---

## 2. Crear el sitio en IIS

### 2.1 Application Pool

Nuevo pool, por ejemplo `MercadoPagoApi`, con:

| Opción | Valor |
|---|---|
| .NET CLR Version | **No Managed Code** |
| Managed Pipeline Mode | Integrated |
| Identity | `ApplicationPoolIdentity` (o una cuenta de dominio con acceso a TSD) |

> **"No Managed Code" no es un error.** La aplicación no corre sobre el CLR de
> IIS: el `AspNetCoreModuleV2` lanza el proceso .NET aparte. Si le ponés una
> versión de CLR, no arranca.

### 2.2 Sitio

- **Ruta física:** `C:\inetpub\wwwroot\TecnisegurMP Api` (copiar ahí el contenido de `publish\`)
- **Binding:** `https` puerto 443, host `mpapi.tecnisegur.com.uy`
- **Certificado SSL:** el wildcard `*.tecnisegur.com.uy` de Abitab, vigente hasta
  el 10/01/2027. Es obligatorio: MercadoPago **no envía webhooks a HTTP**.
- **Require Server Name Indication (SNI):** ✅ **marcado**.

> **El SNI no es opcional.** En esa IP el 443 ya lo ocupa el sitio de EmpleadoWeb.
> Sin SNI, IIS no puede tener dos sitios HTTPS sobre el mismo IP:puerto: o falla
> al guardar el binding, o rompe el sitio que ya andaba. Con SNI conviven, cada
> uno respondiendo por su host name.

### 2.3 DNS

El subdominio **tiene que ser de `tecnisegur.com.uy`**, no un dominio nuevo. Dos
razones: la zona ya existe (crear el registro es gratis e inmediato), y el
certificado wildcard sólo cubre `*.tecnisegur.com.uy`. Un dominio aparte como
`api-tecnisegurmp.com.uy` habría que registrarlo en NIC.uy *y* comprarle un
certificado propio.

Registro a crear en el panel de Antel (la zona está en `ns1.anteldata.com.uy`;
el contacto administrativo es `pablo@tecnisegur.com.uy`):

```
Tipo:   A
Nombre: mpapi
Valor:  191.239.244.138      # misma IP pública que www.tecnisegur.com.uy
TTL:    600
```

> **Cuidado con qué servidor es cuál.** La organización tiene dos:
>
> | IP | Qué corre ahí |
> |---|---|
> | `191.239.244.138` | `www.tecnisegur.com.uy` **y esta API** (VM en Azure) |
> | `179.27.99.230` | `empleado.tecnisegur.com.uy` (servidor on-premise) |
>
> Los dos presentan el mismo certificado wildcard, así que mirar el certificado
> **no** sirve para distinguirlos. Para saber a cuál le pega un nombre:
>
> ```powershell
> curl.exe -s -w "`nHTTP=%{http_code}`n" `
>   --resolve "mpapi.tecnisegur.com.uy:443:191.239.244.138" `
>   https://mpapi.tecnisegur.com.uy/health
> # el servidor correcto responde Healthy / 200; el otro, 404
> ```

### 2.4 DNS interno

Los equipos de la red interna resuelven contra `172.16.10.20` / `172.16.10.22`,
que **no son autoritativos** de la zona (devuelven el SOA de Antel): sólo
reenvían y cachean.

Si se consultó el nombre **antes** de que existiera el registro, esos servidores
se quedan con un NXDOMAIN cacheado y siguen diciendo que no existe aunque afuera
ya resuelva. El síntoma es exacto: funciona desde internet y desde el servidor,
pero no desde la red interna.

Se destraba vaciando la caché en el servidor DNS (no alcanza con
`Clear-DnsClientCache` en el cliente):

```powershell
# En 172.16.10.20, como administrador
Clear-DnsServerCache -Force
```

Importa porque **EmpleadoWeb resuelve por DNS interno**: si el nombre no resuelve
ahí, las llamadas a la API fallan aunque desde afuera todo ande.

> El wildcard cubre **un solo nivel**. `mpapi.tecnisegur.com.uy` ✅, pero
> `api.mp.tecnisegur.com.uy` ❌.

> El *host name* del binding de IIS **no crea nada en internet**: es sólo un
> filtro de enrutamiento interno. Si el nombre no resuelve en DNS, el navegador
> nunca llega al servidor y el error es "no se puede acceder a este sitio web".

---

## 3. Configurar los secretos

Los secretos **no van en `appsettings.json`** (ese archivo se versiona). En el
servidor se cargan como variables de entorno del proceso.

La forma más práctica en IIS es agregarlas al `web.config` **del servidor**
(el que quedó en `C:\inetpub\wwwroot\TecnisegurMP Api\`, que nunca se sube a git):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*"
             modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet"
                  arguments=".\TecnisegurMercadoPago.Api.dll"
                  stdoutLogEnabled="true"
                  stdoutLogFile=".\logs\stdout"
                  hostingModel="inprocess">
        <environmentVariables>
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
          <environmentVariable name="ConnectionStrings__TSD"
                               value="Server=172.16.10.22;Database=TSD;User ID=...;Password=...;TrustServerCertificate=True" />
          <environmentVariable name="MercadoPago__AccessToken"   value="APP_USR-..." />
          <environmentVariable name="MercadoPago__WebhookSecret" value="..." />
          <environmentVariable name="MercadoPago__BackUrl"
                               value="https://empleado.tecnisegur.com.uy/CotizacionAlarma/RetornoSuscripcion" />
          <environmentVariable name="Api__Claves__WEBEMPLEADO"   value="..." />
          <environmentVariable name="Api__Claves__TSD"           value="..." />
        </environmentVariables>
      </aspNetCore>
    </system.webServer>
  </location>
</configuration>
```

Notas:

- El **doble guion bajo** (`__`) separa niveles de configuración. `MercadoPago__AccessToken`
  equivale a la sección `MercadoPago` → clave `AccessToken`.
- Este archivo tiene credenciales en texto plano: restringir sus permisos NTFS a
  administradores y a la identidad del pool. No copiarlo a ningún otro lado.
- Alternativa más segura si la política lo exige: variables de entorno a nivel de
  sistema, o el store de secretos de Windows. Para el nivel de exposición de este
  servicio, el `web.config` restringido es razonable y es lo estándar en IIS.

### Generar las claves de API

```powershell
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
foreach ($n in @("WEBEMPLEADO","TSD")) {
  $b = New-Object byte[] 32
  $rng.GetBytes($b)
  Write-Output ("{0,-12}: {1}" -f $n, ([BitConverter]::ToString($b) -replace '-',''))
}
$rng.Dispose()
```

Una distinta para `WEBEMPLEADO` y para `TSD`, así se puede revocar el acceso de
un sistema sin afectar al otro.

> No usar `Get-Random`: no es criptográficamente seguro. Y `[Convert]::ToHexString`
> no existe en Windows PowerShell 5.1 (es .NET Core en adelante), que es la
> consola del servidor — de ahí `[BitConverter]::ToString`.

---

## 4. Verificar

### 4.1 Sin esperar al DNS

El registro A puede demorar (depende de un tercero). Para validar IIS,
certificado y arranque de la aplicación desde ya, agregar temporalmente en el
servidor a `C:\Windows\System32\drivers\etc\hosts` (como administrador):

```
127.0.0.1    mpapi.tecnisegur.com.uy
```

Así se separan los dos problemas: si con esto anda, lo único que falta es DNS.
**Borrar esa línea** una vez publicado el registro A, si no el servidor se
resuelve a sí mismo y enmascara un DNS mal configurado.

### 4.2 Pruebas

```powershell
# Desde el propio servidor
curl.exe https://mpapi.tecnisegur.com.uy/health
# → Healthy

# El webhook debe responder anónimo
curl.exe https://mpapi.tecnisegur.com.uy/api/webhook
# → {"estado":"activo"}

# Sin clave debe rechazar
curl.exe https://mpapi.tecnisegur.com.uy/api/suscripciones/1
# → 401 {"ok":false,"mensaje":"Falta el header X-Api-Key."}
```

> Usar `curl.exe`, con la extensión. En PowerShell 5.1 `curl` a secas es un alias
> de `Invoke-WebRequest`, que tiene otra sintaxis y otra salida.

**Probar también desde afuera de la red** (por ejemplo desde el celular con datos
móviles). Si `/health` responde desde adentro pero no desde afuera, el problema
es DNS o firewall, no la aplicación.

---

## 5. Si algo falla

**HTTP 500.30 / 500.31 al entrar:** la aplicación no arrancó. Poner
`stdoutLogEnabled="true"`, crear la carpeta `logs\` con permiso de escritura para
la identidad del pool, reiniciar el sitio y leer `logs\stdout_*.log`:

```powershell
$sitio = "C:\inetpub\wwwroot\TecnisegurMP Api"
$pool  = "MercadoPagoApi"

New-Item -ItemType Directory -Force "$sitio\logs" | Out-Null
icacls "$sitio\logs" /grant "IIS AppPool\$pool:(OI)(CI)M"

# El web.config tiene credenciales: sacarle la herencia, dejar solo admins + pool
icacls "$sitio\web.config" /inheritance:r `
  /grant "BUILTIN\Administradores:(F)" `
  /grant "IIS AppPool\$pool:(R)"

Restart-WebAppPool -Name $pool
```

Las causas más comunes, en orden:

1. Falta el Hosting Bundle, o se instaló antes que IIS (reinstalarlo).
2. El pool no está en **No Managed Code**.
3. Falta `MercadoPago__AccessToken` o `MercadoPago__WebhookSecret` — la aplicación
   **se niega a arrancar** a propósito si no están (`ValidateOnStart` en `Program.cs`).
   El log lo dice explícitamente.

**Arranca pero las consultas fallan:** la identidad del pool no llega a TSD.
Revisar la cadena de conexión y, si se usa autenticación integrada, cambiar la
identidad del pool por una cuenta de dominio con permisos sobre TSD.

**MercadoPago no entrega los webhooks:** verificar que la URL sea HTTPS con
certificado válido (no autofirmado) y que responda desde fuera de la red.

---

## 6. Actualizaciones posteriores

```powershell
# En el servidor, antes de copiar
Stop-WebAppPool -Name "MercadoPagoApi"
# ... copiar el nuevo contenido de publish\ ...
# OJO: no pisar el web.config del servidor, que tiene los secretos
Start-WebAppPool -Name "MercadoPagoApi"
```

> Para evitar pisar el `web.config` accidentalmente, conviene mantener una copia
> del archivo del servidor fuera de la carpeta del sitio.
