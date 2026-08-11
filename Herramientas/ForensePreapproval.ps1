<#
.SINOPSIS
    Fotografia el estado real de UN preapproval en MercadoPago y responde si
    llego a asociarse una tarjeta y si se genero algun cobro.

.DESCRIPCION
    SOLO LECTURA. Hace GET y nada mas: /users/me, /preapproval/{id},
    /authorized_payments/search y /v1/payments/search. No crea, no cancela y no
    modifica nada. Se puede correr contra PRODUCCION sin riesgo.

    Existe para reconstruir la transicion de un preapproval que termina
    cancelado sin haber pasado por authorized. Cada corrida agrega una fila a
    una linea temporal en disco, de modo que corriendolo en los momentos
    relevantes (antes de mandar el link, apenas el cliente carga la tarjeta,
    cuando llega el webhook, cuando aparece cancelado) queda la secuencia
    completa con horas UTC.

    Lo que imprime son los campos que deciden en que punto se rompe:

        status              pending / authorized / paused / cancelled
        payment_method_id   null = MercadoPago nunca asocio un medio de pago
        card_id             null = nunca quedo una tarjeta vinculada
        payer_id            null = nunca se identifico al pagador
        version             sube en cada cambio del recurso del lado de MP
        last_modified       cuando lo cambiaron

    Si status termina en cancelled con payment_method_id y card_id en null y sin
    ningun payment asociado, la falla es ANTERIOR al primer cobro: MercadoPago
    no llego a constituir el debito recurrente.

.PARAMETER PreapprovalId
    Id del preapproval a inspeccionar. Es el que devuelve POST /preapproval y el
    que viaja como data.id en las notificaciones subscription_preapproval.

.PARAMETER Etiqueta
    Nombre del momento que se esta capturando: T0, T2, "tras webhook", etc.
    Va a la linea temporal para poder leerla despues.

.PARAMETER AccessToken
    Si no se pasa, se lee de user-secrets del proyecto. Misma resolucion que
    VerificarDesglosePago.ps1.

.PARAMETER WebConfig
    Ruta al web.config del servidor, para leer el token de PRODUCCION sin que
    quede en el historial de PowerShell.

.PARAMETER Pedir
    Pide el token por pantalla (Read-Host no llega al historial de PSReadLine).
    Hay que correrlo en una ventana de PowerShell propia: a traves de una
    herramienta sin consola interactiva, Read-Host falla.

.PARAMETER CarpetaSalida
    Donde se guardan los snapshots crudos y la linea temporal.
    Por defecto Herramientas\reportes.

.EJEMPLO
    .\ForensePreapproval.ps1 -PreapprovalId 8ef74c91585845b3b72e985de1ee0e7a -Pedir -Etiqueta T0
    .\ForensePreapproval.ps1 -PreapprovalId 8ef74c91585845b3b72e985de1ee0e7a -Pedir -Etiqueta "tras cargar tarjeta"
    .\ForensePreapproval.ps1 -PreapprovalId 8ef74c91585845b3b72e985de1ee0e7a -WebConfig "\\SERVIDOR\c$\inetpub\wwwroot\TecnisegurMP Api\web.config" -Etiqueta T4
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PreapprovalId,

    [string] $Etiqueta = 'snapshot',
    [string] $AccessToken,
    [string] $WebConfig,
    [switch] $Pedir,
    [string] $CarpetaSalida
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$raiz = Split-Path -Parent $PSScriptRoot
if (-not $CarpetaSalida) { $CarpetaSalida = Join-Path $PSScriptRoot 'reportes' }
if (-not (Test-Path $CarpetaSalida)) {
    New-Item -ItemType Directory -Force $CarpetaSalida | Out-Null
}

