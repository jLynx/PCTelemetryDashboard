# PC Telemetry Dashboard

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

Use **Pause CSV** to stop writing new samples to disk while keeping live telemetry and charts running. Use **Resume CSV** to start writing again; the next focused sample is written immediately.

Use the previous-log selector and **Open read-only** to inspect an existing CSV without changing it. While a previous log is open, live log controls are disabled in the dashboard view. Use **Live mode** to return to current telemetry.

Use the dashboard's **Download CSV** button to export all available telemetry logs into one file.
