# eGPU Tray

A small Windows tray app that shows whether your external GPU is connected, using a flat chip icon that changes with the connection state. No pop-ups, no sounds.

| Icon | Meaning |
|---|---|
| Solid chip | eGPU connected and working |
| Outlined chip, blinking | Detected and starting, or disconnecting |
| Chip with a slash | Not connected, or safe to unplug |
| Solid chip with "!" | Windows reports an error |

Click the icon for GPU usage, VRAM, temperature, power, fan and PCIe link, the apps using the eGPU, and a **Disconnect eGPU** button that works like "Safely Remove Hardware". Right-click for the same information plus *Start with Windows*, *Run when connected…* and *Exit*.

## Run a program when the eGPU connects

*Run when connected…* runs a program each time the eGPU is plugged in and has been ready for 3 seconds (and once at start-up if it is already connected). Use it to re-apply an undervolt or power limit, since the card resets to stock whenever it reconnects. A program that exits with an error (for example `schtasks` in the first moments after the eGPU connects) is retried for about 3 minutes, and once more the next time Windows is unlocked. The result, including the exit code and message on failure, is written to the log.

MSI Afterburner needs admin rights, so starting it directly brings up a UAC prompt every time. Instead, create an elevated scheduled task once, from an admin PowerShell:

```powershell
$a = New-ScheduledTaskAction -Execute "${env:ProgramFiles(x86)}\MSI Afterburner\MSIAfterburner.exe" -Argument "-Profile1"
$s = New-ScheduledTaskSettingsSet -MultipleInstances Parallel -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0
Register-ScheduledTask -TaskName "Afterburner Profile 1" -Action $a -Settings $s -RunLevel Highest
```

The settings matter: Afterburner keeps running as the task, so by default Windows would ignore the next run while it's still open, wouldn't start it on battery, would stop it when the laptop unplugs, and would end it after 72 hours.

Then set *Program* to `schtasks.exe` and *Arguments* to `/run /tn "Afterburner Profile 1"`.

Works with any eGPU brand: it watches for a PCI graphics card that Windows marks as removable (Thunderbolt / USB4 / hot-plug PCIe) and reads usage, VRAM, temperature and link speed from Windows. On NVIDIA cards it also reads power draw from the NVIDIA driver.

## Install

Run `dist\eGPU-Tray-Setup.exe` (after building). It installs for your account only, with no admin rights needed, to `%LOCALAPPDATA%\Programs\eGPU Tray`, and adds a Start menu entry, an optional sign-in autostart and a Settings › Apps entry. `/S` installs silently.

Uninstall from Settings › Apps, or run `Uninstall.exe` in the install folder (`/S` for silent).

## Build

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Uses the C# compiler that ships with Windows (.NET Framework 4.x), so there's nothing to install. Output:

- `build\eGPUTray.exe` – the tray app
- `build\Uninstall.exe` – the uninstaller
- `dist\eGPU-Tray-Setup.exe` – the installer (contains both)

## Source

- `src\Monitor.cs` – entry point, tray icon, menu and status panel
- `src\Gpu.cs` – eGPU detection, readings and disconnect
- `src\OnConnect.cs` – the *Run when connected* setting and its window
- `src\ChipIcon.cs` – the chip icon drawing
- `src\Shared.cs` – install/uninstall logic shared by all three programs
- `src\Setup.cs`, `src\Uninstall.cs` – installer and uninstaller windows
