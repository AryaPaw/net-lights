param(
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publish = Join-Path $root 'artifacts\publish'
$staging = Join-Path $root 'artifacts\publish-next'
$localExe = Join-Path $publish 'NetLights.exe'
New-Item -ItemType Directory -Force -Path $staging | Out-Null
dotnet publish src/NetLights.App/NetLights.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $staging
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish src/NetLights.UpdateAgent/NetLights.UpdateAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o $staging
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$localProcesses = @(Get-Process NetLights -ErrorAction SilentlyContinue | Where-Object { $_.Path -and [string]::Equals($_.Path, $localExe, [System.StringComparison]::OrdinalIgnoreCase) })
if ($localProcesses.Count -gt 0) {
    & $localExe --exit-local
    foreach ($process in $localProcesses) {
        try { $process.WaitForExit(15000) | Out-Null } catch { }
        if (-not $process.HasExited) { throw "Локальная копия NetLights не завершилась штатно: PID $($process.Id)" }
    }
}

New-Item -ItemType Directory -Force -Path $publish | Out-Null
Copy-Item -Path (Join-Path $staging '*') -Destination $publish -Recurse -Force

if (-not $NoStart) {
    Start-Process -FilePath $localExe -WindowStyle Hidden
}
