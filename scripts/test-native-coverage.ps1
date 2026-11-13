param(
    [string]$ResultsDirectory = ('TestResults/native-coverage-'+[Guid]::NewGuid().ToString('N')),
    [switch]$NativeUI,
    [switch]$FunctionalUI,
    [ValidateSet(0,30)][int]$GeoMeasurementSeconds = 0,
    [switch]$SelfTest
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($NativeUI -and $FunctionalUI) {throw 'Select NativeUI or FunctionalUI, not both.'}
if($GeoMeasurementSeconds -gt 0 -and -not $NativeUI) {throw 'GeoMeasurementSeconds requires NativeUI.'}
$root=Split-Path -Parent $PSScriptRoot
$target=Join-Path $root 'artifacts/rust-coverage-target'
$results=[IO.Path]::GetFullPath($ResultsDirectory,$root)
$tool=Join-Path $root 'artifacts/tools/bin/cargo-llvm-cov.exe'
$savedEnvironment=@{}
$lock=$null
function Assert-LocalArtifactPath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    $allowed=$false
    foreach($directory in @('artifacts','TestResults')) {
        $prefix=[IO.Path]::GetFullPath((Join-Path $root $directory))+[IO.Path]::DirectorySeparatorChar
        if($full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { $allowed=$true }
    }
    if(-not $allowed) {throw 'Coverage output must be inside artifacts or TestResults.'}
    for($part=$full;$part;$part=[IO.Path]::GetDirectoryName($part)) {
        if(Test-Path -LiteralPath $part) {
            if((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Coverage path contains a reparse point.'}
        }
    }
}
function Set-RunEnvironment([string]$Name,[AllowNull()][object]$Value) {
    if(-not $savedEnvironment.ContainsKey($Name)) { $savedEnvironment[$Name]=[Environment]::GetEnvironmentVariable($Name,'Process') }
    # A typed string parameter converts $null to ''. On .NET 9+, that keeps an
    # empty variable, overriding Cargo's configured CRT flags instead of removing it.
    # NullString preserves CLR null at the PowerShell method-call boundary too.
    if($null -eq $Value) {[Environment]::SetEnvironmentVariable($Name,[NullString]::Value,'Process')}
    else {[Environment]::SetEnvironmentVariable($Name,[string]$Value,'Process')}
}
function Assert-BaseRustFlagsRemoved {
    foreach($name in @('RUSTFLAGS','CARGO_ENCODED_RUSTFLAGS')) {
        if($null -ne [Environment]::GetEnvironmentVariable($name,'Process')) {throw 'Clearing Rust flags left a present environment variable.'}
    }
}
function Ensure-CargoCacheTag([string]$Folder) {
    Assert-LocalArtifactPath $Folder
    if([IO.File]::ReadAllText((Join-Path $Folder '.netlights-coverage-target')) -cne $root) {throw 'Cache bootstrap requires proven coverage target ownership.'}
    $tagPath=Join-Path $Folder 'CACHEDIR.TAG'
    $signature='Signature: 8a477f597d28d172789f06886806bc55'
    if(Test-Path -LiteralPath $tagPath) {
        Assert-LocalArtifactPath $tagPath
        if(-not [IO.File]::ReadAllText($tagPath).StartsWith($signature,[StringComparison]::Ordinal)) {throw 'Existing Cargo cache tag is invalid; retained.'}
        return
    }
    # Cargo 1.98 refuses clean in a pre-created target without its cache tag.
    # Create the standard tag only after the exact target/owner checks above.
    $stream=[IO.File]::Open($tagPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {
        $bytes=[Text.Encoding]::ASCII.GetBytes($signature+"`n# This file is a cache directory tag created for the owned Net Lights coverage target.`n# https://bford.info/cachedir/`n")
        $stream.Write($bytes)
    } finally {$stream.Dispose()}
}
function Invoke-Bounded([string]$Name,[string]$File,[string[]]$Arguments,[int]$Seconds=1800) {
    $stdout=Join-Path $results "$Name.log"; $stderr=Join-Path $results "$Name.stderr.log"
    if((Test-Path -LiteralPath $stdout) -or (Test-Path -LiteralPath $stderr)) {throw "Log already exists: $Name"}
    # Start-Process's string joining loses spaces in paths. Quote each argument
    # using the Windows CRT rules, without passing the command to a shell.
    $quoted=foreach($argument in $Arguments) {
        '"'+([regex]::Replace([regex]::Replace($argument,'(\\*)"','$1$1\"'),'(\\+)$','$1$1'))+'"'
    }
    $start=@{FilePath=$File;ArgumentList=($quoted -join ' ');WorkingDirectory=$root;PassThru=$true;RedirectStandardOutput=$stdout;RedirectStandardError=$stderr;WindowStyle='Hidden'}
    $process=Start-Process @start
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    try {
        while(-not $process.WaitForExit(500)) {
            if([DateTimeOffset]::UtcNow -gt $deadline) {throw "Step timed out: $Name ($Seconds seconds)"}
            foreach($log in @($stdout,$stderr)) {if((Get-Item -LiteralPath $log).Length -gt 64MB){throw "Step log exceeded 64 MiB: $Name"}}
        }
        if($process.ExitCode -ne 0) {Get-Content -LiteralPath $stderr -Tail 40;Get-Content -LiteralPath $stdout -Tail 30;throw "$Name failed ($($process.ExitCode))."}
    }
    finally {
        if(-not $process.HasExited) {$process.Kill($true);$process.WaitForExit(10000)|Out-Null}
        $process.Dispose()
    }
    Write-Host "RUN - PASS: $Name"
    return $stdout
}
function Get-Inputs {
    $snapshot=[ordered]@{}
    $paths=@(Get-ChildItem -LiteralPath (Join-Path $root 'native') -Recurse -File | Where-Object {$_.Extension -in @('.rs','.toml')})
    foreach($path in @('Cargo.toml','Cargo.lock','rust-toolchain.toml','.cargo/config.toml','scripts/check-native-coverage.ps1','scripts/test-native-coverage.ps1','scripts/test-native-ui.ps1','scripts/ci-environment.ps1','scripts/test-published.ps1','scripts/native-ui-smoke.py')) {
        $paths+=Get-Item -LiteralPath (Join-Path $root $path)
    }
    foreach($file in $paths|Sort-Object FullName -Unique) {$snapshot[[IO.Path]::GetRelativePath($root,$file.FullName)]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
    return $snapshot
}
function Get-RawProfiles([string]$Folder) {
    $profiles=@(Get-ChildItem -LiteralPath $Folder -Filter '*.profraw' -File)
    if($profiles.Count -gt 1024 -or ($profiles.Count -gt 0 -and ($profiles|Measure-Object Length -Sum).Sum -gt 1GB) -or @($profiles|Where-Object {$_.Length -gt 64MB -or $_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count) {throw 'Raw profiles exceed bounds or contain a reparse point.'}
    return $profiles
}
function Assert-FreshProfiles([string]$Folder,[int[]]$RequiredPids=@()) {
    $profiles=@(Get-RawProfiles $Folder)
    if(-not @($profiles|Where-Object {$_.Length -gt 0}).Count) {throw 'Fresh nonempty raw profiles are missing; cached profdata cannot prove this run.'}
    foreach($requiredPid in $RequiredPids) {
        if(-not @($profiles|Where-Object {$_.Length -gt 0 -and $_.Name -like "netlights-$requiredPid-*.profraw"}).Count) {throw "Actual application PID $requiredPid has no fresh coverage profile."}
    }
}
function Get-CompletedGeoPid($Evidence,[int]$Seconds) {
    foreach($name in @('v2fly','iplocate','public_lookup','both_versions_recorded')) {
        $value=$Evidence.metadata.country_measurement_readiness.$name
        if($value -isnot [bool] -or -not $value) {throw "Requested Geo coverage is NOT RUN: $name readiness is missing."}
    }
    $checks=@($Evidence.checks|Where-Object {$_.check -eq 'resource-measurement' -and $_.result -eq 'PASS'})
    if($checks.Count -ne 1 -or $checks[0].detail.live_country_detection -cne $true -or
       -not [double]::IsFinite([double]$checks[0].detail.elapsed_seconds) -or [double]$checks[0].detail.elapsed_seconds -lt $Seconds -or [int]$checks[0].detail.pid -lt 1) {throw 'Requested bounded Geo exercise did not complete.'}
    return [int]$checks[0].detail.pid
}
function Assert-FunctionalEvidence($Evidence,[string]$ExpectedHash,[string]$EndpointFile,[switch]$RequireLocale) {
    if($Evidence.metadata.evidenceLevel -cne 'functional' -or $Evidence.metadata.visualAcceptance -cne 'NOT RUN') {throw 'Functional UI evidence must explicitly leave visual acceptance NOT RUN.'}
    if($Evidence.metadata.coverage_instrumented -isnot [bool] -or -not $Evidence.metadata.coverage_instrumented -or
        $Evidence.metadata.navigation_smoothness -cne 'NOT RUN') {throw 'Coverage UI must identify instrumentation and leave navigation smoothness NOT RUN.'}
    if([int]$Evidence.metadata.windows_build -lt 1 -or [int]$Evidence.metadata.windows_product_type -notin @(1,2,3)) {throw 'Actual Windows environment metadata is missing.'}
    if($Evidence.metadata.country_measurement_requested -isnot [bool] -or $Evidence.metadata.country_measurement_requested -or [int]$Evidence.metadata.measured_seconds -ne 0) {throw 'Offline functional UI cannot include a live Geo or resource measurement.'}
    if($Evidence.metadata.assertions_enabled -isnot [bool] -or -not $Evidence.metadata.assertions_enabled) {throw 'Functional UI assertions must be enabled.'}
    if($Evidence.metadata.fixture.offline_availability_endpoints -isnot [bool] -or -not $Evidence.metadata.fixture.offline_availability_endpoints) {throw 'Functional UI must use its offline availability fixture.'}
    $endpointInfo=Get-Item -LiteralPath $EndpointFile
    if($endpointInfo.Length -gt 65536 -or $endpointInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Offline endpoint evidence is invalid.'}
    $endpoints=@(Get-Content -LiteralPath $EndpointFile -Raw|ConvertFrom-Json)
    if($endpoints.Count -ne 14 -or @($endpoints.id|Sort-Object -Unique).Count -ne 14 -or @($endpoints.infrastructureId|Sort-Object -Unique).Count -ne 14) {throw 'Offline endpoint identities must be distinct.'}
    foreach($group in @('Ru','World')) {if(@($endpoints|Where-Object group -CEQ $group).Count -ne 7) {throw 'Offline endpoint groups are incomplete.'}}
    foreach($endpoint in $endpoints) {
        if($endpoint.id -isnot [string] -or $endpoint.infrastructureId -isnot [string] -or
            $endpoint.id -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or $endpoint.infrastructureId -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {throw 'Offline endpoint identity is invalid.'}
        $uri=[uri]$endpoint.uri; $address=$null
        if($uri.Scheme -cne 'https' -or $uri.Port -ne 1 -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
            -not[Net.IPAddress]::TryParse($uri.Host,[ref]$address) -or -not[Net.IPAddress]::IsLoopback($address)) {throw 'Functional availability probes must use loopback only.'}
    }
    if(@($endpoints.uri|Sort-Object -Unique).Count -ne 14) {throw 'Offline endpoint addresses must be distinct.'}
    $settingsFile=Get-Item -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($EndpointFile)) 'settings.json')
    if($settingsFile.Length -gt 1MB -or $settingsFile.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Offline settings evidence is invalid.'}
    $settings=Get-Content -LiteralPath $settingsFile.FullName -Raw|ConvertFrom-Json
    foreach($key in @('geoCountryDetectionEnabled','geoCountryIconEnabled','autoStart','autoUpdateEnabled')) {
        if($settings.$key -isnot [bool] -or $settings.$key) {throw 'Functional UI enabled a prohibited public or global action.'}
    }
    if($ExpectedHash -cnotmatch '^[0-9a-fA-F]{64}$' -or $Evidence.metadata.exe_sha256 -ine $ExpectedHash) {throw 'Functional UI used a different executable.'}
    $pids=@($Evidence.metadata.pids|ForEach-Object {[int]$_})
    $expectedPidCount=if($RequireLocale){5}else{4}
    if($Evidence.metadata.locale_required -isnot [bool] -or $Evidence.metadata.locale_required -ne [bool]$RequireLocale) {throw 'Functional UI locale capability differs from the candidate source.'}
    if($RequireLocale -and ($Evidence.metadata.locale_verified -isnot [bool] -or -not $Evidence.metadata.locale_verified)) {throw 'Required locale switching/restart evidence is missing.'}
    if($pids.Count -ne $expectedPidCount -or @($pids|Where-Object {$_ -lt 1}).Count -or @($pids|Sort-Object -Unique).Count -ne $expectedPidCount) {throw 'Functional UI process identities do not match the required restart matrix.'}
    $required=@('fluent-tabs-about-and-settings-navigation','overview-linked-selection-zoom-and-addresses','physical-outer-geometry-reset-and-restart','native-caption-controls-and-hittest','default-debug-off',
        'dropdown-wheel-does-not-change-settings','X-hide-and-show-local-same-PID','Debug-on-and-off-survive-restart','history-end-and-single-scroll-owner')
    foreach($page in @('Состояние','Локации')) {foreach($hours in @(1,3,6,12,24,72,120,168)) {$required+="$page-${hours}h"}}
    if($RequireLocale) {$required+=@('state-no-duplicate-provider-world','locale-English-restart-system-Russian')}
    foreach($name in $required) {
        $matchingChecks=@($Evidence.checks|Where-Object {$_.check -ceq $name})
        if($matchingChecks.Count -ne 1 -or $matchingChecks[0].result -cne 'PASS') {throw "Functional UI mandatory check is missing, duplicated or failed: $name"}
    }
    $navigation=@($Evidence.checks|Where-Object check -CEQ 'fluent-tabs-about-and-settings-navigation')
    if($navigation[0].detail.navigation_motion.smoothness -cne 'NOT RUN') {throw 'Instrumented navigation detail cannot claim smoothness acceptance.'}
    if(@($Evidence.checks|Where-Object {$_.result -cne 'PASS' -and -not($_.check -ceq 'resource-measurement' -and $_.result -ceq 'NOT RUN')}).Count) {throw 'Functional UI contains failed or incomplete checks.'}
    $resource=@($Evidence.checks|Where-Object {$_.check -ceq 'resource-measurement'})
    if($resource.Count -ne 1 -or $resource[0].result -cne 'NOT RUN' -or @($Evidence.cleanup_errors).Count -or @($Evidence.resource_errors).Count -or [int]$Evidence.resource_samples -lt 1) {throw 'Functional UI cleanup or resource evidence is incomplete.'}
    return $pids
}
function Archive-Profiles([string]$Destination,[switch]$RemoveAfterArchive,[string]$Folder=$target) {
    $profiles=@(Get-ChildItem -LiteralPath $Folder -File|Where-Object {$_.Extension -in @('.profraw','.profdata') -or $_.Name -like '*-profraw-list'})
    if($profiles.Count -eq 0) {return}
    if($profiles.Count -gt 1024 -or ($profiles|Measure-Object Length -Sum).Sum -gt 1GB -or @($profiles|Where-Object {$_.Length -gt 64MB -or $_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count) {throw 'Coverage archive exceeds bounds or contains a reparse point; retained.'}
    New-Item -ItemType Directory -Path $Destination | Out-Null
    foreach($profileFile in $profiles) {
        $copy=Join-Path $Destination $profileFile.Name
        Copy-Item -LiteralPath $profileFile.FullName -Destination $copy
        if((Get-FileHash -LiteralPath $profileFile.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) {throw 'Raw profile archive verification failed; original retained.'}
    }
    if($RemoveAfterArchive) {foreach($profileFile in $profiles) {Remove-Item -LiteralPath $profileFile.FullName}}
}
function Invoke-RunnerControls {
    $probe=Join-Path $root ('artifacts/coverage-runner-selftest-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $probe|Out-Null
    function Assert-Throws([scriptblock]$Action) {
        $rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true}
        if(-not$rejected){throw 'Runner negative control accepted missing fresh evidence.'}
    }
    $originalFlags=@{}
    foreach($name in @('RUSTFLAGS','CARGO_ENCODED_RUSTFLAGS')) {$originalFlags[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
    try {
        foreach($name in $originalFlags.Keys) {Set-RunEnvironment $name $null}
        Assert-BaseRustFlagsRemoved
        foreach($name in $originalFlags.Keys) {
            Set-RunEnvironment $name ''
            if($null -eq [Environment]::GetEnvironmentVariable($name,'Process') -or [Environment]::GetEnvironmentVariable($name,'Process') -cne '') {throw 'An explicit empty environment value was not preserved.'}
            $rejected=$false
            try{Assert-BaseRustFlagsRemoved}catch{if($_.Exception.Message -cne 'Clearing Rust flags left a present environment variable.'){throw};$rejected=$true}
            if(-not $rejected){throw 'Present-empty Rust flags bypassed the preflight guard.'}
            Set-RunEnvironment $name $null
            Assert-BaseRustFlagsRemoved
        }
    }finally {
        foreach($name in $originalFlags.Keys){Set-RunEnvironment $name $originalFlags[$name];$savedEnvironment.Remove($name)}
    }
    foreach($initial in @($null,'')) {
        $name='NETLIGHTS_ENV_CONTROL_'+[Guid]::NewGuid().ToString('N')
        try {
            Set-RunEnvironment $name $initial
            $savedEnvironment.Remove($name)
            Set-RunEnvironment $name 'temporary-control-value'
            Set-RunEnvironment $name $savedEnvironment[$name]
            $restored=[Environment]::GetEnvironmentVariable($name,'Process')
            if(($null -eq $restored) -ne ($null -eq $initial) -or $restored -cne $initial){throw 'Environment restoration confused absent and present-empty values.'}
        }finally{Set-RunEnvironment $name $null;$savedEnvironment.Remove($name)}
    }
    [IO.File]::WriteAllText((Join-Path $probe 'old.profdata'),'old counters')
    Assert-Throws {Assert-FreshProfiles $probe}
    [IO.File]::WriteAllText((Join-Path $probe 'netlights-123-test.profraw'),'fresh fixture')
    Assert-FreshProfiles $probe @(123)
    Assert-Throws {Assert-FreshProfiles $probe @(456)}
    [IO.File]::WriteAllText((Join-Path $probe 'keep.txt'),'unrelated')
    [IO.File]::WriteAllText((Join-Path $probe 'old-profraw-list'),'old list')
    Archive-Profiles (Join-Path $probe 'archived') -Folder $probe -RemoveAfterArchive
    if(@(Get-ChildItem -LiteralPath $probe -File).Count -ne 1 -or -not(Test-Path -LiteralPath (Join-Path $probe 'keep.txt'))) {throw 'Profile cleanup affected foreign files or retained old counters.'}
    if(@(Get-ChildItem -LiteralPath (Join-Path $probe 'archived') -File).Count -ne 3) {throw 'Old counters were not archived before removal.'}
    Assert-Throws {Assert-FreshProfiles $probe}
    $bootstrap=Join-Path $probe 'bootstrap';New-Item -ItemType Directory -Path $bootstrap|Out-Null
    [IO.File]::WriteAllText((Join-Path $bootstrap '.netlights-coverage-target'),'foreign workspace')
    Assert-Throws {Ensure-CargoCacheTag $bootstrap}
    [IO.File]::WriteAllText((Join-Path $bootstrap '.netlights-coverage-target'),$root)
    Ensure-CargoCacheTag $bootstrap
    Ensure-CargoCacheTag $bootstrap
    if(-not [IO.File]::ReadAllText((Join-Path $bootstrap 'CACHEDIR.TAG')).StartsWith('Signature: 8a477f597d28d172789f06886806bc55')) {throw 'Cargo cache bootstrap signature mismatch.'}
    [IO.File]::WriteAllText((Join-Path $bootstrap 'CACHEDIR.TAG'),'foreign invalid marker')
    Assert-Throws {Ensure-CargoCacheTag $bootstrap}
    $geo=@{metadata=@{country_measurement_readiness=@{v2fly=$true;iplocate=$true;public_lookup=$true;both_versions_recorded=$true}};checks=@(@{check='resource-measurement';result='PASS';detail=@{live_country_detection=$true;elapsed_seconds=30.1;pid=123}})}
    if((Get-CompletedGeoPid $geo 30) -ne 123) {throw 'Geo evidence positive control failed.'}
    $geo.metadata.country_measurement_readiness.iplocate=$false
    Assert-Throws {Get-CompletedGeoPid $geo 30}
    $geo.metadata.country_measurement_readiness.iplocate=$true
    $geo.checks[0].result='NOT RUN'
    Assert-Throws {Get-CompletedGeoPid $geo 30}
    $geo.checks[0].result='PASS';$geo.checks[0].detail.elapsed_seconds=20
    Assert-Throws {Get-CompletedGeoPid $geo 30}
    $fixtureHash='a'*64
    $functionalEndpointFile=Join-Path $probe 'endpoints.json'
    @{geoCountryDetectionEnabled=$false;geoCountryIconEnabled=$false;autoStart=$false;autoUpdateEnabled=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $probe 'settings.json') -Encoding utf8
    $functionalEndpoints=@(foreach($group in @('Ru','World')) {foreach($i in 0..6) {@{id="$group-$i";group=$group;uri="https://127.0.0.1:1/$group-$i";infrastructureId="local-$group-$i"}}})
    $functionalEndpoints|ConvertTo-Json|Set-Content -LiteralPath $functionalEndpointFile -Encoding utf8
    $functional=@{metadata=@{fixture=@{offline_availability_endpoints=$true};coverage_instrumented=$true;navigation_smoothness='NOT RUN';locale_required=$false;evidenceLevel='functional';visualAcceptance='NOT RUN';windows_build=20348;windows_product_type=3;country_measurement_requested=$false;measured_seconds=0;assertions_enabled=$true;exe_sha256=$fixtureHash;pids=@(123,124,125,126)};checks=@();cleanup_errors=@();resource_errors=@();resource_samples=1}
    Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile}
    foreach($name in @('overview-linked-selection-zoom-and-addresses','fluent-tabs-about-and-settings-navigation','physical-outer-geometry-reset-and-restart','native-caption-controls-and-hittest','default-debug-off','dropdown-wheel-does-not-change-settings','X-hide-and-show-local-same-PID','Debug-on-and-off-survive-restart','history-end-and-single-scroll-owner')) {$functional.checks+=@{check=$name;result='PASS'}}
    foreach($page in @('Состояние','Локации')) {foreach($hours in @(1,3,6,12,24,72,120,168)) {$functional.checks+=@{check="$page-${hours}h";result='PASS'}}}
    $functional.checks+=@{check='resource-measurement';result='NOT RUN'}
    $functional.checks[1].detail=@{navigation_motion=@{smoothness='NOT RUN'}}
    $validatedPids=@(Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile)
    foreach($kind in @('instrumented-false','instrumented-type','smoothness','motion-smoothness','missing-mode','missing-smoothness')) {
        $bad=ConvertFrom-Json (ConvertTo-Json $functional -Depth 12)
        switch($kind) {
            'instrumented-false'{$bad.metadata.coverage_instrumented=$false}
            'instrumented-type'{$bad.metadata.coverage_instrumented='true'}
            'smoothness'{$bad.metadata.navigation_smoothness='PASS'}
            'motion-smoothness'{$bad.checks[1].detail.navigation_motion.smoothness='PASS'}
            'missing-mode'{$bad.metadata.PSObject.Properties.Remove('coverage_instrumented')}
            'missing-smoothness'{$bad.metadata.PSObject.Properties.Remove('navigation_smoothness')}
        }
        Assert-Throws {Assert-FunctionalEvidence $bad $fixtureHash $functionalEndpointFile}
    }
    $overviewEvidence=$functional.checks[0]
    $functional.checks=$functional.checks[1..($functional.checks.Count-1)]
    Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile}
    $functional.checks=@($overviewEvidence)+$functional.checks
    $functional.checks+=@{check='overview-linked-selection-zoom-and-addresses';result='PASS'}
    Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile}
    $functional.checks=$functional.checks[0..($functional.checks.Count-2)]
    $overviewEvidence.result='NOT RUN';Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile};$overviewEvidence.result='PASS'
    if($validatedPids.Count -ne 4 -or $validatedPids[3] -ne 126) {throw 'Functional UI process identities were not returned.'}
    Assert-Throws {Assert-FunctionalEvidence $functional ('b'*64) $functionalEndpointFile}
    $functional.metadata.assertions_enabled=$false;Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile};$functional.metadata.assertions_enabled=$true
    $functional.checks[0].result='FAIL';Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile};$functional.checks[0].result='PASS'
    $functional.checks+=@{check='Состояние-1h';result='PASS'};Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile};$functional.checks=$functional.checks[0..($functional.checks.Count-2)]
    $functional.cleanup_errors=@('cleanup failed');Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile};$functional.cleanup_errors=@()
    $functional.metadata.pids=@(123,123,125,126);Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile};$functional.metadata.pids=@(123,124,125,126)
    $functional.metadata.visualAcceptance='PASS'
    Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile}
    $functional.metadata.visualAcceptance='NOT RUN';$functional.metadata.country_measurement_requested=$true
    Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile}
    $functional.metadata.country_measurement_requested=$false
    $functionalEndpoints[0].uri='https://example.com/'
    $functionalEndpoints|ConvertTo-Json|Set-Content -LiteralPath $functionalEndpointFile -Encoding utf8
    Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile}
    $functionalEndpoints[0].uri='https://127.0.0.1:1/Ru-0'
    $functionalEndpoints[1].infrastructureId=$functionalEndpoints[0].infrastructureId
    $functionalEndpoints|ConvertTo-Json|Set-Content -LiteralPath $functionalEndpointFile -Encoding utf8
    Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile}
    $functionalEndpoints[1].infrastructureId='local-Ru-1'
    $functionalEndpoints|ConvertTo-Json|Set-Content -LiteralPath $functionalEndpointFile -Encoding utf8
    Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile -RequireLocale}
    $functional.metadata.locale_required=$true;$functional.metadata.locale_verified=$true;$functional.metadata.pids=@(123,124,125,126,127)
    $functional.checks+=@(@{check='state-no-duplicate-provider-world';result='PASS'},@{check='locale-English-restart-system-Russian';result='PASS'})
    Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile -RequireLocale|Out-Null
    $functional.metadata.locale_verified=$false
    Assert-Throws {Assert-FunctionalEvidence $functional $fixtureHash $functionalEndpointFile -RequireLocale}
    Write-Output "RUN - PASS: runner fresh-profile, PID evidence and archive controls. Artifacts: $probe"
}
if($SelfTest) {Invoke-RunnerControls;return}
Assert-LocalArtifactPath $target
Assert-LocalArtifactPath $results
if(Test-Path -LiteralPath $results) {throw 'Coverage requires a fresh results directory.'}
if(-not(Test-Path -LiteralPath $tool -PathType Leaf)) {throw 'Install the pinned cargo-llvm-cov tool before running native coverage.'}
New-Item -ItemType Directory -Path $results|Out-Null
Push-Location $root
try {
    $nativeRustChannel=[regex]::Match((Get-Content -LiteralPath (Join-Path $root 'rust-toolchain.toml') -Raw),'(?m)^channel\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"\s*$').Groups[1].Value
    if(-not $nativeRustChannel) {throw 'The coverage compiler must be pinned to an exact Rust version.'}
    Set-RunEnvironment 'RUSTUP_TOOLCHAIN' $nativeRustChannel
    Set-RunEnvironment 'PYTHONOPTIMIZE' $null
    $lock=[IO.File]::Open((Join-Path $root 'artifacts/native-coverage.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    if(-not(Test-Path -LiteralPath $target)) {New-Item -ItemType Directory -Path $target|Out-Null}
    $marker=Join-Path $target '.netlights-coverage-target'
    if(-not(Test-Path -LiteralPath $marker)) {
        if(@(Get-ChildItem -LiteralPath $target -Force).Count) {throw 'Coverage target is non-empty and ownership is unproven.'}
        [IO.File]::WriteAllText($marker,$root)
    }
    if([IO.File]::ReadAllText($marker) -cne $root) {throw 'Coverage target belongs to another workspace.'}
    Ensure-CargoCacheTag $target
    Archive-Profiles (Join-Path $results 'previous-profiles') -RemoveAfterArchive
    $before=Get-Inputs
    $before|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $results 'source-before.json') -Encoding utf8
    $pwsh=@(Get-Command pwsh -CommandType Application)[0].Source
    $cargo=@(Get-Command cargo -CommandType Application)[0].Source
    Invoke-Bounded 'self-controls' $pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'check-native-coverage.ps1'),'-SelfTest') 60|Out-Null
    Invoke-Bounded 'runner-self-controls' $pwsh @('-NoProfile','-File',$PSCommandPath,'-SelfTest') 60|Out-Null
    $versionLog=Invoke-Bounded 'tool-version' $tool @('llvm-cov','--version') 30
    if((Get-Content -LiteralPath $versionLog -Raw).Trim() -ne 'cargo-llvm-cov 0.9.1') {throw 'Unexpected coverage tool version.'}
    Set-RunEnvironment 'CARGO_TARGET_DIR' $target
    Set-RunEnvironment 'CARGO_LLVM_COV_TARGET_DIR' $target
    Set-RunEnvironment 'LLVM_PROFILE_FILE_NAME' 'netlights-%p-%m.profraw'
    # These override target-specific flags in Cargo. Clear only in this process,
    # restoring them in finally; show-env then includes the repository CRT flags.
    Set-RunEnvironment 'RUSTFLAGS' $null
    Set-RunEnvironment 'CARGO_ENCODED_RUSTFLAGS' $null
    Assert-BaseRustFlagsRemoved
    $envLog=Invoke-Bounded 'coverage-environment' $tool @('llvm-cov','show-env','--pwsh','--target','x86_64-pc-windows-msvc','--coverage-target-only','--no-cfg-coverage') 60
    foreach($line in Get-Content -LiteralPath $envLog) {
        if($line -notmatch '^\$env:([A-Z0-9_]+)="((?:[^"`]|`u\{[a-fA-F0-9]+\})*)"$') {throw 'Unexpected coverage environment output.'}
        $name=$Matches[1];$encoded=$Matches[2]
        if($name -notmatch '^(CARGO_|__CARGO_LLVM_COV_|RUST|LLVM_PROFILE_FILE)') {throw 'Unexpected coverage environment variable.'}
        $value=[regex]::Replace($encoded,'`u\{([a-fA-F0-9]+)\}',{param($match)[char]::ConvertFromUtf32([Convert]::ToInt32($match.Groups[1].Value,16))})
        Set-RunEnvironment $name $value
    }
    if($env:CARGO_TARGET_DIR -ne $target -or $env:CARGO_LLVM_COV_TARGET_DIR -ne $target -or $env:CARGO_LLVM_COV_BUILD_DIR -ne $target -or [IO.Path]::GetDirectoryName($env:LLVM_PROFILE_FILE) -ne $target) {throw 'Coverage instrumentation escaped its isolated target.'}
    Set-RunEnvironment 'CARGO_LLVM_COV_DENY_WARNINGS' '1'
    # Official external-test workflow: clean only workspace artifacts in this
    # verified owned target. Dependencies remain cached; old binaries/counters
    # must never contribute to a later candidate's coverage.
    Invoke-Bounded 'clean-workspace' $tool @('llvm-cov','clean','--workspace','--locked') 120|Out-Null
    if(@(Get-ChildItem -LiteralPath $target -File|Where-Object {$_.Extension -in @('.profraw','.profdata')}).Count) {throw 'Coverage clean retained old counters.'}
    $common=@('--workspace','--locked','--release','--target-dir',$target,'-j','4')
    Invoke-Bounded 'build' $cargo (@('build')+$common) 2400|Out-Null
    Invoke-Bounded 'tests' $cargo (@('test')+$common+@('--','--test-threads=1')) 1800|Out-Null
    $published=Join-Path $results 'publish';New-Item -ItemType Directory -Path $published|Out-Null
    Copy-Item -LiteralPath (Join-Path $target 'x86_64-pc-windows-msvc/release/netlights.exe') -Destination (Join-Path $published 'NetLights.exe')
    Copy-Item -LiteralPath (Join-Path $target 'x86_64-pc-windows-msvc/release/netlights-update-agent.exe') -Destination (Join-Path $published 'NetLights.UpdateAgent.exe')
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $published
    $licenseOutput=Join-Path $results 'license-source'
    Invoke-Bounded 'license-collection' $pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'collect-native-licenses.ps1'),'-OutputDirectory',$licenseOutput,'-SupplementDirectory',(Join-Path $root 'native/licenses')) 300|Out-Null
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $licenseOutput 'licenses'),(Join-Path $published 'third-party-licenses.zip'))
    Invoke-Bounded 'published-smoke' $pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'test-published.ps1'),'-PublishDirectory',$published,'-ResultsDirectory',(Join-Path $results 'published-smoke')) 180|Out-Null
    $appPids=@(Get-ChildItem -LiteralPath (Join-Path $results 'published-smoke/profile/logs') -File|ForEach-Object {if($_.Name -match '^native-[0-9]{8}-([0-9]+)\.log$'){[int]$Matches[1]}}|Sort-Object -Unique)
    if($appPids.Count -ne 2) {throw 'Two actual instrumented app restart identities are missing.'}
    Assert-FreshProfiles $target $appPids
    if($NativeUI -or $FunctionalUI) {
        $hash=(Get-FileHash -LiteralPath (Join-Path $published 'NetLights.exe')).Hash
        $uiArguments=@('-NoProfile','-File',(Join-Path $PSScriptRoot 'test-native-ui.ps1'),'-Executable',(Join-Path $published 'NetLights.exe'),
            '-ResultsDirectory',(Join-Path $results 'native-ui'),'-ExpectedSha256',$hash,'-RawProfileDirectory',$target,'-CoverageInstrumented')
        if($FunctionalUI) {$uiArguments+='-FunctionalUI'}
        if($GeoMeasurementSeconds -gt 0) {$uiArguments+=@('-MeasureSeconds',[string]$GeoMeasurementSeconds,'-EnableCountryForMeasurement')}
        # The wrapper owns a VM, not a child process. Allow its runner deadline,
        # packaging, boot and finally cleanup to finish before the outer watchdog.
        Invoke-Bounded 'native-ui' $pwsh $uiArguments 2400|Out-Null
        if($FunctionalUI) {
            $functionalFile=Get-Item -LiteralPath (Join-Path $results 'native-ui/ui/results.json')
            if($functionalFile.Length -gt 1MB) {throw 'Functional UI evidence exceeds its size bound.'}
            $uiPids=@(Assert-FunctionalEvidence (Get-Content -LiteralPath $functionalFile.FullName -Raw|ConvertFrom-Json) $hash (Join-Path $results 'native-ui/ui/profile/endpoints.json') -RequireLocale:(Test-Path -LiteralPath (Join-Path $root 'native/core/src/locale.rs')))
            Assert-FreshProfiles $target $uiPids
            Write-Host 'RUN - PASS: instrumented offline functional UI; navigation smoothness and Windows 11 visual acceptance: NOT RUN.'
        }
        if($GeoMeasurementSeconds -gt 0) {
            $evidenceFile=Get-Item -LiteralPath (Join-Path $results 'native-ui/ui/results.json')
            if($evidenceFile.Length -gt 1MB) {throw 'Native Geo evidence exceeds its size bound.'}
            $evidence=Get-Content -LiteralPath $evidenceFile.FullName -Raw|ConvertFrom-Json
            $geoPid=Get-CompletedGeoPid $evidence $GeoMeasurementSeconds
            Assert-FreshProfiles $target @($geoPid)
            Write-Host "RUN - PASS: actual Geo exercise for $GeoMeasurementSeconds seconds after both databases and a public lookup were ready. Hour resource stability: NOT RUN."
        }
    } else {Write-Host 'UI coverage: NOT RUN (use -FunctionalUI or -NativeUI).'}
    Assert-FreshProfiles $target
    $report=Join-Path $results 'coverage.cobertura.xml'
    Invoke-Bounded 'export' $tool @('llvm-cov','report','--release','--locked','--target','x86_64-pc-windows-msvc','--coverage-target-only','--cobertura','--output-path',$report) 180|Out-Null
    Archive-Profiles (Join-Path $results 'profiles')
    $after=Get-Inputs
    $changed=@(@($before.Keys)+@($after.Keys)|Sort-Object -Unique|Where-Object {-not$before.Contains($_)-or-not$after.Contains($_)-or$before[$_]-ne$after[$_]})
    if($changed.Count) {$after|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $results 'source-after-drift.json');throw "Coverage source changed during execution: $($changed -join ', ')"}
    Invoke-Bounded 'coverage-gate' $pwsh @('-NoProfile','-File',(Join-Path $PSScriptRoot 'check-native-coverage.ps1'),'-CoverageFile',$report,'-OutputPath',(Join-Path $results 'coverage-summary.json')) 60|Out-Null
    Write-Host "RUN - PASS: Rust runtime coverage, actual instrumented process smoke. Results: $results"
}
finally {
    foreach($name in $savedEnvironment.Keys) {Set-RunEnvironment $name $savedEnvironment[$name]}
    if($lock) {$lock.Dispose()}
    Pop-Location
}
