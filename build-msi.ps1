[CmdletBinding()]
param(
    [string]$ProductVersion = '0.1.2',
    # Firma de código opcional (Authenticode). Si se indica -PfxPath se firman los
    # ejecutables y el MSI; si se omite, el build se genera SIN firmar.
    [string]$PfxPath = '',
    [string]$PfxPassword = '',
    # URL de sello de tiempo RFC3161. Vacio = no sellar (recomendado para
    # certificados privados: los TSA publicos suelen rechazar cadenas no publicas).
    [string]$TimestampUrl = ''
)

# Compila y empaqueta el instalador MSI de Screen Recorder (Fase 1).
#
# Requisitos: .NET 10 SDK (portable en %LOCALAPPDATA%\dotnet es suficiente, sin admin)
# y el CLI de WiX 4.0.6 (se instala solo si falta).
#
# Uso desde la raiz del repo:
#   powershell -ExecutionPolicy Bypass -File build-msi.ps1 [-ProductVersion 0.1.2]
#
# Nota: usa el CLI de WiX en lugar de 'dotnet build' del .wixproj porque en este
# entorno el SDK de WiX (4 y 5) descarta silenciosamente los <Fragment>; el wxs
# evita fragments y el CLI compila correctamente.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$version = $ProductVersion

# --- Firma de código (opcional) -----------------------------------------------
# Sin -PfxPath el build continúa sin firmar (uso local). Con -PfxPath se firma
# cada ejecutable (antes de construir el MSI, para que el MSI incluya sus hashes)
# y el propio MSI al final. El certificado puede provenir de una CA interna (AD CS).
function Get-SignTool {
    $sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if ($sdk) { return $sdk.FullName }
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$doSign = -not [string]::IsNullOrWhiteSpace($PfxPath)
$script:SignTool = $null
if ($doSign) {
    if (-not (Test-Path $PfxPath)) { Write-Output "ERROR: no existe el certificado PFX '$PfxPath'"; exit 1 }
    $script:SignTool = Get-SignTool
    if (-not $script:SignTool) { Write-Output "ERROR: no se encontró signtool.exe (instala el Windows SDK)"; exit 1 }
    Write-Output "Firma de código ACTIVADA. signtool: $script:SignTool"
} else {
    Write-Output "Firma de código desactivada (sin -PfxPath): se generará un MSI sin firmar."
}

function Invoke-SignFile([string]$File) {
    if (-not $doSign) { return }
    if (-not (Test-Path $File)) { return }
    $signArgs = @('sign', '/fd', 'sha256', '/f', $PfxPath)
    if (-not [string]::IsNullOrEmpty($PfxPassword)) { $signArgs += @('/p', $PfxPassword) }
    if (-not [string]::IsNullOrEmpty($TimestampUrl)) { $signArgs += @('/tr', $TimestampUrl, '/td', 'sha256') }
    $signArgs += $File
    Write-Output "  Firmando: $(Split-Path -Leaf $File)"
    & $script:SignTool @signArgs
    if ($LASTEXITCODE -ne 0) { Write-Output "ERROR al firmar $File"; exit 1 }
}

# --- SDK .NET 10 (portable de usuario tiene prioridad, luego Program Files, luego PATH) ---
$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $pfDotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $pfDotnet) {
        $dotnet = $pfDotnet
    } else {
        $dotnet = 'dotnet'
    }
}
if (Test-Path $dotnet) {
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    if ($env:Path -notlike "*$($env:DOTNET_ROOT)*") {
        $env:Path = "$($env:DOTNET_ROOT);$($env:Path)"
    }
}
Write-Output "SDK: $dotnet"
& $dotnet --version

# --- CLI de WiX 4.0.6 ---
$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
if (-not (Test-Path $wix)) {
    $wixCmd = Get-Command wix -ErrorAction SilentlyContinue
    if ($wixCmd) {
        $wix = $wixCmd.Source
    } else {
        Write-Output "=== Instalando CLI de WiX 4.0.6 ==="
        & $dotnet tool install --global wix --version 4.0.6
    }
}

