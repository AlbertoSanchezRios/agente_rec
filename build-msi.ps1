# Compila y empaqueta el instalador MSI de Screen Recorder (Fase 1).
#
# Requisitos: .NET 10 SDK (portable en %LOCALAPPDATA%\dotnet es suficiente, sin admin)
# y el CLI de WiX 4.0.6 (se instala solo si falta).
#
# Uso desde la raiz del repo:
#   powershell -ExecutionPolicy Bypass -File build-msi.ps1 [-ProductVersion 1.2.0]
#
# Nota: usa el CLI de WiX en lugar de 'dotnet build' del .wixproj porque en este
# entorno el SDK de WiX (4 y 5) descarta silenciosamente los <Fragment>; el wxs
# evita fragments y el CLI compila correctamente.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$version = '1.2.0'
if ($args -contains '-ProductVersion') { $version = $args[$args.IndexOf('-ProductVersion') + 1] }

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
    Write-Output "=== Instalando CLI de WiX 4.0.6 ==="
    & $dotnet tool install --global wix --version 4.0.6
}

# --- Extensiones de WiX (UI + Util): se restauran vía el .wixproj y se localizan en el cache nuget ---
Write-Output "=== Restaurando extensiones de WiX ==="
& $dotnet restore src/ScreenRecorder.Installer/ScreenRecorder.Installer.wixproj
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en restore de extensiones"; exit 1 }
$nugetCache = Join-Path $env:USERPROFILE '.nuget\packages'
$uiExt = Get-ChildItem $nugetCache -Recurse -Filter 'WixToolset.UI.wixext.dll' | Where-Object FullName -match '4\.0\.6' | Select-Object -First 1
$utilExt = Get-ChildItem $nugetCache -Recurse -Filter 'WixToolset.Util.wixext.dll' | Where-Object FullName -match '4\.0\.6' | Select-Object -First 1
if (-not $uiExt -or -not $utilExt) { Write-Output "ERROR: no se encontraron las extensiones WiX 4.0.6 en $nugetCache"; exit 1 }

# 1) Publicar la UI y el servicio watchdog self-contained (incluyen runtime; no dependen
#    de nada instalado en la PC de destino). Ambos publican a la misma carpeta: el harvest
#    empaqueta todos los archivos (los DLLs comunes se comparten por nombre).
Write-Output "=== Publish UI + Servicio watchdog (self-contained win-x64) ==="
# Limpiar publish/msi: evita residuos de publishes anteriores (p. ej. scripts ya no distribuidos)
# colándose en el harvest y en el MSI.
if (Test-Path publish/msi) { Remove-Item publish/msi -Recurse -Force }
& $dotnet publish src/ScreenRecorder.UI/ScreenRecorder.UI.csproj -c Release -r win-x64 --self-contained true -o publish/msi
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en publish de la UI"; exit 1 }
& $dotnet publish src/ScreenRecorder.Service/ScreenRecorder.Service.csproj -c Release -r win-x64 --self-contained true -o publish/msi
if ($LASTEXITCODE -ne 0) { Write-Output "ERROR en publish del servicio"; exit 1 }

# 2) Recursos de operacion junto al binario
#    NOTA: 'Detener ScreenRecorder.bat' ya NO se distribuye: la detención voluntaria ('echate')
#    queda reservada a TI (el pipe de control solo acepta clientes administrador/SYSTEM).
foreach ($f in @('Instalar Autoarranque.bat','Verificar Autoarranque.bat','install-autostart.ps1','verify-autostart.ps1')) {
    if (Test-Path $f) { Copy-Item $f publish/msi -Force }
}
Copy-Item src/ScreenRecorder.Installer/LEEME-PRUEBAS.txt publish/msi -Force

# 2b) Harvest: generar GeneratedComponents.wxi con un componente por archivo de publish/msi.
#     Va INCLUIDO dentro del ComponentGroup del wxs (WiX 4 no tiene <Files> y los <Fragment>
#     se descartan en este entorno).
Write-Output "=== Harvest de publish/msi -> GeneratedComponents.wxi ==="
$exclude = @('WmvCoreHost.exe','WSearchHost.exe','WSearchHostSvc.exe','createdump.exe','Instalar Autoarranque.bat','Verificar Autoarranque.bat','install-autostart.ps1','verify-autostart.ps1','LEEME-PRUEBAS.txt')
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

Write-Output ""
Write-Output "OK: $root\dist\ScreenRecorder-$version.msi"
Get-ChildItem dist\*.msi | ForEach-Object { "  $($_.Name)  $([math]::Round($_.Length/1MB,1)) MB" }
