$ErrorActionPreference = "Stop"

$TaskName = "PC Telemetry Dashboard"
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $elevatedArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    Start-Process -FilePath powershell.exe `
        -Verb RunAs `
        -WorkingDirectory $ProjectRoot `
        -ArgumentList $elevatedArguments
    exit
}

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $task) {
    Write-Host "The PC Telemetry Dashboard startup task is not installed."
    exit
}

Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Write-Host "PC Telemetry Dashboard startup has been removed." -ForegroundColor Green
