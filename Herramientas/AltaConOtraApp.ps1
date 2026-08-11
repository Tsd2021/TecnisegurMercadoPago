<#
.SINOPSIS
    Crea UN preapproval con el token de otra aplicacion de la MISMA cuenta, y lo
    deja vivo para que alguien le cargue una tarjeta real.

.DESCRIPCION
    Aisla la variable que el experimento del 10/08 no habia aislado. Aquella
    prueba cambio la cuenta cobradora Y la aplicacion al mismo tiempo (cuenta de
    prueba 3572201273 + aplicacion 244721644743240), asi que no distingue estas
    dos hipotesis:

        la CUENTA 3521850855 no puede constituir debitos recurrentes
        la APLICACION 437871649677590 no puede constituir debitos recurrentes

    Corriendo esto con el token de una aplicacion distinta de la misma cuenta,
    la unica variable que cambia es la aplicacion:

        autoriza  -> el problema es la aplicacion. Se migra y listo, sin soporte.
        falla     -> el problema es la cuenta. El ticket queda mucho mas firme.

    El payload es COPIA EXACTA del que fallo el 11/08 con el preapproval
    498c7bbb05124f908ed0eb6bafd85b8c, tomado del log del servidor. No cambiarlo:
    cualquier diferencia reintroduce una variable y arruina el experimento.

    A diferencia de DiagnosticarAltaSuscripcion.ps1, este NO cancela lo que crea
    —hace falta vivo para poder autorizarlo—. Cancelalo despues con -Cancelar.

    SEGURIDAD: se crea en status "pending", que no cobra nada por si mismo. El
    cobro recien ocurre si alguien completa el checkout, que es justamente lo que
    se quiere medir. Con free_trial de 15 dias, la primera cuota no se cobra
    hasta 15 dias despues de autorizada.

.PARAMETER AccessToken
    Token de PRODUCCION de la aplicacion nueva. Si no se pasa, usar -Pedir.

.PARAMETER Pedir
    Pide el token por pantalla con Read-Host, que no queda en el historial de
    PSReadLine. Hay que correrlo en una ventana de PowerShell propia.

.PARAMETER PayerEmail
    Correo del pagador. Tiene que ser real, y el cliente tiene que escribir ESE
    MISMO en el checkout o MercadoPago rechaza con "Tu e-mail no coincide con el
    de la suscripcion".

.PARAMETER Cancelar
    Id de un preapproval a cancelar. Modo limpieza: no crea nada.

.EJEMPLO
    .\AltaConOtraApp.ps1 -Pedir
    .\AltaConOtraApp.ps1 -Pedir -Cancelar 498c7bbb05124f908ed0eb6bafd85b8c
#>

[CmdletBinding()]
param(
    [string] $AccessToken,
    [switch] $Pedir,
    [string] $PayerEmail        = 'diegochiquiar@gmail.com',
    [string] $ExternalReference = 'COT-36',
    [decimal] $Monto            = 15.00,
    [int]    $DiasPrueba        = 15,
    [string] $Cancelar
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ---------------------------------------------------------------------------
# 1) Token  (misma resolucion que ForensePreapproval.ps1)
# ---------------------------------------------------------------------------
if ($Pedir -and -not $AccessToken) {
    $seguro  = Read-Host "Access token de PRODUCCION de la aplicacion nueva" -AsSecureString
    $puntero = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($seguro)
    try   { $AccessToken = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($puntero) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($puntero) }
}

if (-not $AccessToken) { throw "Falta el access token. Usar -Pedir o -AccessToken." }

$cabeceras = @{ Authorization = "Bearer $AccessToken" }

# ---------------------------------------------------------------------------
# 2) Verificar que sea la MISMA cuenta
#
#    Es la guarda que hace valido al experimento. Si el token resulta ser de
#    otra cuenta, vuelve a cambiar dos variables a la vez y la corrida no mide
#    nada. Mejor frenar que sacar una conclusion falsa.
# ---------------------------------------------------------------------------
$CuentaEsperada = 3521850855

$yo = Invoke-RestMethod -Uri 'https://api.mercadopago.com/users/me' -Headers $cabeceras
Write-Host ""
Write-Host ("Cuenta   : {0}  ({1})" -f $yo.id, $yo.nickname)
Write-Host ("Site     : {0}" -f $yo.site_id)

if ($yo.id -ne $CuentaEsperada) {
    throw ("El token es de la cuenta {0} y se esperaba {1}. " -f $yo.id, $CuentaEsperada) +
          "Con otra cuenta cambian dos variables y el experimento no distingue nada."
}

# ---------------------------------------------------------------------------
# 3) Modo limpieza
# ---------------------------------------------------------------------------
if ($Cancelar) {
    $cuerpo = @{ status = 'cancelled' } | ConvertTo-Json
    $r = Invoke-RestMethod -Method Put -Uri "https://api.mercadopago.com/preapproval/$Cancelar" `
                           -Headers $cabeceras -ContentType 'application/json' -Body $cuerpo
    Write-Host ("Cancelado {0}. Estado: {1}" -f $Cancelar, $r.status) -ForegroundColor Yellow
    return
}

# ---------------------------------------------------------------------------
# 4) El payload — copia exacta del que fallo el 11/08
# ---------------------------------------------------------------------------
$payload = [ordered]@{
    reason              = 'TECNISEGUR ALARMAS - DIEGO C TEST'
    external_reference  = $ExternalReference
    payer_email         = $PayerEmail
    back_url            = 'https://www.tecnisegur.com.uy/'
    notification_url    = 'https://mpapi.tecnisegur.com.uy/api/webhook'
    status              = 'pending'
    auto_recurring      = [ordered]@{
        frequency          = 1
        frequency_type     = 'months'
        transaction_amount = $Monto
        currency_id        = 'UYU'
        free_trial         = [ordered]@{
            frequency      = $DiasPrueba
            frequency_type = 'days'
        }
    }
}

$json = $payload | ConvertTo-Json -Depth 6 -Compress
Write-Host ""
Write-Host "Payload:" -ForegroundColor DarkGray
Write-Host $json -ForegroundColor DarkGray
Write-Host ""

$respuesta = Invoke-RestMethod -Method Post -Uri 'https://api.mercadopago.com/preapproval' `
                               -Headers $cabeceras -ContentType 'application/json' -Body $json

Write-Host ("preapproval  : {0}" -f $respuesta.id)          -ForegroundColor Green
Write-Host ("status       : {0}" -f $respuesta.status)
Write-Host ("application  : {0}" -f $respuesta.application_id)
Write-Host ("collector    : {0}" -f $respuesta.collector_id)
Write-Host ""
Write-Host "init_point:" -ForegroundColor Cyan
Write-Host $respuesta.init_point
Write-Host ""
Write-Host ("En el checkout hay que escribir EXACTAMENTE este correo: {0}" -f $PayerEmail) -ForegroundColor Yellow
Write-Host ""
Write-Host "Despues del intento, medir con:" -ForegroundColor DarkGray
Write-Host ("  .\ForensePreapproval.ps1 -PreapprovalId {0} -Pedir -Etiqueta 'app2 tras tarjeta'" -f $respuesta.id) -ForegroundColor DarkGray
Write-Host ""
Write-Host "card_id / payment_method_id poblados = la aplicacion era el problema." -ForegroundColor DarkGray
