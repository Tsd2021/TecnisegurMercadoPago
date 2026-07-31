<#
.SINOPSIS
    Verifica contra un cobro real que el calculo de comision y retenciones de la
    API coincide con lo que informa MercadoPago.

.DESCRIPCION
    Solo lectura: un GET a /v1/payments/{id}. No modifica nada.

    Responde la pregunta que quedo abierta al escribir
    09_SepararComisionDeRetenciones.sql:

        El desglose documentado (mercadopago_fee + tax_withholding-uruguay +
        tax_withholding-lif_debito) salio de charges_details.
        Pero PagoRespuesta.ComisionCalculada suma fee_details.

        Si fee_details trae los tres renglones, Comision queda en 2,53 y
        Retenciones en 0: exactamente el error que el script 09 vino a
        corregir, pero al reves y sin sintoma visible.

    El script imprime los dos arrays por separado, replica el calculo del C# y
    lo contrasta con el reporte de Liberaciones.

.PARAMETER PagoId
    Id del pago a inspeccionar. Por defecto 166657246137, el cobro de $20 con
    debito del 06/07/2026 que ya esta conciliado contra el CSV de Liberaciones.

.PARAMETER AccessToken
    Si no se pasa, se lee de user-secrets del proyecto. El script informa contra
    que cuenta habla antes de consultar: el pago por defecto es de la cuenta de
    PRODUCCION (3521850855) y con el token de prueba da 404.

.PARAMETER WebConfig
    Ruta al web.config del servidor, para leer de ahi el token de PRODUCCION sin
    que quede en el historial de PowerShell.

.PARAMETER Pedir
    Pide el token por pantalla. Lo que se tipea en un Read-Host NO queda en el
    historial de PSReadLine —solo queda la linea de comando—, asi que es la forma
    mas limpia de usar el token de produccion cuando no hay acceso al servidor.

    OJO: hay que correrlo en una ventana de PowerShell propia. A traves de una
    herramienta que ejecuta sin consola interactiva, Read-Host falla.

.EJEMPLO
    .\VerificarDesglosePago.ps1
    .\VerificarDesglosePago.ps1 -Pedir
    .\VerificarDesglosePago.ps1 -WebConfig "\\SERVIDOR\c$\inetpub\wwwroot\TecnisegurMP Api\web.config"
    .\VerificarDesglosePago.ps1 -PagoId 171086544690
#>

