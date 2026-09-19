param(
    [int]$Minutes = 2,
    [string]$OutDir = "artifacts/soak"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$seconds = [Math]::Max(20, $Minutes * 60)
if ($seconds -gt 7200) { $seconds = 7200 }
$env:NETLIGHTS_SOAK_SECONDS = "$seconds"
$started = [DateTimeOffset]::UtcNow
dotnet test tests/NetLights.SoakTests/NetLights.SoakTests.csproj -c Release --logger "trx;LogFileName=$OutDir/soak.trx"
$code = $LASTEXITCODE
$ended = [DateTimeOffset]::UtcNow
@{
    startedUtc = $started.ToString("o")
    endedUtc = $ended.ToString("o")
    requestedMinutes = $Minutes
    soakSeconds = $seconds
    exitCode = $code
} | ConvertTo-Json | Set-Content "$OutDir/summary.json"
if ($code -ne 0) {
    exit $code
}
