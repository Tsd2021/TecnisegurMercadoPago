<#
.SINOPSIS
    Genera y descarga el reporte de Liberaciones de MercadoPago.

.DESCRIPCION
    Exploratorio. Sirve para responder tres preguntas antes de decidir si vale la
    pena construir el conciliador automático:

      1. ¿El access token tiene permiso sobre la API de reportes?
      2. ¿Qué columnas devuelve REALMENTE esta cuenta? (la documentación lista
         las posibles, no las que cada cuenta entrega por defecto)
      3. ¿Qué valores toma RECORD_TYPE en la operación real?

    NO configura nada ni programa la generación automática: sólo pide un reporte
    manual del rango indicado y lo baja. No modifica la cuenta.

    La generación es ASINCRÓNICA. El POST devuelve 202 y el archivo aparece
    después, así que el script consulta el listado hasta que aparezca uno nuevo.

.PARAMETER Dias
    Cuántos días hacia atrás pedir. Por defecto 30.

.PARAMETER AccessToken
    Si no se pasa, se lee de user-secrets del proyecto.
    OJO: el de la máquina de desarrollo suele ser el de la cuenta VENDEDORA DE
    PRUEBA, no el de producción. El script informa contra qué cuenta habla antes
    de pedir nada — leer esa línea antes de sacar conclusiones del CSV.

.PARAMETER WebConfig
    Ruta al web.config del servidor, para leer de ahí el token de PRODUCCIÓN.
    Es la forma preferida: no queda el token en el historial de PowerShell.

.PARAMETER Salida
    Carpeta donde dejar el CSV. Por defecto .\reportes (ignorada por git).

.PARAMETER SoloListar
    No genera nada: sólo consulta si ya hay reportes disponibles para bajar.

.EJEMPLO
    # Cuenta de desarrollo (la de prueba), token desde user-secrets
    .\ReporteLiberaciones.ps1

    # Cuenta de PRODUCCIÓN, token desde el web.config del servidor
    .\ReporteLiberaciones.ps1 -WebConfig "\\SERVIDOR\c$\inetpub\wwwroot\TecnisegurMP Api\web.config"

    # Ver si ya se generó algo, sin pedir otro
    .\ReporteLiberaciones.ps1 -SoloListar
#>

