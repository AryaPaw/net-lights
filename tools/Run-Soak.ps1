param(
    [int]$Minutes = 2,
    [string]$OutDir = "artifacts/soak"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$started = [DateTimeOffset]::UtcNow
dotnet test tests/NetLights.SoakTests/NetLights.SoakTests.csproj -c Release --logger "trx;LogFileName=$OutDir/soak.trx"
$ended = [DateTimeOffset]::UtcNow
@{
    startedUtc = $started.ToString("o")
    endedUtc = $ended.ToString("o")
    requestedMinutes = $Minutes
    note = "Use -Minutes 60 for T57 and 1440 for T58. This script currently runs the short soak test project."
} | ConvertTo-Json | Set-Content "$OutDir/summary.json"
