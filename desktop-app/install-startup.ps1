$ErrorActionPreference = "Stop"

$TaskName = "PC Telemetry Dashboard"
$DashboardUrl = "http://localhost:5127"
$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectPath = Join-Path $ProjectRoot "PCTelemetryDashboard.csproj"
$InstallRoot = Join-Path $env:LOCALAPPDATA "PCTelemetryDashboard"
$AppDirectory = Join-Path $InstallRoot "app"
$ExecutablePath = Join-Path $AppDirectory "PCTelemetryDashboard.exe"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Administrator approval is required once to install the elevated startup task."
    $elevatedArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    Start-Process -FilePath powershell.exe `
        -Verb RunAs `
        -WorkingDirectory $ProjectRoot `
        -ArgumentList $elevatedArguments
    exit
}

$existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existingTask) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $AppDirectory -Force | Out-Null

Write-Host "Publishing PC Telemetry Dashboard..."
dotnet publish $ProjectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    --output $AppDirectory `
    --nologo

if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "Publishing the dashboard failed."
}

$taskUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$arguments = "--urls $DashboardUrl --contentRoot `"$AppDirectory`""
$action = New-ScheduledTaskAction `
    -Execute $ExecutablePath `
    -Argument $arguments `
    -WorkingDirectory $AppDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $taskUser
$taskPrincipal = New-ScheduledTaskPrincipal `
    -UserId $taskUser `
    -LogonType Interactive `
    -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1)
$task = New-ScheduledTask `
    -Action $action `
    -Trigger $trigger `
    -Principal $taskPrincipal `
    -Settings $settings `
    -Description "Runs the PC Telemetry Dashboard in the system tray at sign-in."

Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null

$listener = Get-NetTCPConnection -LocalPort 5127 -State Listen -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($listener) {
    Write-Host "Startup is installed, but port 5127 is currently in use by PID $($listener.OwningProcess)." -ForegroundColor Yellow
    Write-Host "Quit the currently running dashboard; the installed app will start automatically at your next sign-in."
} else {
    Start-ScheduledTask -TaskName $TaskName
    $started = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 500
        $started = $null -ne (Get-NetTCPConnection -LocalPort 5127 -State Listen -ErrorAction SilentlyContinue |
            Select-Object -First 1)
        if ($started) {
            break
        }
    }

    if ($started) {
        Write-Host "PC Telemetry Dashboard is installed and running in the system tray." -ForegroundColor Green
    } else {
        $taskInfo = Get-ScheduledTaskInfo -TaskName $TaskName
        Write-Host "Startup was installed, but the dashboard did not begin listening on port 5127." -ForegroundColor Yellow
        Write-Host "Task Scheduler result: $($taskInfo.LastTaskResult)"
    }
}

Write-Host "Future sign-ins will start it elevated without another UAC prompt."
Write-Host "Dashboard: $DashboardUrl"
