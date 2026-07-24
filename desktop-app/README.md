# Standalone PC Telemetry Dashboard

Local Windows dashboard for logging PC temperatures, fan control percent, fan RPM, load, power, and other numeric hardware sensors.

<img width="1516" height="1016" alt="image" src="https://github.com/user-attachments/assets/cb2c2175-7829-4e63-ba94-98d0ae353f87" />


## Run

Open PowerShell in this folder and run:

```powershell
.\run-dashboard.ps1
```

Then open:

```text
http://localhost:5127
```

## Install as a startup tray app

Close any currently running copy of the dashboard, then run this once from
PowerShell:

```powershell
.\install-startup.ps1
```

Windows shows one administrator approval prompt while the installer publishes
the app and creates an elevated scheduled task. After that, the dashboard starts
automatically at every sign-in without another UAC prompt. It runs without a
console window and shows a system tray icon with two actions:

- **Open dashboard** opens `http://localhost:5127` in the default browser.
- **Quit** shuts down the dashboard until the next sign-in or manual task start.

Running through Task Scheduler with highest privileges preserves access to the
motherboard and elevated FanControl sensor sources. The installed files are
stored under `%LOCALAPPDATA%\PCTelemetryDashboard\app`.

Run the installer again whenever you want to publish a newer dashboard build to
the startup installation.

To disable automatic startup and stop the installed instance:

```powershell
.\remove-startup.ps1
```

## USB telemetry display

When the ESP32-S3 firmware in `..\esp32-telemetry-display` is connected, the
dashboard automatically discovers its `PC Telemetry Display` USB HID interface
and sends CPU/GPU temperature, load, power, and case-fan output percentages once
per second. The display groups the matching radiator fans into one value and
also shows IO, PCIe, and exhaust fan outputs. No COM port or USB driver is
required. The worker automatically reconnects after unplugging and reconnecting
the display.

USB connection state is available at:

```text
http://localhost:5127/api/display/status
```

For the best sensor coverage, run PowerShell as Administrator. The dashboard can read FanControl's live sensor IPC when it runs at the same elevation as FanControl. If FanControl is elevated and this dashboard is not, Windows denies the sensor pipe and only the local fallback sensors will show.

The included `run-dashboard.ps1` script restarts itself as Administrator so motherboard sensors and FanControl's own sensor names/values are available.

FanControl V271 may report `ReadSensorValues` as unimplemented over IPC. The dashboard handles that by using the values returned from `GetAllSensors`.

## Sharing logs

The dashboard writes CSV logs to:

```text
logs\
```

CSV logging is limited to the focused dashboard sensors, including temperatures, load %, power W, and the main case/radiator fan outputs. Optional fan cards such as CPU Pump and GPU fans are hidden by default and are not written to the CSV log.

Optional temperature cards such as CCD temperatures and GPU VR SoC are also hidden by default and skipped by new CSV log rows. Use **Show optional temperatures** to inspect them in the dashboard when needed.

Use **New log** to rotate to a fresh timestamped CSV and clear the chart history. Use **Reset log** to clear the active CSV and start the current log again.

CSV logging starts paused whenever the application launches. Use **Resume CSV**
when you want to write samples to disk; the next focused sample is written
immediately. Use **Pause CSV** to stop writing while keeping live telemetry,
charts, and the USB display running.

Use the previous-log selector and **Open read-only** to inspect an existing CSV without changing it. While a previous log is open, live log controls are disabled in the dashboard view. Use **Live mode** to return to current telemetry.

Use the dashboard's **Download CSV** button to export all available telemetry logs into one file.