[CmdletBinding()]
param(
    [string] $PagoId = '166657246137',
    [string] $AccessToken,
    [string] $WebConfig,
    [switch] $Pedir
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$raiz = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------------------
# 1) Token  (misma resolucion que ReporteLiberaciones.ps1, mas -Pedir)
# ---------------------------------------------------------------------------
# Read-Host es la via preferida cuando no hay acceso al web.config del servidor:
# el texto tipeado en un prompt no llega al historial de PSReadLine, a diferencia
# de -AccessToken "APP_USR-...", que queda en texto plano en ConsoleHost_history.
if ($Pedir -and -not $AccessToken) {
    $seguro = Read-Host "Access token de PRODUCCION" -AsSecureString

    $puntero = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($seguro)
    try {
        $AccessToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($puntero)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($puntero)
    }

    if (-not $AccessToken) { throw "No se ingreso ningun token." }
}

if (-not $AccessToken -and $WebConfig) {
    if (-not (Test-Path $WebConfig)) { throw "No existe el archivo '$WebConfig'." }

    Write-Host "Leyendo el access token del web.config..." -ForegroundColor DarkGray
    $xml   = [xml](Get-Content $WebConfig -Raw)
    $nodo  = $xml.SelectSingleNode("//environmentVariable[@name='MercadoPago__AccessToken']")

    if (-not $nodo) {
        throw ("No se encontro 'MercadoPago__AccessToken' en $WebConfig. " +
               "Revisa el bloque <environmentVariables> del aspNetCore.")
    }
    $AccessToken = $nodo.value
}

if (-not $AccessToken) {
    Write-Host "Leyendo el access token de user-secrets..." -ForegroundColor DarkGray

    $proyecto = Join-Path $raiz 'src\TecnisegurMercadoPago.Api'
    $secretos = dotnet user-secrets list --project $proyecto
    $linea    = $secretos | Where-Object { $_ -like 'MercadoPago:AccessToken*' }

    if (-not $linea) {
        throw "No se encontro 'MercadoPago:AccessToken' en user-secrets. Pasalo con -AccessToken."
    }
    $AccessToken = ($linea -split '=', 2)[1].Trim()
}

$cabeceras = @{ Authorization = "Bearer $AccessToken" }

# ---------------------------------------------------------------------------
# 2) Contra que cuenta hablamos
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== Cuenta ==" -ForegroundColor Cyan

$yo = Invoke-RestMethod -Uri 'https://api.mercadopago.com/users/me' -Headers $cabeceras
Write-Host ("  id={0}  nickname={1}  sitio={2}" -f $yo.id, $yo.nickname, $yo.site_id)

if ("$($yo.id)" -eq '3521850855') {
    Write-Host "  -> Cuenta REAL de produccion." -ForegroundColor Yellow
}
else {
    Write-Host "  -> NO es la cuenta de produccion (3521850855)." -ForegroundColor Yellow
    Write-Host "     Un 404 en el pago por defecto es esperable: pertenece a la otra cuenta." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 3) El pago
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== Pago $PagoId ==" -ForegroundColor Cyan

try {
    $pago = Invoke-RestMethod -Uri "https://api.mercadopago.com/v1/payments/$PagoId" -Headers $cabeceras
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

$bruto = [decimal]$pago.transaction_amount
$neto  = $null
if ($null -ne $pago.transaction_details.net_received_amount) {
    $neto = [decimal]$pago.transaction_details.net_received_amount
}

Write-Host ("  status            : {0} / {1}" -f $pago.status, $pago.status_detail)
Write-Host ("  medio de pago     : {0} ({1})" -f $pago.payment_method_id, $pago.payment_type_id)
Write-Host ("  transaction_amount: {0:N2}" -f $bruto)
Write-Host ("  net_received      : {0:N2}" -f $neto)
Write-Host ("  date_approved     : {0}" -f $pago.date_approved)
Write-Host ("  money_release_date: {0}" -f $pago.money_release_date)
Write-Host ("  release_status    : {0}" -f $pago.money_release_status)

# ---------------------------------------------------------------------------
# 4) Los dos arrays, uno al lado del otro.
#    Esta es la razon de ser del script.
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== fee_details  (lo que suma ComisionCalculada) ==" -ForegroundColor Cyan

# [decimal]0 y no 0m: el sufijo m de C# no existe en PowerShell (aca es d), y lo
# peor es que no falla al parsear —se interpreta como una llamada a comando— asi
# que el error recien aparece al ejecutar esta linea.
$sumaFee = [decimal]0
if ($pago.fee_details) {
    foreach ($f in $pago.fee_details) {
        Write-Host ("  {0,-32} {1,10:N2}   payer={2}" -f $f.type, [decimal]$f.amount, $f.fee_payer)
        $sumaFee += [decimal]$f.amount
    }
    Write-Host ("  {0,-32} {1,10:N2}" -f '  TOTAL', $sumaFee) -ForegroundColor White
}
else {
    Write-Host "  (vacio o ausente)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "== charges_details  (de donde salio el desglose documentado) ==" -ForegroundColor Cyan

$sumaCargos = [decimal]0
$sumaTax    = [decimal]0
if ($pago.charges_details) {
    foreach ($c in $pago.charges_details) {
        $importe = [decimal]0
        if ($null -ne $c.amounts.original) { $importe = [decimal]$c.amounts.original }

        $devuelto = ''
        if ($c.amounts.refunded -and [decimal]$c.amounts.refunded -ne 0) {
            $devuelto = "  refunded=$($c.amounts.refunded)"
        }

        Write-Host ("  {0,-32} {1,10:N2}   type={2}{3}" -f $c.name, $importe, $c.type, $devuelto)
        $sumaCargos += $importe

        # En MLU las retenciones vienen como name 'tax_withholding-*'.
        if ($c.name -like 'tax*') { $sumaTax += $importe }
    }
    Write-Host ("  {0,-32} {1,10:N2}" -f '  TOTAL', $sumaCargos) -ForegroundColor White
}
else {
    Write-Host "  (vacio o ausente)" -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 5) El calculo del C#, replicado
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== Lo que persistiria la API hoy ==" -ForegroundColor Cyan

$comision = $null
if ($pago.fee_details) { $comision = $sumaFee }

$retenciones = $null
if ($null -ne $neto) {
    $descuento = $bruto - $neto
    $base = [decimal]0
    if ($null -ne $comision) { $base = $comision }
    $retenciones = $descuento - $base
}

Write-Host ("  Monto        : {0,10:N2}" -f $bruto)
Write-Host ("  MontoNeto    : {0,10:N2}" -f $neto)
Write-Host ("  Comision     : {0,10:N2}   <- sum(fee_details.amount)" -f $comision)
Write-Host ("  Retenciones  : {0,10:N2}   <- (bruto - neto) - Comision" -f $retenciones)

# ---------------------------------------------------------------------------
# 6) Veredicto
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== Veredicto ==" -ForegroundColor Cyan

if ($null -eq $neto) {
    Write-Host "  Sin net_received_amount no se puede concluir nada." -ForegroundColor Yellow
}
elseif ($sumaTax -gt 0 -and [Math]::Abs($comision - $sumaTax) -lt 0.005) {
    Write-Host "  PROBLEMA: fee_details suma lo mismo que las retenciones." -ForegroundColor Red
}
elseif ($sumaTax -gt 0 -and $comision -ge ($sumaCargos - 0.005)) {
    Write-Host "  PROBLEMA: fee_details YA INCLUYE las retenciones." -ForegroundColor Red
    Write-Host ("  Comision quedaria en {0:N2} y Retenciones en {1:N2}." -f $comision, $retenciones) -ForegroundColor Red
    Write-Host "  Hay que calcular la comision filtrando charges_details por type='fee'," -ForegroundColor Red
    Write-Host "  no sumando fee_details. Corregir PagoRespuesta.ComisionCalculada." -ForegroundColor Red
}
elseif ($sumaTax -gt 0 -and [Math]::Abs($retenciones - $sumaTax) -lt 0.02) {
    Write-Host "  OK: fee_details trae solo la comision de MercadoPago, y las" -ForegroundColor Green
    Write-Host ("  retenciones calculadas ({0:N2}) coinciden con los tax_withholding" -f $retenciones) -ForegroundColor Green
    Write-Host ("  de charges_details ({0:N2}). El calculo del C# es correcto." -f $sumaTax) -ForegroundColor Green
}
elseif ($null -eq $comision) {
    Write-Host "  fee_details vino vacio: Comision quedaria en NULL y Retenciones" -ForegroundColor Yellow
    Write-Host ("  se llevaria el descuento entero ({0:N2}). Revisar." -f $retenciones) -ForegroundColor Yellow
}
else {
    Write-Host "  Sin tax_withholding en charges_details no hay con que contrastar." -ForegroundColor Yellow
    Write-Host ("  Comision {0:N2} + Retenciones {1:N2} = {2:N2} de descuento." -f `
        $comision, $retenciones, ($bruto - $neto))
}

# Contraste con el reporte de Liberaciones ya descargado, si esta.
$csv = Get-ChildItem (Join-Path $PSScriptRoot 'reportes') -Filter '*.csv' -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($csv) {
    $fila = Import-Csv $csv.FullName -Delimiter ';' |
            Where-Object { $_.SOURCE_ID -eq $PagoId } | Select-Object -First 1

    if ($fila) {
        Write-Host ""
        Write-Host "== Contra el reporte de Liberaciones ==" -ForegroundColor Cyan
        Write-Host ("  archivo           : {0}" -f $csv.Name)
        Write-Host ("  NET_CREDIT_AMOUNT : {0,10}   (API: {1:N2})" -f $fila.NET_CREDIT_AMOUNT, $neto)
        Write-Host ("  MP_FEE_AMOUNT     : {0,10}   (API: {1:N2})" -f $fila.MP_FEE_AMOUNT, $comision)
        Write-Host ("  TAXES_AMOUNT      : {0,10}   (API: {1:N2})" -f $fila.TAXES_AMOUNT, $retenciones)
        Write-Host ("  DATE (liberacion) : {0}" -f $fila.DATE)
        Write-Host ("  money_release_date: {0}" -f $pago.money_release_date)
        Write-Host ""
        Write-Host "  Las tres lineas de importe tienen que coincidir en valor absoluto," -ForegroundColor DarkGray
        Write-Host "  y DATE con money_release_date. Si no, la conciliacion no cierra." -ForegroundColor DarkGray
    }
}
