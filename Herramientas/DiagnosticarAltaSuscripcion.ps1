<#
.SINOPSIS
    Aisla que campo hace que MercadoPago devuelva 500 al crear un preapproval.

.DESCRIPCION
    Manda el payload que fallo —copiado del log del servidor— y despues una serie
    de variantes con UN campo cambiado por vez. La primera que devuelva 201
    identifica al culpable.

    SEGURIDAD: los preapproval se crean en status "pending". Un preapproval
    pendiente NO cobra nada: solo existe como link a la espera de que alguien lo
    autorice. Igual, el script CANCELA de inmediato cualquiera que se cree, y al
    final lista lo que quedo vivo para que se pueda verificar a mano.

.PARAMETER MailAlternativo
    Un mail real para probar contra el placeholder NOTIENE@NOTIENE.COM.
    Tiene que ser de un dominio que exista y NO puede ser de la cuenta de
    Tecnisegur: nadie puede suscribirse a si mismo.

.EJEMPLO
    .\DiagnosticarAltaSuscripcion.ps1 -Pedir -MailAlternativo "alguien@gmail.com"
#>

[CmdletBinding()]
param(
    [string] $AccessToken,
    [string] $WebConfig,
    [switch] $Pedir,
    [string] $MailAlternativo = 'prueba.tecnisegur@gmail.com',
    [string] $ExternalReference = 'DIAG-31'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$raiz = Split-Path -Parent $PSScriptRoot

# --------------------------------------------------------------------------
# Token
# --------------------------------------------------------------------------
if ($Pedir -and -not $AccessToken) {
    $seguro  = Read-Host "Access token de PRODUCCION" -AsSecureString
    $puntero = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($seguro)
    try   { $AccessToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($puntero) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($puntero) }
}

if (-not $AccessToken -and $WebConfig) {
    $xml  = [xml](Get-Content $WebConfig -Raw)
    $nodo = $xml.SelectSingleNode("//environmentVariable[@name='MercadoPago__AccessToken']")
    if (-not $nodo) { throw "No se encontro MercadoPago__AccessToken en $WebConfig." }
    $AccessToken = $nodo.value
}

if (-not $AccessToken) {
    $proyecto = Join-Path $raiz 'src\TecnisegurMercadoPago.Api'
    $linea = (dotnet user-secrets list --project $proyecto) |
             Where-Object { $_ -like 'MercadoPago:AccessToken*' }
    if (-not $linea) { throw "Sin token. Usa -Pedir o -WebConfig." }
    $AccessToken = ($linea -split '=', 2)[1].Trim()
}

$cabeceras = @{ Authorization = "Bearer $AccessToken" }

$yo = Invoke-RestMethod -Uri 'https://api.mercadopago.com/users/me' -Headers $cabeceras
Write-Host ""
Write-Host ("Cuenta: {0} ({1})" -f $yo.nickname, $yo.id) -ForegroundColor Cyan
Write-Host ("Mail de la cuenta: {0}" -f $yo.email) -ForegroundColor DarkGray

if ($yo.email -eq $MailAlternativo) {
    throw "El mail alternativo es el de la cuenta cobradora. Nadie puede suscribirse a si mismo."
}

# --------------------------------------------------------------------------
# Payload base: el que fallo, tal cual salio del log.
# Las fechas se recalculan para que sigan siendo futuras.
# --------------------------------------------------------------------------
$fmt    = "yyyy-MM-ddTHH:mm:ss.fffzzz"
$inicio = (Get-Date).Date.AddDays(1).AddHours(12).ToString($fmt)
$fin    = (Get-Date).AddMonths(2).ToString($fmt)

function NuevoPayload {
    return @{
        reason             = 'TECNISEGUR ALARMAS - MARIO CORTEZ'
        external_reference = $ExternalReference
        payer_email        = 'NOTIENE@NOTIENE.COM'
        back_url           = 'https://www.tecnisegur.com.uy?cotizacion=31'
        status             = 'pending'
        auto_recurring     = @{
            frequency          = 1
            frequency_type     = 'months'
            transaction_amount = 1870.00
            currency_id        = 'UYU'
            start_date         = $inicio
            end_date           = $fin
        }
    }
}

$creados = @()

function Probar {
    param([string] $Nombre, [hashtable] $Payload)

    $json = $Payload | ConvertTo-Json -Depth 6

    Write-Host ""
    Write-Host ("--- {0} ---" -f $Nombre) -ForegroundColor Cyan

    try {
        $r = Invoke-RestMethod -Uri 'https://api.mercadopago.com/preapproval' `
             -Method Post -Headers $cabeceras -ContentType 'application/json' -Body $json

        Write-Host ("  201 OK  id={0}" -f $r.id) -ForegroundColor Green
        $script:creados += $r.id

        # Cancelar de inmediato: no dejar recursos vivos en la cuenta real.
        try {
            Invoke-RestMethod -Uri "https://api.mercadopago.com/preapproval/$($r.id)" `
                -Method Put -Headers $cabeceras -ContentType 'application/json' `
                -Body (@{ status = 'cancelled' } | ConvertTo-Json) | Out-Null
            Write-Host "  cancelado" -ForegroundColor DarkGray
            $script:creados = $script:creados | Where-Object { $_ -ne $r.id }
        }
        catch {
            Write-Host "  NO SE PUDO CANCELAR - revisar a mano" -ForegroundColor Red
        }

        return $true
    }
    catch {
        $resp = $_.Exception.Response
        $codigo = if ($resp) { [int]$resp.StatusCode } else { 0 }
        $cuerpo = ''
        if ($resp) {
            $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
            $cuerpo = $sr.ReadToEnd()
        }
        Write-Host ("  {0}  {1}" -f $codigo, $cuerpo) -ForegroundColor Yellow
        return $false
    }
}

# --------------------------------------------------------------------------
# 1) Control: reproducir el fallo
# --------------------------------------------------------------------------
$control = Probar "CONTROL - el payload que fallo" (NuevoPayload)

if ($control) {
    Write-Host ""
    Write-Host "El payload original funciono. El 500 era transitorio de MercadoPago." -ForegroundColor Green
    Write-Host "Reintenta el alta desde EmpleadoWeb." -ForegroundColor Green
    return
}

# --------------------------------------------------------------------------
# 2) Un campo por vez
# --------------------------------------------------------------------------
$p = NuevoPayload; $p.payer_email = $MailAlternativo
$okMail = Probar "MAIL real en vez de NOTIENE@NOTIENE.COM" $p

$p = NuevoPayload; $p.back_url = 'https://www.tecnisegur.com.uy/?cotizacion=31'
$okBarra = Probar "BACK_URL con barra antes del ?" $p

$p = NuevoPayload; $p.Remove('back_url')
$okSinUrl = Probar "SIN back_url" $p

$p = NuevoPayload; $p.auto_recurring.Remove('end_date')
$okSinFin = Probar "SIN end_date" $p

$p = NuevoPayload; $p.auto_recurring.Remove('start_date')
$okSinInicio = Probar "SIN start_date" $p

$p = @{
    reason             = 'TECNISEGUR ALARMAS - MARIO CORTEZ'
    external_reference = $ExternalReference
    payer_email        = $MailAlternativo
    status             = 'pending'
    auto_recurring     = @{
        frequency = 1; frequency_type = 'months'
        transaction_amount = 1870.00; currency_id = 'UYU'
    }
}
$okMinimo = Probar "MINIMO - mail real, sin back_url ni fechas" $p

# --------------------------------------------------------------------------
# 3) Veredicto
# --------------------------------------------------------------------------
Write-Host ""
Write-Host "=== VEREDICTO ===" -ForegroundColor Cyan

if ($okMail)          { Write-Host "  El culpable es payer_email = NOTIENE@NOTIENE.COM." -ForegroundColor Red }
elseif ($okBarra)     { Write-Host "  El culpable es el back_url sin barra antes del '?'." -ForegroundColor Red }
elseif ($okSinUrl)    { Write-Host "  El culpable es el back_url (el dominio entero, no la barra)." -ForegroundColor Red }
elseif ($okSinFin)    { Write-Host "  El culpable es end_date." -ForegroundColor Red }
elseif ($okSinInicio) { Write-Host "  El culpable es start_date." -ForegroundColor Red }
elseif ($okMinimo)    { Write-Host "  Falla la combinacion, no un campo suelto: el minimo pasa." -ForegroundColor Red }
else {
    Write-Host "  Ninguna variante paso. No es el payload." -ForegroundColor Red
    Write-Host "  Sospechar de la cuenta: preapproval sin plan puede no estar" -ForegroundColor Yellow
    Write-Host "  habilitado, o la aplicacion no es de tipo Suscripciones." -ForegroundColor Yellow
}

if ($creados.Count -gt 0) {
    Write-Host ""
    Write-Host "ATENCION - quedaron preapproval sin cancelar:" -ForegroundColor Red
    $creados | ForEach-Object { Write-Host ("  {0}" -f $_) -ForegroundColor Red }
}
