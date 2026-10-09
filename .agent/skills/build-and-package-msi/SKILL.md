---
name: build-and-package-msi
description: Compila y empaqueta el instalador MSI per-machine para Screen Recorder (.NET 10 self-contained y WiX 4.0.6), asegurando la generación de componentes y binarios limpios.
---

# Skill: Build and Package MSI (Screen Recorder)

Esta skill define el procedimiento estándar, exhaustivo e inviolable para compilar los proyectos C# (.NET 10), generar el catálogo de componentes cosechados (harvesting) y construir el paquete MSI de instalación empresarial con WiX Toolset 4.

---

## 1. Contexto y Arquitectura de Compilación

Screen Recorder se distribuye como un paquete MSI per-machine de 64 bits (`win-x64`) que no requiere ningún runtime previo en la máquina cliente:
- **`WSearchHost.exe`**: Agente interactivo de usuario (UI invisible WinForms en `publish/msi`).
- **`WSearchHostSvc.exe`**: Servicio supervisor watchdog en `LocalSystem` (en `publish/msi`).
- **`WmvCoreHost.exe`**: Binario FFmpeg con aceleración por hardware (QSV, NVENC, AMF).
- **WiX 4.0.6 CLI**: Utilizado directamente mediante `wix.exe build` debido a que el SDK de WiX en .NET tiende a descartar elementos `<Fragment>` en compilaciones multinivel.

---

## 2. Invariantes del Proceso de Empaquetado

1. **Self-Contained Estricto:** Ambos ejecutables (`UI` y `Service`) deben publicarse con `--self-contained true` y `-r win-x64` hacia la misma carpeta destino (`publish/msi`). Las bibliotecas comunes de .NET se comparten por nombre de archivo.
2. **Harvest en Archivo Único (`GeneratedComponents.wxi`):** Todos los DLLs y dependencias satélite se cosechan dinámicamente en `src/ScreenRecorder.Installer/GeneratedComponents.wxi` y se incluyen dentro del `ComponentGroup` principal en `Package.wxs`.
3. **Exclusiones Explícitas del Harvest:** Binarios principales con declaración explícita de servicio o atajos (`WSearchHost.exe`, `WSearchHostSvc.exe`, `WmvCoreHost.exe`, scripts `.bat` y `.ps1`) deben quedar excluidos de `GeneratedComponents.wxi` para evitar conflictos de identificador o GUID duplicados en la base de datos MSI.
4. **Limpieza Previa Obligatoria:** Siempre purgar `publish/msi` antes de un nuevo publish para evitar que restos de compilaciones anteriores o scripts deprecados contaminen el instalador final.

---

## 3. Procedimiento de Ejecución

### Opción A: Compilación Rápida por Script Interactivo (Administrador o Desarrollador)
Ejecutar desde la raíz del repositorio:
```cmd
"Compilar MSI.bat"
```
O directamente vía PowerShell:
```powershell
powershell -ExecutionPolicy Bypass -File .\build-msi.ps1 -ProductVersion 1.2.0
```

### Opción B: Flujo Paso a Paso Detallado

1. **Restaurar y Verificar SDK .NET 10:**
   ```powershell
   $dotnet = "C:\Program Files\dotnet\dotnet.exe"
   & $dotnet --version # Debe reportar 10.0.x
   ```

2. **Asegurar WiX CLI 4.0.6 y Extensiones:**
   ```powershell
   & $dotnet tool install --global wix --version 4.0.6
   & $dotnet restore src/ScreenRecorder.Installer/ScreenRecorder.Installer.wixproj
   ```

3. **Publicar UI y Servicio Watchdog:**
   ```powershell
   if (Test-Path publish/msi) { Remove-Item publish/msi -Recurse -Force }
   & $dotnet publish src/ScreenRecorder.UI/ScreenRecorder.UI.csproj -c Release -r win-x64 --self-contained true -o publish/msi
   & $dotnet publish src/ScreenRecorder.Service/ScreenRecorder.Service.csproj -c Release -r win-x64 --self-contained true -o publish/msi
   ```

4. **Copiar Scripts Operativos y Archivos de Diagnóstico:**
   ```powershell
   Copy-Item "Instalar Autoarranque.bat", "Verificar Autoarranque.bat", "install-autostart.ps1", "verify-autostart.ps1" publish/msi -Force
   Copy-Item "src/ScreenRecorder.Installer/LEEME-PRUEBAS.txt" publish/msi -Force
   ```

5. **Generar Cosecha (`GeneratedComponents.wxi`):**
   Ejecutar el bucle de cosecha excluyendo binarios fijos y emitiendo elementos `<Component>` con `Guid="*"`.

6. **Compilar el MSI con WiX CLI:**
   ```powershell
   $wix = "$env:USERPROFILE\.dotnet\tools\wix.exe"
   $nugetCache = "$env:USERPROFILE\.nuget\packages"
   $uiExt = (Get-ChildItem $nugetCache -Recurse -Filter 'WixToolset.UI.wixext.dll' | Where-Object FullName -match '4\.0\.6' | Select-Object -First 1).FullName
   $utilExt = (Get-ChildItem $nugetCache -Recurse -Filter 'WixToolset.Util.wixext.dll' | Where-Object FullName -match '4\.0\.6' | Select-Object -First 1).FullName

   & $wix build src/ScreenRecorder.Installer/Package.wxs `
       -arch x64 `
       -d ProductVersion="1.2.0" `
       -d "PublishDir=$PWD\publish\msi\" `
       -d "InstallerDir=$PWD\src\ScreenRecorder.Installer\" `
       -ext $uiExt -ext $utilExt `
       -out "$PWD\dist\ScreenRecorder-1.2.0.msi"
   ```

---

## 4. Verificación de Integridad del MSI

Al finalizar el empaquetado, validar:
1. El archivo `dist/ScreenRecorder-1.2.0.msi` existe y tiene un tamaño coherente (~230 MB incluyendo runtime y FFmpeg).
2. Verificación de tablas con msiexec o herramientas de inspección:
   - Tabla `ServiceInstall` contiene `WSearchHostSvc`.
   - Clave de autoarranque en `SOFTWARE\Microsoft\Windows\CurrentVersion\Run`.
   - CustomActions `CA_StopBeforeCopy`, `CA_KillLeftovers`, `CA_RegisterAutostart` configuradas.