# ---------------------------------------------------------------------------
# 1) Token  (misma resolucion que VerificarDesglosePago.ps1)
# ---------------------------------------------------------------------------
if ($Pedir -and -not $AccessToken) {
    $seguro = Read-Host "Access token" -AsSecureString

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
    $xml  = [xml](Get-Content $WebConfig -Raw)
    $nodo = $xml.SelectSingleNode("//environmentVariable[@name='MercadoPago__AccessToken']")

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

# El token NUNCA se imprime. Lo unico que se muestra es el prefijo y el ultimo
# segmento, que es el id de la cuenta duena y alcanza para saber contra quien se
# esta hablando (3521850855 = TECNISEGURURUGUAY, produccion).
$partes  = $AccessToken -split '-'
$prefijo = $partes[0]
$duena   = $partes[-1]

$instante = (Get-Date).ToUniversalTime()
$sello    = $instante.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')

function Mascara([string] $correo) {
    if (-not $correo) { return $null }
    $trozos = $correo -split '@', 2
    if ($trozos.Count -ne 2) { return '(ilegible)' }
    $usuario = $trozos[0]
    $visible = if ($usuario.Length -le 2) { $usuario } else { $usuario.Substring(0, 2) }
    return "$visible***@$($trozos[1])"
}

function Valor($v) {
    if ($null -eq $v -or "$v" -eq '') { return 'null' }
    return "$v"
}

# ---------------------------------------------------------------------------
# 2) Contra que cuenta y que aplicacion hablamos
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== Cuenta ==" -ForegroundColor Cyan
Write-Host ("  instante UTC : {0}" -f $sello)
Write-Host ("  credencial   : prefijo={0}  cuenta_duena={1}" -f $prefijo, $duena)

$yo = Invoke-RestMethod -Uri 'https://api.mercadopago.com/users/me' -Headers $cabeceras

Write-Host ("  id={0}  nickname={1}  site={2}  tipo={3}" -f `
    $yo.id, $yo.nickname, $yo.site_id, $yo.user_type)

$billing = $yo.status.billing
$codigos = if ($billing.codes) { ($billing.codes -join ', ') } else { '(ninguno)' }

Write-Host ("  billing.allow={0}  codes={1}" -f (Valor $billing.allow), $codigos)
Write-Host ("  sell.allow   ={0}" -f (Valor $yo.status.sell.allow))

if ("$($yo.id)" -eq '3521850855') {
    Write-Host "  -> Cuenta REAL de PRODUCCION (TECNISEGURURUGUAY)." -ForegroundColor Yellow
}
else {
    Write-Host "  -> NO es la cuenta de produccion (3521850855)." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 3) El preapproval
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== preapproval $PreapprovalId ==" -ForegroundColor Cyan

$pre = $null
try {
    $pre = Invoke-RestMethod -Headers $cabeceras `
        -Uri "https://api.mercadopago.com/preapproval/$PreapprovalId"
}
catch {
    # El mensaje de la excepcion trae el codigo pero no el cuerpo, y el cuerpo es
    # donde MercadoPago explica el motivo. Sin esto un 400 no dice nada.
    $codigo = $null
    $cuerpo  = $null

    if ($_.Exception.Response) {
        $codigo = [int] $_.Exception.Response.StatusCode
        try {
            $lector = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
            $cuerpo = $lector.ReadToEnd()
            $lector.Close()
        }
        catch { }
    }

    Write-Host "  NO SE PUDO LEER: $($_.Exception.Message)" -ForegroundColor Red
    if ($cuerpo) { Write-Host "  Respuesta de MercadoPago: $cuerpo" -ForegroundColor Red }

    if ($codigo -in @(400, 401, 404)) {
        Write-Host ("  La credencial en uso es de la cuenta {0}. Un {1} acá suele significar" -f $yo.id, $codigo) -ForegroundColor Yellow
        Write-Host "  que el preapproval pertenece a OTRA cuenta: hay que usar el token de esa." -ForegroundColor Yellow
    }

    throw
}

$campos = [ordered]@{
    'id'                 = Valor $pre.id
    'version'            = Valor $pre.version
    'application_id'     = Valor $pre.application_id
    'collector_id'       = Valor $pre.collector_id
    'external_reference' = Valor $pre.external_reference
    'payer_id'           = Valor $pre.payer_id
    'payer_email'        = Valor (Mascara $pre.payer_email)
    'status'             = Valor $pre.status
    'card_id'            = Valor $pre.card_id
    'payment_method_id'  = Valor $pre.payment_method_id
    'next_payment_date'  = Valor $pre.next_payment_date
    'date_created'       = Valor $pre.date_created
    'last_modified'      = Valor $pre.last_modified
    'init_point'         = Valor $pre.init_point
    'back_url'           = Valor $pre.back_url
    'notification_url'   = Valor $pre.notification_url
    'reason'             = Valor $pre.reason
    'summarized.status'  = Valor $pre.summarized.status
    'summarized.charged_quantity' = Valor $pre.summarized.charged_quantity
    'auto_recurring.transaction_amount' = Valor $pre.auto_recurring.transaction_amount
    'auto_recurring.currency_id'        = Valor $pre.auto_recurring.currency_id
    'auto_recurring.frequency'          = Valor $pre.auto_recurring.frequency
    'auto_recurring.frequency_type'     = Valor $pre.auto_recurring.frequency_type
    'auto_recurring.start_date'         = Valor $pre.auto_recurring.start_date
    'auto_recurring.end_date'           = Valor $pre.auto_recurring.end_date
    'auto_recurring.free_trial'         = if ($pre.auto_recurring.free_trial) {
                                              "$($pre.auto_recurring.free_trial.frequency) $($pre.auto_recurring.free_trial.frequency_type)"
                                          } else { 'null' }
}

foreach ($c in $campos.GetEnumerator()) {
    $color = 'Gray'
    if ($c.Key -in @('status', 'card_id', 'payment_method_id', 'payer_id')) { $color = 'White' }
    Write-Host ("  {0,-36} {1}" -f $c.Key, $c.Value) -ForegroundColor $color
}

# Campos que MercadoPago devolvio y que este script no conoce. Se listan para no
# perder informacion nueva por no haberla previsto.
$conocidos = @('id','version','application_id','collector_id','external_reference',
               'payer_id','payer_email','status','card_id','payment_method_id',
               'next_payment_date','date_created','last_modified','init_point',
               'back_url','notification_url','reason','summarized','auto_recurring')

$extras = $pre.PSObject.Properties.Name | Where-Object { $_ -notin $conocidos }
if ($extras) {
    Write-Host ""
    Write-Host "  Campos adicionales devueltos por MercadoPago:" -ForegroundColor DarkGray
    foreach ($e in $extras) {
        Write-Host ("    {0,-34} {1}" -f $e, (Valor $pre.$e)) -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------------------
# 4) Hubo cuotas? Hubo pagos?
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "== Cobros asociados ==" -ForegroundColor Cyan

$cuotas = $null
try {
    $cuotas = Invoke-RestMethod -Headers $cabeceras `
        -Uri "https://api.mercadopago.com/authorized_payments/search?preapproval_id=$PreapprovalId"
}
catch {
    Write-Host "  authorized_payments/search fallo: $($_.Exception.Message)" -ForegroundColor Yellow
}

$cantidadCuotas = 0
if ($cuotas) {
    $cantidadCuotas = @($cuotas.results).Count
    Write-Host ("  authorized_payments : {0}" -f $cantidadCuotas)

    foreach ($q in $cuotas.results) {
        Write-Host ("    cuota id={0} status={1} debit_date={2} payment.id={3} payment.status={4}" -f `
            (Valor $q.id), (Valor $q.status), (Valor $q.debit_date),
            (Valor $q.payment.id), (Valor $q.payment.status))
    }
}

$cantidadPagos = 0
if ($pre.external_reference) {
    try {
        $pagos = Invoke-RestMethod -Headers $cabeceras `
            -Uri ("https://api.mercadopago.com/v1/payments/search?external_reference={0}&sort=date_created&criteria=desc" -f $pre.external_reference)

        $cantidadPagos = @($pagos.results).Count
        Write-Host ("  payments (external_reference={0}) : {1}" -f $pre.external_reference, $cantidadPagos)

        foreach ($p in $pagos.results) {
            Write-Host ("    pago id={0} status={1} detail={2} monto={3} creado={4}" -f `
                (Valor $p.id), (Valor $p.status), (Valor $p.status_detail),
                (Valor $p.transaction_amount), (Valor $p.date_created))
        }
    }
    catch {
        Write-Host "  payments/search fallo: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
# 5) Veredicto de este snapshot
# ---------------------------------------------------------------------------
$hayTarjeta = ($pre.card_id) -or ($pre.payment_method_id)
$hayCobro   = ($cantidadCuotas -gt 0) -or ($cantidadPagos -gt 0)

Write-Host ""
Write-Host "== Veredicto ==" -ForegroundColor Cyan
Write-Host ("  status               : {0}" -f (Valor $pre.status))
Write-Host ("  tarjeta asociada     : {0}" -f $(if ($hayTarjeta) { 'SI' } else { 'NO' }))
Write-Host ("  algun cobro generado : {0}" -f $(if ($hayCobro) { 'SI' } else { 'NO' }))

if ($pre.status -eq 'cancelled' -and -not $hayTarjeta -and -not $hayCobro) {
    Write-Host ""
    Write-Host "  -> Cancelado SIN tarjeta asociada y SIN ningun cobro." -ForegroundColor Yellow
    Write-Host "     La falla es ANTERIOR al primer cobro: MercadoPago no llego" -ForegroundColor Yellow
    Write-Host "     a constituir el debito recurrente." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------
# 6) Persistir: snapshot crudo + linea temporal
# ---------------------------------------------------------------------------
# El correo se enmascara antes de guardar: estos archivos se adjuntan al ticket
# de soporte.
$crudo = $pre | ConvertTo-Json -Depth 12
if ($pre.payer_email) {
    $crudo = $crudo.Replace($pre.payer_email, (Mascara $pre.payer_email))
}

$nombre = ('preapproval-{0}-{1}-{2}.json' -f `
    $PreapprovalId,
    ($Etiqueta -replace '[^\w\-]', '_'),
    $instante.ToString('yyyyMMdd-HHmmss'))

$rutaSnapshot = Join-Path $CarpetaSalida $nombre
$crudo | Set-Content -Path $rutaSnapshot -Encoding utf8

$fila = [ordered]@{
    hora_utc          = $sello
    etiqueta          = $Etiqueta
    preapproval_id    = $PreapprovalId
    status            = $pre.status
    version           = $pre.version
    payer_id          = $pre.payer_id
    card_id           = $pre.card_id
    payment_method_id = $pre.payment_method_id
    last_modified     = $pre.last_modified
    application_id    = $pre.application_id
    collector_id      = $pre.collector_id
    cuotas            = $cantidadCuotas
    pagos             = $cantidadPagos
    cuenta_consultada = $yo.id
}

$rutaLinea = Join-Path $CarpetaSalida ("linea-temporal-{0}.jsonl" -f $PreapprovalId)
($fila | ConvertTo-Json -Compress) | Add-Content -Path $rutaLinea -Encoding utf8

Write-Host ""
Write-Host "Snapshot      : $rutaSnapshot" -ForegroundColor DarkGray
Write-Host "Linea temporal: $rutaLinea" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Para ver la secuencia completa:" -ForegroundColor DarkGray
Write-Host "  Get-Content '$rutaLinea' | ForEach-Object { `$_ | ConvertFrom-Json } | Format-Table hora_utc, etiqueta, status, version, card_id, payment_method_id, cuotas, pagos" -ForegroundColor DarkGray
