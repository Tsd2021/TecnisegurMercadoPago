<#
.SINOPSIS
    Ejecuta un script SQL de SOLO LECTURA contra la base TSD y muestra los
    resultados. Pensado para cruzar lo que MercadoPago dice que entrego contra
    lo que quedo en nuestra base.

.DESCRIPCION
    SOLO LECTURA, y no por convencion sino por verificacion: antes de abrir la
    conexion revisa el texto del script y se niega a ejecutarlo si aparece
    cualquier verbo que escriba (INSERT, UPDATE, DELETE, MERGE, DROP, ALTER,
    TRUNCATE, CREATE, EXEC, GRANT, BACKUP, RESTORE). Por eso se puede correr
    contra PRODUCCION sin riesgo, y por eso es razonable habilitarlo en
    .claude\settings.local.json por ruta exacta.

    La guarda mira el archivo entero, comentarios incluidos. Un comentario que
    mencione alguno de esos verbos frena la corrida. Es a proposito: preferimos
    un falso positivo ruidoso antes que dejar pasar una escritura.

    El contexto de por que existe esto esta en ANALISIS-COBROS.md 2 ter y en la
    cabecera de Database\14_CruceNotificacionesMp.sql. En resumen: el MCP de
    MercadoPago informa 25 notificaciones entregadas en el ultimo mes, todas con
    HTTP 200. Si nuestras filas coinciden, la notificacion faltante de
    version 1 no se perdio en el camino: MercadoPago nunca la genero.

.PARAMETER Sql
    Ruta del script a ejecutar. Por defecto
    Database\14_CruceNotificacionesMp.sql.

.PARAMETER CadenaConexion
    Si no se pasa, se lee de user-secrets del proyecto, igual que
    ForensePreapproval.ps1 resuelve el access token. Nunca se imprime.

.PARAMETER EsperadoMp
    Cuantas notificaciones informa `notifications_history` del MCP para el mismo
    periodo. Se usa solo para imprimir el veredicto del cruce.

.EJEMPLO
    .\CruceNotificaciones.ps1
    .\CruceNotificaciones.ps1 -Sql ..\Database\12_ForenseUnaSuscripcion.sql
    .\CruceNotificaciones.ps1 -EsperadoMp 25
#>

[CmdletBinding()]
param(
    [string] $Sql,
    [string] $CadenaConexion,
    [int]    $EsperadoMp = 25
)

$ErrorActionPreference = 'Stop'

$raiz = Split-Path -Parent $PSScriptRoot
if (-not $Sql) { $Sql = Join-Path $raiz 'Database\14_CruceNotificacionesMp.sql' }
if (-not (Test-Path $Sql)) { throw "No existe el script '$Sql'." }

# ---------------------------------------------------------------------------
# 1) Guarda de solo lectura
# ---------------------------------------------------------------------------
# -Encoding UTF8 no es opcional. Windows PowerShell 5.1 lee en ANSI por defecto,
# y los .sql de este repo son UTF-8: sin esto, 'Pago único' llega a SQL Server
# como 'Pago Ãºnico' y queda guardado así dentro de la vista. Paso el 12/08/2026
# con vw_CobranzasMercadoPago: el literal corrupto dejó de coincidir con el
# filtro "Pago único" del módulo de TSD y la grilla salía vacía sin ningún error.
$texto = Get-Content $Sql -Raw -Encoding UTF8

$escrituras = 'INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|TRUNCATE|CREATE|EXEC|EXECUTE|GRANT|REVOKE|BACKUP|RESTORE'
$halladas = [regex]::Matches($texto, "(?im)\b($escrituras)\b") |
            ForEach-Object { $_.Value.ToUpper() } |
            Sort-Object -Unique

if ($halladas) {
    throw ("El script contiene verbos de escritura ({0}) y esta herramienta " -f ($halladas -join ', ')) +
          "solo ejecuta lectura. Correlo en SSMS si realmente tiene que escribir."
}

# ---------------------------------------------------------------------------
# 2) Cadena de conexion  (misma resolucion que ForensePreapproval.ps1)
# ---------------------------------------------------------------------------
if (-not $CadenaConexion) {
    Write-Host "Leyendo la cadena de conexion de user-secrets..." -ForegroundColor DarkGray

    $proyecto = Join-Path $raiz 'src\TecnisegurMercadoPago.Api'
    $secretos = dotnet user-secrets list --project $proyecto
    $linea    = $secretos | Where-Object { $_ -like 'ConnectionStrings:TSD*' }

    if (-not $linea) {
        throw "No se encontro 'ConnectionStrings:TSD' en user-secrets. Pasala con -CadenaConexion."
    }
    $CadenaConexion = ($linea -split '=', 2)[1].Trim()
}

# La cadena NUNCA se imprime; solo el servidor y la base, que es lo unico que
# hace falta para saber contra que se esta hablando.
$servidor = if ($CadenaConexion -match '(?i)(?:Data Source|Server)=([^;]+)') { $Matches[1] } else { '(?)' }
$base     = if ($CadenaConexion -match '(?i)(?:Initial Catalog|Database)=([^;]+)') { $Matches[1] } else { '(?)' }

Write-Host ""
Write-Host "Servidor : $servidor"
Write-Host "Base     : $base"
Write-Host "Script   : $(Split-Path -Leaf $Sql)"
Write-Host ""

# ---------------------------------------------------------------------------
# 3) Ejecucion, lote por lote
# ---------------------------------------------------------------------------
$lotes = [regex]::Split($texto, '(?im)^\s*GO\s*$') |
         Where-Object { $_.Trim() }

$cn = New-Object System.Data.SqlClient.SqlConnection $CadenaConexion
$cn.Open()

$primerConteo = $null
$numero = 0

try {
    foreach ($lote in $lotes) {
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = $lote
        $cmd.CommandTimeout = 120

        $ds = New-Object System.Data.DataSet
        $da = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
        [void]$da.Fill($ds)

        foreach ($tabla in $ds.Tables) {
            $numero++
            Write-Host ("--- Resultado {0}  ({1} fila(s)) ---" -f $numero, $tabla.Rows.Count) -ForegroundColor Cyan
            $tabla | Format-Table -AutoSize | Out-String -Width 220 | Write-Host

            if ($null -eq $primerConteo -and $tabla.Columns.Contains('TotalRecibidas') -and $tabla.Rows.Count -gt 0) {
                $primerConteo = [int] $tabla.Rows[0]['TotalRecibidas']
            }
        }
    }
}
finally {
    $cn.Close()
}

# ---------------------------------------------------------------------------
# 4) El veredicto del cruce
# ---------------------------------------------------------------------------
if ($null -ne $primerConteo) {
    Write-Host ""
    Write-Host ("MercadoPago informa : {0}" -f $EsperadoMp)
    Write-Host ("Nuestra base tiene  : {0}" -f $primerConteo)

    if ($primerConteo -eq $EsperadoMp) {
        Write-Host "CIERRAN. Nada se perdio en el camino: los huecos de version son de MercadoPago." -ForegroundColor Green
    }
    elseif ($primerConteo -lt $EsperadoMp) {
        Write-Host ("FALTAN {0} de nuestro lado. Hay entregas que MP dio por exitosas y no persistimos." -f ($EsperadoMp - $primerConteo)) -ForegroundColor Yellow
    }
    else {
        Write-Host ("TENEMOS {0} de mas. El historial de MP no cubre todo el periodo consultado." -f ($primerConteo - $EsperadoMp)) -ForegroundColor Yellow
    }
    Write-Host ""
}
