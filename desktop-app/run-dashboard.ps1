$ErrorActionPreference = "Stop"

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Url = "http://localhost:5127"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Restarting as Administrator for motherboard/FanControl sensor access..."
    Start-Process -FilePath powershell.exe `
        -Verb RunAs `
        -WorkingDirectory $ProjectRoot `
        -ArgumentList @("-NoExit", "-ExecutionPolicy", "Bypass", "-File", $PSCommandPath)
    exit
}

$existingListener = Get-NetTCPConnection -LocalPort 5127 -State Listen -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($existingListener) {
    Write-Host "PC Telemetry Dashboard is already running on port 5127 (PID $($existingListener.OwningProcess))." -ForegroundColor Yellow
    Write-Host "Close the existing dashboard window or stop that process before starting the updated build."
    Read-Host "Press Enter to close this window"
    exit
}

Write-Host "Starting PC Telemetry Dashboard at $Url"
Start-Process $Url
dotnet run --project (Join-Path $ProjectRoot "PCTelemetryDashboard.csproj") -p:UseAppHost=false -- --urls $Url