[CmdletBinding()]
param(
    [int]    $Dias = 30,
    [string] $AccessToken,
    [string] $WebConfig,
    [string] $Salida,
    [switch] $SoloListar
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$raiz = Split-Path -Parent $PSScriptRoot
if (-not $Salida) { $Salida = Join-Path $PSScriptRoot 'reportes' }
if (-not (Test-Path $Salida)) { New-Item -ItemType Directory -Path $Salida | Out-Null }

# ---------------------------------------------------------------------------
# 1) Token
# ---------------------------------------------------------------------------
# Desde el web.config del servidor. Es la forma preferida para consultar la
# cuenta de PRODUCCION: evita pegar el token en la linea de comandos, donde
# quedaria guardado en el historial de PowerShell (PSReadLine) en texto plano.
if (-not $AccessToken -and $WebConfig) {
    if (-not (Test-Path $WebConfig)) {
        throw "No existe el archivo '$WebConfig'."
    }

    Write-Host "Leyendo el access token del web.config..." -ForegroundColor DarkGray

    $xml = [xml](Get-Content $WebConfig -Raw)

    $nodo = $xml.SelectSingleNode(
        "//environmentVariable[@name='MercadoPago__AccessToken']")

    if (-not $nodo) {
        throw ("No se encontro la variable 'MercadoPago__AccessToken' en $WebConfig. " +
               "Revisa el bloque <environmentVariables> del aspNetCore.")
    }

    $AccessToken = $nodo.value
}

if (-not $AccessToken) {
    Write-Host "Leyendo el access token de user-secrets..." -ForegroundColor DarkGray

    $proyecto = Join-Path $raiz 'src\TecnisegurMercadoPago.Api'
    $secretos = dotnet user-secrets list --project $proyecto

    $linea = $secretos | Where-Object { $_ -like 'MercadoPago:AccessToken*' }

    if (-not $linea) {
        throw "No se encontro 'MercadoPago:AccessToken' en user-secrets. Pasalo con -AccessToken."
    }

    $AccessToken = ($linea -split '=', 2)[1].Trim()
}

$cabeceras = @{ Authorization = "Bearer $AccessToken" }

# ---------------------------------------------------------------------------
# 2) Contra qué cuenta estamos hablando
#
#    Es el paso que evita el malentendido clásico de esta integración: el ultimo
#    segmento del access token es el id de la cuenta duenia, y hay dos en juego.
#      3521850855 = TECNISEGURURUGUAY (real, produccion)
#      3572201273 = vendedora de prueba
#    Un CSV vacio significa cosas muy distintas segun cual sea.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== Cuenta ==" -ForegroundColor Cyan

try {
    $yo = Invoke-RestMethod -Uri 'https://api.mercadopago.com/users/me' -Headers $cabeceras
}
catch {
    throw "El token no sirve para consultar /users/me. Detalle: $($_.Exception.Message)"
}

Write-Host ("  id       : {0}" -f $yo.id)
Write-Host ("  nickname : {0}" -f $yo.nickname)
Write-Host ("  sitio    : {0}" -f $yo.site_id)

if ("$($yo.id)" -eq '3521850855') {
    Write-Host "  -> Cuenta REAL de produccion." -ForegroundColor Yellow
}
else {
    Write-Host "  -> NO es la cuenta de produccion (3521850855)." -ForegroundColor Yellow
    Write-Host "     Un reporte vacio acá no dice nada sobre la operacion real." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 3) Listado previo, para reconocer cuál es el reporte nuevo
# ---------------------------------------------------------------------------
$urlBase = 'https://api.mercadopago.com/v1/account/release_report'

Write-Host ""
Write-Host "== Reportes ya existentes ==" -ForegroundColor Cyan

try {
    $previos = Invoke-RestMethod -Uri "$urlBase/list" -Headers $cabeceras
}
catch {
    Write-Host "  No se pudo listar. Puede ser que el token no tenga permiso sobre reportes." -ForegroundColor Red
    throw
}

$nombresPrevios = @()
if ($previos) { $nombresPrevios = @($previos | ForEach-Object { $_.file_name }) }

Write-Host ("  {0} reporte(s) previo(s)." -f $nombresPrevios.Count)

# ---------------------------------------------------------------------------
# 4) Generar
# ---------------------------------------------------------------------------
$hasta = (Get-Date).ToUniversalTime()
$desde = $hasta.AddDays(-$Dias)

# SIN milisegundos. Con ellos la API responde 400 con un mensaje que despista:
#   {"message":"Must specify begin_date parameter","error":"invalid_begin_date"}
# El parámetro está; lo que no acepta es el formato. Verificado el 31/07/2026.
$cuerpo = @{
    begin_date = $desde.ToString("yyyy-MM-ddTHH:mm:ss") + "Z"
    end_date   = $hasta.ToString("yyyy-MM-ddTHH:mm:ss") + "Z"
} | ConvertTo-Json

Write-Host ""
Write-Host "== Generando ==" -ForegroundColor Cyan
Write-Host ("  Rango: {0}  ->  {1}  (UTC)" -f $desde.ToString('dd/MM/yyyy'), $hasta.ToString('dd/MM/yyyy'))

if ($SoloListar) {
    Write-Host "  (omitido: -SoloListar)" -ForegroundColor DarkGray
}
else {
    try {
        $creado = Invoke-RestMethod -Uri $urlBase -Method Post -Headers $cabeceras `
            -ContentType 'application/json' -Body $cuerpo
    }
    catch {
        $resp = $_.Exception.Response
        if ($resp) {
            $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
            Write-Host ("  HTTP {0}" -f [int]$resp.StatusCode) -ForegroundColor Red
            Write-Host ("  " + $sr.ReadToEnd()) -ForegroundColor Red
        }
        throw
    }

    Write-Host ("  Aceptado. id={0} status={1} formato={2}" -f `
        $creado.id, $creado.status, $creado.format)
}

# ---------------------------------------------------------------------------
# 5) Esperar a que aparezca
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== Esperando el archivo ==" -ForegroundColor Cyan

$nuevo = $null
$intentos = 0

# Con -SoloListar no hay nada que esperar: se toma el más reciente de los que ya
# están, si es que hay alguno.
if ($SoloListar) {
    $nuevo = $previos | Select-Object -First 1

    if (-not $nuevo) {
        Write-Host "  No hay ningun reporte disponible para descargar." -ForegroundColor Yellow
        return
    }
}

while (-not $SoloListar -and -not $nuevo -and $intentos -lt 24) {
    $intentos++
    Start-Sleep -Seconds 5

    $actuales = Invoke-RestMethod -Uri "$urlBase/list" -Headers $cabeceras
    $nuevo = $actuales | Where-Object { $nombresPrevios -notcontains $_.file_name } |
             Select-Object -First 1

    if (-not $nuevo) { Write-Host ("  ... {0}s" -f ($intentos * 5)) -ForegroundColor DarkGray }
}

if (-not $nuevo) {
    Write-Host ""
    Write-Host "No aparecio ningun archivo en 2 minutos." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Lo mas probable NO es un error: si la cuenta no tuvo movimientos en el" -ForegroundColor Yellow
    Write-Host "rango pedido, MercadoPago no genera archivo. El reporte queda en 'pending'" -ForegroundColor Yellow
    Write-Host "y /list sigue devolviendo []." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Para descartar que sea demora, volve mas tarde con:" -ForegroundColor DarkGray
    Write-Host "  .\ReporteLiberaciones.ps1 -SoloListar" -ForegroundColor DarkGray
    return
}

Write-Host ("  Listo: {0}" -f $nuevo.file_name) -ForegroundColor Green

# ---------------------------------------------------------------------------
# 6) Descargar
# ---------------------------------------------------------------------------
$destino = Join-Path $Salida $nuevo.file_name

Invoke-WebRequest -Uri "$urlBase/$($nuevo.file_name)" -Headers $cabeceras -OutFile $destino

Write-Host ""
Write-Host "== Archivo ==" -ForegroundColor Cyan
Write-Host ("  {0}" -f $destino)

# ---------------------------------------------------------------------------
# 7) Lo que vinimos a ver
# ---------------------------------------------------------------------------
$lineas = Get-Content $destino

Write-Host ""
Write-Host "== COLUMNAS REALES ==" -ForegroundColor Cyan

if ($lineas.Count -eq 0) {
    Write-Host "  El archivo vino vacio." -ForegroundColor Yellow
    return
}

# El CSV de MercadoPago usa PUNTO Y COMA, no coma. Verificado contra el reporte
# real de la cuenta de producción el 31/07/2026. Separar por coma devuelve una
# sola columna y hace parecer que faltan todas.
$separador = ';'
if (($lineas[0] -split ';').Count -lt 2) { $separador = ',' }

$columnas = $lineas[0] -split $separador
for ($i = 0; $i -lt $columnas.Count; $i++) {
    Write-Host ("  {0,2}. {1}" -f ($i + 1), $columnas[$i].Trim('"'))
}

Write-Host ""
Write-Host ("  Total de columnas: {0}" -f $columnas.Count)
Write-Host ("  Total de filas   : {0}" -f [Math]::Max(0, $lineas.Count - 1))

# Las tres columnas de las que depende todo el conciliador.
Write-Host ""
Write-Host "== Columnas clave para la conciliacion ==" -ForegroundColor Cyan
foreach ($clave in @('SOURCE_ID', 'EXTERNAL_REFERENCE', 'RECORD_TYPE', 'NET_CREDIT_AMOUNT')) {
    $hay = $columnas | Where-Object { $_.Trim('"').Trim() -eq $clave }
    if ($hay) { Write-Host ("  [SI] {0}" -f $clave) -ForegroundColor Green }
    else      { Write-Host ("  [NO] {0}  <- habria que agregarla por /config" -f $clave) -ForegroundColor Yellow }
}

if ($lineas.Count -gt 1) {
    Write-Host ""
    Write-Host "== Primeras filas ==" -ForegroundColor Cyan
    $lineas | Select-Object -Skip 1 -First 10 | ForEach-Object { Write-Host ("  " + $_) }

    $datos = Import-Csv $destino
    $tipos = $datos | Group-Object RECORD_TYPE -ErrorAction SilentlyContinue

    if ($tipos) {
        Write-Host ""
        Write-Host "== Valores de RECORD_TYPE ==" -ForegroundColor Cyan
        $tipos | ForEach-Object { Write-Host ("  {0,-30} {1}" -f $_.Name, $_.Count) }
    }
}

Write-Host ""
Write-Host "Listo. Lo que importa: si SOURCE_ID y EXTERNAL_REFERENCE estan," -ForegroundColor Green
Write-Host "el conciliador se puede construir cruzando contra MpPaymentId." -ForegroundColor Green
