$ErrorActionPreference = "Stop"

$PluginRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectPath = Join-Path $PluginRoot "FanControl.PCTelemetryDashboard.csproj"
$OutputDirectory = Join-Path $PluginRoot "bin\Release\net10.0-windows"
$PluginDll = Join-Path $OutputDirectory "FanControl.PCTelemetryDashboard.dll"
$PackagePath = Join-Path $PluginRoot "bin\Release\FanControl.PCTelemetryDashboard.zip"

dotnet build $ProjectPath --configuration Release
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $PluginDll)) {
    throw "The FanControl plugin build failed."
}

Compress-Archive -LiteralPath $PluginDll -DestinationPath $PackagePath -Force
Write-Host "Plugin package created:" -ForegroundColor Green
Write-Host $PackagePath
