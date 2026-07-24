[CmdletBinding()]
param(
    [string]$FanControlDirectory = "C:\Program Files (x86)\FanControl"
)

$ErrorActionPreference = "Stop"

$PluginRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackagePath = Join-Path $PluginRoot "bin\Release\FanControl.PCTelemetryDashboard.zip"
$PluginsDirectory = Join-Path $FanControlDirectory "Plugins"
$InstallDirectory = Join-Path $PluginsDirectory "PCTelemetryDashboard"
$ExpectedInstallDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)
$ExpectedPluginsDirectory = [System.IO.Path]::GetFullPath($PluginsDirectory)

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Plugin package not found. Run .\build-plugin.ps1 first."
}

if (-not $ExpectedInstallDirectory.StartsWith(
        $ExpectedPluginsDirectory + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to modify a directory outside FanControl's Plugins directory."
}

if (Get-Process -Name "FanControl" -ErrorAction SilentlyContinue) {
    throw "Quit FanControl before updating the plugin, then run this script again."
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

if (Test-Path -LiteralPath $ExpectedInstallDirectory) {
    Remove-Item -LiteralPath $ExpectedInstallDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $ExpectedInstallDirectory -Force | Out-Null
Expand-Archive -LiteralPath $PackagePath -DestinationPath $ExpectedInstallDirectory -Force

Write-Host "PC Telemetry Dashboard plugin installed successfully." -ForegroundColor Green
Write-Host "Start FanControl to load the updated plugin."
