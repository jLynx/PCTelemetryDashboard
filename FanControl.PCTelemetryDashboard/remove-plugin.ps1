[CmdletBinding()]
param(
    [string]$FanControlDirectory = "C:\Program Files (x86)\FanControl"
)

$ErrorActionPreference = "Stop"

$PluginsDirectory = Join-Path $FanControlDirectory "Plugins"
$InstallDirectory = Join-Path $PluginsDirectory "PCTelemetryDashboard"
$ExpectedInstallDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)
$ExpectedPluginsDirectory = [System.IO.Path]::GetFullPath($PluginsDirectory)

if (-not $ExpectedInstallDirectory.StartsWith(
        $ExpectedPluginsDirectory + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to modify a directory outside FanControl's Plugins directory."
}

if (Get-Process -Name "FanControl" -ErrorAction SilentlyContinue) {
    throw "Quit FanControl before removing the plugin, then run this script again."
}

$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @(
        "-NoProfile"
        "-ExecutionPolicy", "Bypass"
        "-File", ('"' + $PSCommandPath + '"')
        "-FanControlDirectory", ('"' + $FanControlDirectory + '"')
    )
    $elevated = Start-Process -FilePath "powershell.exe" `
        -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $elevated.ExitCode
}

if (-not (Test-Path -LiteralPath $ExpectedInstallDirectory)) {
    Write-Host "PC Telemetry Dashboard is not installed."
    exit 0
}

Remove-Item -LiteralPath $ExpectedInstallDirectory -Recurse -Force
Write-Host "PC Telemetry Dashboard plugin removed successfully." -ForegroundColor Green
