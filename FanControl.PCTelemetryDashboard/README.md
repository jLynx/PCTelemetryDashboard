# PC Telemetry Dashboard FanControl plugin

Experimental FanControl V272 plugin version of the PC telemetry dashboard. It
runs inside FanControl and provides:

- the ESP32-S3 USB HID telemetry connection and automatic reconnection;
- the dashboard at `http://localhost:5127`;
- one-second live sensor sampling through FanControl IPC;
- live NVIDIA GPU load and power through NVIDIA NVML when FanControl does not
  expose those sensor types;
- up to six hours of focused in-memory graph history;
- optional CSV logging, paused by default;
- clean `Initialize -> Load -> Close` lifecycle handling.

The standalone dashboard remains unchanged in the repository as a fallback.

## Build

This project targets the installed FanControl V272 .NET 10 runtime and defaults
to finding FanControl under `C:\Program Files (x86)\FanControl`.

```powershell
cd .\FanControl.PCTelemetryDashboard
.\build-plugin.ps1
```

The installable package is created at:

```text
bin\Release\FanControl.PCTelemetryDashboard.zip
```

If FanControl is elsewhere, build with:

```powershell
dotnet build -c Release -p:FanControlDirectory="D:\Path\To\FanControl"
```

## Test in FanControl

1. Quit the standalone PC Telemetry Dashboard first. Only one process can own
   port 5127 and the USB HID display at a time.
2. For the first installation, use FanControl's **Install plugin** and select
   `bin\Release\FanControl.PCTelemetryDashboard.zip`. For an update, quit
   FanControl and run `install-plugin.ps1`; FanControl's installer deliberately
   refuses to overwrite an existing plugin DLL.
3. Restart FanControl, or use its sensor/plugin refresh action.
4. Open `http://localhost:5127` directly or save it as a browser bookmark.
   FanControl's loaded-plugin entries only select the plugin details panel; its
   plugin API does not expose a custom click action.
5. Connect the ESP32-S3 display and confirm it changes from OFFLINE to LIVE.

Plugin messages are prefixed with `[PC Telemetry Dashboard]` in FanControl's
`log.txt`. A dedicated diagnostic log, including complete exception details, is
also written to:

```text
%TEMP%\PCTelemetryDashboard\fancontrol-plugin.log
```

It rolls over to `fancontrol-plugin.previous.log` at 2 MB. CSV files are stored
under:

```text
%LOCALAPPDATA%\PCTelemetryDashboard\plugin-logs
```

If the web page does not open, confirm the standalone app is closed and inspect
FanControl's log for a port-binding message. The plugin retries port 5127 every
five seconds after a conflict is removed.

## Remove

Quit FanControl and run `remove-plugin.ps1`. You can then return to the
standalone app without changing the ESP32 firmware or USB protocol.

## Design notes

FanControl calls `IPlugin2.Update()` on its own update path. This plugin leaves
that hook non-blocking: sensor IPC, USB writes and HTTP requests all run on
background workers. `Close()` cancels them and releases the USB/HTTP resources
so FanControl plugin refreshes do not accumulate duplicate workers.
