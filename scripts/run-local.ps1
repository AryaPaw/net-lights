param(
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Get-Process NetLights -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path artifacts/publish | Out-Null
dotnet publish src/NetLights.App/NetLights.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o artifacts/publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish src/NetLights.UpdateAgent/NetLights.UpdateAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o artifacts/publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $NoStart) {
    Start-Process -FilePath (Join-Path $root "artifacts\publish\NetLights.exe")
}
