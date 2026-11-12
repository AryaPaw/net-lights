param([string]$PublishDirectory = 'artifacts/publish', [string]$ResultsDirectory = ('TestResults/published-' + [Guid]::NewGuid().ToString('N')))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$publish = [IO.Path]::GetFullPath($PublishDirectory, $root)
$results = [IO.Path]::GetFullPath($ResultsDirectory, $root)
if (Test-Path -LiteralPath $results) { throw 'Published smoke needs a new results directory.' }
New-Item -ItemType Directory -Path $results | Out-Null
$testProfileDirectory = Join-Path $results 'profile'
New-Item -ItemType Directory -Path $testProfileDirectory | Out-Null
$exe = Join-Path $publish 'NetLights.exe'
$agent = Join-Path $publish 'NetLights.UpdateAgent.exe'
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = [string]$props.Project.PropertyGroup.Version
foreach ($file in @($exe,$agent)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing published file: $file" }
    if (([Diagnostics.FileVersionInfo]::GetVersionInfo($file).ProductVersion -split '\+')[0] -ne $version) { throw "Published version mismatch: $file" }
}
$appHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
$agentHash = (Get-FileHash -LiteralPath $agent -Algorithm SHA256).Hash
# Invalid arguments must exit before the agent opens the live update directory.
$agentStart = [Diagnostics.ProcessStartInfo]::new($agent)
$agentStart.UseShellExecute = $false
$agentStart.CreateNoWindow = $true
$agentProbe = [Diagnostics.Process]::Start($agentStart)
try {
    if (-not $agentProbe.WaitForExit(10000)) { throw 'Published agent preflight timed out.' }
    if ($agentProbe.ExitCode -ne 2) { throw 'Published agent did not reject missing arguments safely.' }
}
finally {
    if (-not $agentProbe.HasExited) { $agentProbe.Kill($true); $agentProbe.WaitForExit(5000) | Out-Null }
    $agentProbe.Dispose()
}
$seedStart = [DateTimeOffset]::UtcNow.AddMinutes(-5).ToString('o')
$seedEnd = [DateTimeOffset]::UtcNow.AddMinutes(-4).ToString('o')
@{settingsVersion=0; autoStart=$true; autoUpdateEnabled=$true; geoCountryDetectionEnabled=$false; availabilityPollingSeconds=5; countryPollingSeconds=10; windowWidth=1000; windowHeight=800} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testProfileDirectory 'settings.json') -Encoding utf8
@{version=3; lastObservedUtc=$seedEnd; stays=@(@{iso='DE'; startedUtc=$seedStart; endedUtc=$seedEnd}); observations=@()} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testProfileDirectory 'location-history.json') -Encoding utf8
@{lastObservedUtc=$seedEnd; spans=@(@{group='Ru'; state='Online'; paused=$false; startedUtc=$seedStart; endedUtc=$seedEnd})} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $testProfileDirectory 'availability-history.json') -Encoding utf8
# Closed localhost ports produce real, bounded TCP failures without sending public traffic
# or weakening production certificate validation. All fourteen endpoints remain distinct.
$endpoints = foreach ($group in @('Ru','World')) { foreach ($index in 0..6) { @{id="$group-$index"; group=$group; uri="https://127.0.0.1:1/$group-$index"; infrastructureId="local-$group-$index"} } }
$endpoints | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testProfileDirectory 'endpoints.json') -Encoding utf8
$started = [DateTimeOffset]::UtcNow
$process = $null
$readyName = 'Local\NetLights.UpdateHealth.' + [Guid]::NewGuid().ToString('N')
$ready = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$readyName)
try {
    foreach ($run in 1..2) {
        $ready.Reset() | Out-Null
        $start = [Diagnostics.ProcessStartInfo]::new($exe)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        foreach ($argument in @('--profile-directory',$testProfileDirectory,'--update-health-event',$readyName)) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        if (-not $ready.WaitOne(30000)) { throw "Published app did not confirm a real probe on run $run." }
        if ($process.WaitForExit(3000)) { throw "Published app exited early on run $run." }
        $exit = [Diagnostics.ProcessStartInfo]::new($exe)
        $exit.UseShellExecute = $false
        $exit.CreateNoWindow = $true
        foreach ($argument in @('--exit-local','--profile-directory',$testProfileDirectory)) { $exit.ArgumentList.Add($argument) }
        $exitCommand = [Diagnostics.Process]::Start($exit)
        try { if (-not $exitCommand.WaitForExit(10000)) { throw 'Exit command timed out.' } }
        finally {
            if (-not $exitCommand.HasExited) { $exitCommand.Kill($true); $exitCommand.WaitForExit(5000) | Out-Null }
            $exitCommand.Dispose()
        }
        if (-not $process.WaitForExit(20000)) { throw 'Published app did not save and exit gracefully.' }
        if ($process.ExitCode -ne 0) { throw "Published app failed with exit $($process.ExitCode)." }
        $process.Dispose(); $process = $null
        $settings = Get-Content -LiteralPath (Join-Path $testProfileDirectory 'settings.json') -Raw | ConvertFrom-Json
        $locations = Get-Content -LiteralPath (Join-Path $testProfileDirectory 'location-history.json') -Raw | ConvertFrom-Json
        $availability = Get-Content -LiteralPath (Join-Path $testProfileDirectory 'availability-history.json') -Raw | ConvertFrom-Json
        if ($settings.availabilityPollingSeconds -ne 5 -or $settings.autoStart -or $settings.autoUpdateEnabled) { throw 'Isolated settings did not survive restart.' }
        if (-not @($locations.stays | Where-Object { $_.iso -eq 'DE' -and [DateTimeOffset]$_.startedUtc -eq [DateTimeOffset]$seedStart }).Count) { throw 'Seeded location history was lost.' }
        if (-not @($availability.spans | Where-Object { $_.group -eq 'Ru' -and [DateTimeOffset]$_.startedUtc -eq [DateTimeOffset]$seedStart }).Count) { throw 'Seeded availability history was lost.' }
        if ([DateTimeOffset]$availability.lastObservedUtc -lt $started) { throw 'Published monitor did not persist new observations.' }
    }
    if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $appHash -or
        (Get-FileHash -LiteralPath $agent -Algorithm SHA256).Hash -ne $agentHash) { throw 'Published candidate changed during verification.' }
    @{status='PASS'; version=$version; appSha256=$appHash; agentSha256=$agentHash; agentPreflightExit=2; runs=2; utc=[DateTimeOffset]::UtcNow} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results 'result.json') -Encoding utf8
    Write-Output "Published process, monitor, history and restart smoke PASS: $results"
}
finally {
    $ready.Dispose()
    if ($process) {
        # This exact Process object was created above, never a process selected by name.
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
}