# --- Asegurar binarios de FFmpeg (WmvCoreHost.exe captura, WCodecHost.exe conversión) ---
$ffmpegTarget = Join-Path $root 'src\ScreenRecorder.UI\WmvCoreHost.exe'
$codecTarget = Join-Path $root 'src\ScreenRecorder.UI\WCodecHost.exe'
$customFfmpeg = Join-Path $root 'tools\ffmpeg-build\ffmpeg.exe'
$usingCustom = Test-Path $customFfmpeg
if ($usingCustom) {
    Write-Output "=== Usando FFmpeg a medida (tools\ffmpeg-build\ffmpeg.exe) ==="
    Copy-Item $customFfmpeg $ffmpegTarget -Force
    Copy-Item $customFfmpeg $codecTarget -Force
}
$needsDownload = (-not $usingCustom) -and ((-not (Test-Path $ffmpegTarget)) -or ((Get-Item $ffmpegTarget).Length -gt 150MB))
if ($needsDownload) {
    Write-Output "=== Asegurando FFmpeg ligero (versión essentials oficial ~95 MB) ==="
    $zipPath = Join-Path $env:TEMP 'ffmpeg_essentials.zip'
    $extractPath = Join-Path $env:TEMP 'ffmpeg_essentials_extracted'
    Invoke-WebRequest -Uri 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $zipPath
    Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force
    $ffmpegExe = Get-ChildItem -Path $extractPath -Filter 'ffmpeg.exe' -Recurse | Select-Object -First 1
    if ($ffmpegExe) {
        Copy-Item $ffmpegExe.FullName $ffmpegTarget -Force
        Write-Output "  FFmpeg essentials copiado con éxito a $ffmpegTarget ($([math]::Round((Get-Item $ffmpegTarget).Length / 1MB, 1)) MB)"
    } else {
        Write-Output "ERROR: no se pudo encontrar ffmpeg.exe en el paquete descargado"
        exit 1
    }
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Remove-Item $extractPath -Recurse -Force -ErrorAction SilentlyContinue
}

# --- FFmpeg de conversión (WCodecHost.exe): copia del mismo binario con identidad distinta,
#     para que el watchdog distinga captura (WmvCoreHost) de conversión (WCodecHost). ---
if (-not (Test-Path $codecTarget)) {
    Copy-Item $ffmpegTarget $codecTarget -Force
    Write-Output "  WCodecHost.exe creado como copia de WmvCoreHost.exe"
}

# --- Extensiones de WiX (UI + Util): se restauran vía el .wixproj y se localizan en el cache nuget ---
Write-Output "=== Restaurando extensiones de WiX ==="
& $dotnet restore src/ScreenRecorder.Installer/ScreenRecorder.Installer.wixproj
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en restore de extensiones"; exit 1 }
$nugetCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$uiExt = Get-ChildItem $nugetCache -Recurse -Filter 'WixToolset.UI.wixext.dll' | Where-Object FullName -match '4\.0\.6' | Select-Object -First 1
$utilExt = Get-ChildItem $nugetCache -Recurse -Filter 'WixToolset.Util.wixext.dll' | Where-Object FullName -match '4\.0\.6' | Select-Object -First 1
if (-not $uiExt -or -not $utilExt) { Write-Output "ERROR: no se encontraron las extensiones WiX 4.0.6 en $nugetCache"; exit 1 }

# 1) Publicar la UI y el servicio watchdog self-contained (incluyen runtime; no dependen
#    de nada instalado en la PC de destino). Ambos publican a la misma carpeta: el harvest
#    empaqueta todos los archivos (los DLLs comunes se comparten por nombre).
Write-Output "=== Publish UI + Agente + Servicio watchdog + Wctl (self-contained win-x64) ==="
# Limpiar publish/msi: evita residuos de publishes anteriores (p. ej. scripts ya no distribuidos)
# colándose en el harvest y en el MSI.
if (Test-Path publish/msi) { Remove-Item publish/msi -Recurse -Force }
& $dotnet publish src/ScreenRecorder.Agent/ScreenRecorder.Agent.csproj -c Release -r win-x64 --self-contained true -o publish/msi
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en publish del agente"; exit 1 }
& $dotnet publish src/ScreenRecorder.UI/ScreenRecorder.UI.csproj -c Release -r win-x64 --self-contained true -o publish/msi
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en publish de la UI"; exit 1 }
& $dotnet publish src/ScreenRecorder.Service/ScreenRecorder.Service.csproj -c Release -r win-x64 --self-contained true -o publish/msi
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en publish del servicio"; exit 1 }
# Wctl: se publica en la misma carpeta; comparte los DLLs de runtime ya presentes (aporta
# solo su exe/dll y sus archivos de configuración).
& $dotnet publish src/Wctl/Wctl.csproj -c Release -r win-x64 --self-contained true -o publish/msi
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en publish de Wctl"; exit 1 }

