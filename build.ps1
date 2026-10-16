# Builds eGPU Tray and its installer with the C# compiler that ships with Windows (.NET Framework 4.x).
#   build\eGPUTray.exe         the tray app
#   build\Uninstall.exe        the uninstaller (installed next to the app)
#   dist\eGPU-Tray-Setup.exe   the installer (contains both)
$ErrorActionPreference = 'Stop'
$src   = Join-Path $PSScriptRoot 'src'
$build = Join-Path $PSScriptRoot 'build'
$dist  = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force $build, $dist | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }

# App icon (white chip on a dark tile)
Add-Type -Path (Join-Path $src 'ChipIcon.cs') -ReferencedAssemblies System.Drawing
$ico = Join-Path $build 'app.ico'
[ChipIcon]::SaveIco($ico, [int[]](16, 20, 24, 32, 40, 48, 64, 256),
    [Func[int, System.Drawing.Bitmap]] { param($s) [ChipIcon]::RenderTile($s) })

$common = @('/nologo', '/optimize+', '/platform:x64', '/target:winexe', '/codepage:65001',
    "/win32manifest:$src\app.manifest", "/win32icon:$ico",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')

& $csc @common "/out:$build\eGPUTray.exe" "$src\Monitor.cs" "$src\Gpu.cs" "$src\Shared.cs" "$src\ChipIcon.cs"
if ($LASTEXITCODE) { throw 'App build failed' }

& $csc @common "/out:$build\Uninstall.exe" "$src\Uninstall.cs" "$src\Shared.cs" "$src\ChipIcon.cs"
if ($LASTEXITCODE) { throw 'Uninstaller build failed' }

& $csc @common "/out:$dist\eGPU-Tray-Setup.exe" `
    "/resource:$build\eGPUTray.exe,eGPUTray.exe" "/resource:$build\Uninstall.exe,Uninstall.exe" `
    "$src\Setup.cs" "$src\Shared.cs" "$src\ChipIcon.cs"
if ($LASTEXITCODE) { throw 'Setup build failed' }

Get-Item "$build\eGPUTray.exe", "$build\Uninstall.exe", "$dist\eGPU-Tray-Setup.exe" | Select-Object Name, Length
