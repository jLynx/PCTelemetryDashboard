# PC Telemetry Dashboard

PC hardware telemetry dashboard with an ESP32-S3 USB display. The repository
contains three separate projects so each one builds without compiling files
from either of the others.

## Projects

| Folder | Purpose |
| --- | --- |
| [`desktop-app`](desktop-app) | Original standalone .NET dashboard, tray application, web server, sensor collection, and USB HID host. |
| [`fancontrol-plugin`](fancontrol-plugin) | FanControl plugin alternative that hosts the dashboard and USB connection inside FanControl. |
| [`esp32-telemetry-display`](esp32-telemetry-display) | PlatformIO firmware for the ESP32-S3 and 480x320 ST7796S display. |

The standalone app and FanControl plugin are alternatives. Do not run both at
the same time because only one process can listen on port `5127` and own the USB
HID display.

## Standalone app

From the repository root:

```powershell
cd .\desktop-app
.\run-dashboard.ps1
```

The dashboard opens at <http://localhost:5127>. To publish it as a tray app that
starts automatically at sign-in, run `desktop-app\install-startup.ps1` once.
See [`desktop-app/README.md`](desktop-app/README.md) for startup, logging, and
sensor details.

To build it directly:

```powershell
dotnet build .\desktop-app\PCTelemetryDashboard.csproj
```

## FanControl plugin

The plugin provides the same web dashboard and USB HID feed from inside
FanControl:

```powershell
cd .\fancontrol-plugin
.\build-plugin.ps1
```

Install `fancontrol-plugin\bin\Release\FanControl.PCTelemetryDashboard.zip`
through FanControl. Quit FanControl and use `install-plugin.ps1` for subsequent
updates because FanControl does not overwrite an installed plugin DLL. Clicking
the loaded **PC Telemetry Dashboard** plugin opens the dashboard in the default
browser. See [`fancontrol-plugin/README.md`](fancontrol-plugin/README.md) for the
full install and removal workflow.

## ESP32-S3 firmware

Open `esp32-telemetry-display` as a PlatformIO project, then build and upload the
firmware. It communicates through a dedicated USB HID interface; no COM port or
custom Windows driver is required. Wiring, display configuration, and USB
protocol details are in
[`esp32-telemetry-display/README.md`](esp32-telemetry-display/README.md).