# 1b) Firma de los ejecutables ANTES de construir el MSI: el MSI captura los
#     hashes de los archivos, por lo que deben firmarse primero.
if ($doSign) {
    Write-Output "=== Firmando ejecutables (publish/msi) ==="
    foreach ($exe in @('WSearchAgent.exe', 'WSearchCfg.exe', 'WSearchHostSvc.exe', 'Wctl.exe', 'WmvCoreHost.exe', 'WCodecHost.exe')) {
        Invoke-SignFile (Join-Path $root "publish\msi\$exe")
    }
}

# 2) Recursos de operacion junto al binario
#    NOTA: 'Detener ScreenRecorder.bat' ya NO se distribuye: la detención voluntaria ('echate')
#    queda reservada a TI (el pipe de control solo acepta clientes administrador/SYSTEM).
foreach ($f in @('Instalar Autoarranque.bat','Verificar Autoarranque.bat','Desinstalar.bat','install-autostart.ps1','verify-autostart.ps1','uninstall.ps1','install-cert.ps1')) {
    if (Test-Path $f) { Copy-Item $f publish/msi -Force }
}
Copy-Item src/ScreenRecorder.Installer/LEEME-PRUEBAS.txt publish/msi -Force
# Certificados PUBLICOS (root + hoja): se empaquetan y se adjuntan al release.
if (Test-Path 'certs') { Get-ChildItem certs -Filter *.cer | Copy-Item -Destination publish/msi -Force }

# 2b) Harvest: generar GeneratedComponents.wxi con un componente por archivo de publish/msi.
#     Va INCLUIDO dentro del ComponentGroup del wxs (WiX 4 no tiene <Files> y los <Fragment>
#     se descartan en este entorno).
Write-Output "=== Harvest de publish/msi -> GeneratedComponents.wxi ==="
$exclude = @('WmvCoreHost.exe','WCodecHost.exe','WSearchAgent.exe','WSearchCfg.exe','WSearchHostSvc.exe','Wctl.exe','createdump.exe','Instalar Autoarranque.bat','Verificar Autoarranque.bat','Desinstalar.bat','install-autostart.ps1','verify-autostart.ps1','uninstall.ps1','install-cert.ps1','LEEME-PRUEBAS.txt','ScreenRecorder-RootCA.cer','ScreenRecorder-CodeSigning.cer')
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('<Include xmlns="http://wixtoolset.org/schemas/v4/wxs">')
Get-ChildItem publish/msi -File | Where-Object { $exclude -notcontains $_.Name } | Sort-Object Name | ForEach-Object {
    $fileId = 'Gen_' + (($_.Name -replace '[^A-Za-z0-9_]','_') -replace '^_+','')
    [void]$sb.AppendLine("  <Component Id=`"C_$fileId`" Guid=`"*`">")
    [void]$sb.AppendLine("    <File Id=`"$fileId`" Source=`"`$(var.PublishDir)$($_.Name)`" KeyPath=`"yes`" />")
    [void]$sb.AppendLine('  </Component>')
}
[void]$sb.AppendLine('</Include>')
[IO.File]::WriteAllText("$root/src/ScreenRecorder.Installer/GeneratedComponents.wxi", $sb.ToString())
Write-Output "  $($sb.ToString().Split([char]10).Count / 3) archivos harvesteados"

# 3) Compilar el MSI con el CLI de WiX
Write-Output "=== Compilando MSI $version ==="
New-Item -ItemType Directory -Path dist -Force | Out-Null
& $wix build src/ScreenRecorder.Installer/Package.wxs `
    -arch x64 `
    -d ProductVersion=$version `
    -d "PublishDir=$root\publish\msi\" `
    -d "InstallerDir=$root\src\ScreenRecorder.Installer\" `
    -ext $uiExt.FullName -ext $utilExt.FullName `
    -out "$root\dist\ScreenRecorder-$version.msi"
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en build del MSI"; exit 1 }

# 4) Firma del propio instalador MSI
if ($doSign) {
    Write-Output "=== Firmando MSI ==="
    Invoke-SignFile (Join-Path $root "dist\ScreenRecorder-$version.msi")
}

# 5) Recursos para el release: certificado publico (root + hoja) y script de instalacion
if (Test-Path 'install-cert.ps1') { Copy-Item 'install-cert.ps1' dist -Force }
if (Test-Path 'certs') { Get-ChildItem certs -Filter *.cer | Copy-Item -Destination dist -Force }

Write-Output ""
Write-Output "OK: $root\dist\ScreenRecorder-$version.msi"
Get-ChildItem dist\*.msi | ForEach-Object { "  $($_.Name)  $([math]::Round($_.Length/1MB,1)) MB" }
