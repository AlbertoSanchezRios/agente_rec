[CmdletBinding()]
param(
    [switch]$Uninstall
)

# ==============================================================================
# Instala (o quita) el certificado raiz de Screen Recorder en este equipo para
# que Windows confie en la firma de codigo del producto (UAC / SmartScreen
# internos). Requiere permisos de Administrador.
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File install-cert.ps1
#   powershell -ExecutionPolicy Bypass -File install-cert.ps1 -Uninstall
#
# Los .cer son PUBLICOS: pueden distribuirse junto al MSI o por Intune/SCCM.
# ==============================================================================

$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "[ERROR] Se requieren permisos de Administrador." -ForegroundColor Red
    exit 1
}

# Localizar los .cer: junto a este script o en ..\certs
function Resolve-Cer([string]$name) {
    $base = Split-Path -Parent $MyInvocation.MyCommand.Path
    $candidates = @(
        (Join-Path $PSScriptRoot $name),
        (Join-Path (Split-Path -Parent $PSScriptRoot) "certs\$name")
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    return $null
}

$rootCer = Resolve-Cer 'ScreenRecorder-RootCA.cer'
$leafCer = Resolve-Cer 'ScreenRecorder-CodeSigning.cer'

if (-not $rootCer) {
    Write-Host "[ERROR] No se encontro 'ScreenRecorder-RootCA.cer' junto al script ni en .\certs." -ForegroundColor Red
    exit 1
}

function Get-Thumbprint([string]$cer) {
    return (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)).Thumbprint
}

if ($Uninstall) {
    Write-Host "Quitando el certificado raiz de Screen Recorder..." -ForegroundColor Yellow
    $tp = Get-Thumbprint $rootCer
    & certutil.exe -delstore Root $tp | Out-Null
    if ($leafCer) { & certutil.exe -delstore TrustedPublisher (Get-Thumbprint $leafCer) | Out-Null }
    Write-Host "[OK] Certificado raiz eliminado (si existia)." -ForegroundColor Green
    exit 0
}

Write-Host "Instalando el certificado raiz de Screen Recorder..." -ForegroundColor Cyan
& certutil.exe -addstore -f Root $rootCer | Out-Null
Write-Host "  - Root -> Entidades de certificacion raiz de confianza" -ForegroundColor Green

if ($leafCer) {
    & certutil.exe -addstore -f TrustedPublisher $leafCer | Out-Null
    Write-Host "  - Hoja -> Editores de confianza" -ForegroundColor Green
}

Write-Host "[OK] Certificado instalado. UAC ya no mostrara 'Editor desconocido' en este equipo." -ForegroundColor Green
Write-Host "NOTA: es confianza INTERNA (no cubre el SmartScreen de descargas de Internet)." -ForegroundColor DarkGray
