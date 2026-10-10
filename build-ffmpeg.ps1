[CmdletBinding()]
param(
    [string]$Msys2Root = 'C:\msys64',
    [string]$FfmpegVersion = 'n7.1',
    [switch]$InstallDeps
)

# Compila la build mínima de FFmpeg con MSYS2 y coloca el binario en el proyecto como
# WmvCoreHost.exe (captura) y WCodecHost.exe (conversión). Si no hay MSYS2, no hace nada:
# build-msi.ps1 usará el "essentials" oficial como respaldo.
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File build-ffmpeg.ps1 [-InstallDeps]
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$bash = Join-Path $Msys2Root 'usr\bin\bash.exe'
if (-not (Test-Path $bash)) {
    Write-Output "MSYS2 no encontrado en $Msys2Root. Se omitirá la build a medida (se usará essentials)."
    exit 0
}

# Ruta del repo en formato MSYS2 (/d/dev/agente_rec)
$drive = $root.Substring(0, 1).ToLower()
$msysRoot = "/$drive" + ($root.Substring(2) -replace '\\', '/')
$msysOut = "$msysRoot/tools/ffmpeg-build"

if ($InstallDeps) {
    Write-Output "=== Instalando dependencias de MSYS2 MINGW64 ==="
    $env:MSYSTEM = 'MINGW64'
    & $bash -lc "pacman -S --needed --noconfirm mingw-w64-x86_64-toolchain mingw-w64-x86_64-nasm mingw-w64-x86_64-yasm mingw-w64-x86_64-pkgconf mingw-w64-x86_64-libx264 mingw-w64-x86_64-onevpl mingw-w64-x86_64-nv-codec-headers mingw-w64-x86_64-amf-headers"
    if ($LASTEXITCODE -ne 0) { Write-Output "ERROR instalando dependencias"; exit 1 }
}

Write-Output "=== Compilando FFmpeg a medida ($FfmpegVersion) ==="
$env:MSYSTEM = 'MINGW64'
& $bash -lc "cd '$msysRoot' && OUT='$msysOut' FFMPEG_VERSION='$FfmpegVersion' bash tools/build-ffmpeg.sh"
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR compilando FFmpeg"; exit 1 }

$built = Join-Path $root 'tools\ffmpeg-build\ffmpeg.exe'
if (-not (Test-Path $built)) { Write-Output "ERROR: no se generó $built"; exit 1 }

$assetsDir = Join-Path $root 'src\ScreenRecorder.UI'
Copy-Item $built (Join-Path $assetsDir 'WmvCoreHost.exe') -Force
Copy-Item $built (Join-Path $assetsDir 'WCodecHost.exe') -Force

Write-Output ""
Write-Output "OK: FFmpeg a medida instalado en el proyecto:"
Get-Item (Join-Path $assetsDir 'WmvCoreHost.exe'), (Join-Path $assetsDir 'WCodecHost.exe') |
    ForEach-Object { "  $($_.Name)  $([math]::Round($_.Length / 1MB, 1)) MB" }
