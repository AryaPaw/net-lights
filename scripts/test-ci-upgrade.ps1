#requires -Version 7.5
param(
    [string]$BaselineInstaller,
    [string]$BridgeInstaller,
    [string]$BridgePublishDirectory,
    [string]$NativeInstaller,
    [string]$NativePublishDirectory,
    [string]$ResultsDirectory = ('artifacts/ci-upgrade-' + [Guid]::NewGuid().ToString('N')),
    [ValidateSet('All','Direct','Bridge','LegacyFailures')][string]$Scenario = 'All',
    [ValidateSet('v1.6.0','v1.4.2')][string]$ReleaseTag = 'v1.6.0',
    [string]$BaselineSha256,
    [long]$BaselineBytes,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ci-environment.ps1')
# This assertion precedes all install, registry, process and filesystem writes.
# SelfTest only calls pure validation functions and never invokes operational code.
if (-not $SelfTest) { Assert-HostedRunner }

$repository = Split-Path -Parent $PSScriptRoot
$productKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C2B1A9E-4F31-4A6D-9C2E-1B8E5D2A0F11}_is1'
$published160Sha = '73d993416a298690f26891936d3b794179edc62ad86e67a7144d695840c2dd97'
$published160Bytes = 59140690
$utf8 = [Text.UTF8Encoding]::new($false)
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$ownsFixture = $false

function Assert-Condition([bool]$Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}

function Assert-Sha([string]$Value) {
    Assert-Condition ($Value -cmatch '^[a-fA-F0-9]{64}$') 'Expected an exact SHA-256 digest.'
}

function Assert-ExactFile([string]$Path, [string]$Sha, [long]$MaximumBytes = 80MB) {
    Assert-Sha $Sha
    $item = Get-Item -LiteralPath $Path -Force
    Assert-Condition (-not $item.PSIsContainer -and $item.Length -gt 0 -and $item.Length -le $MaximumBytes) 'Input file size or type is invalid.'
    Assert-Condition (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Linked input files aren''t accepted.'
    Assert-Condition ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ieq $Sha) 'Exact input digest mismatch.'
}

function Assert-Unlinked([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        Assert-Condition (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Linked fixture paths aren''t accepted.'
    }
}

function Get-NewResultsPath([string]$Path, [string]$ArtifactRoot) {
    $full = [IO.Path]::GetFullPath($Path)
    Assert-Condition ([IO.Path]::GetDirectoryName($full) -ieq [IO.Path]::GetFullPath($ArtifactRoot)) 'Results must be a direct child of repository artifacts.'
    Assert-Condition ([IO.Path]::GetFileName($full) -cmatch '^ci-upgrade-[A-Za-z0-9-]+$') 'Results require a distinct ci-upgrade name.'
    Assert-Unlinked $full
    Assert-Condition (-not [IO.Path]::Exists($full)) 'Existing results aren''t overwritten.'
    return $full
}

function Read-Version([string]$Path) {
    return ([Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion -split '\+')[0]
}

function Get-Payload([string]$Setup, [string]$Directory, [string]$Version) {
    Assert-Condition ([IO.Path]::GetFileName($Setup) -ceq ('NetLights-Setup-win-x64-' + $Version + '.exe')) 'Installer name doesn''t match the intended version.'
    $appPath = Join-Path $Directory 'NetLights.exe'
    $agentPath = Join-Path $Directory 'NetLights.UpdateAgent.exe'
    foreach ($path in @($Setup, $appPath, $agentPath)) { Assert-Unlinked $path }
    foreach ($path in @($appPath, $agentPath)) {
        Assert-Condition ((Read-Version $path) -ceq $Version) 'Published executable version mismatch.'
        Assert-Condition ((Get-Item -LiteralPath $path).Length -le 160MB) 'Published executable exceeds the input bound.'
    }
    $setupSha = (Get-FileHash -LiteralPath $Setup -Algorithm SHA256).Hash
    Assert-ExactFile $Setup $setupSha
    return [ordered]@{
        installer = [IO.Path]::GetFullPath($Setup); version = $Version; sha256 = $setupSha
        appSha256 = (Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash
        agentSha256 = (Get-FileHash -LiteralPath $agentPath -Algorithm SHA256).Hash
    }
}

function Assert-BridgeReceipt([object]$Payload, [string]$Directory) {
    $receiptRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Directory).TrimEnd('\','/'))
    $receipt = Get-Content -LiteralPath (Join-Path $receiptRoot 'bridge.json') -Raw | ConvertFrom-Json
    foreach ($check in @('prepare','build','unit','integration','soak','coverage','publish','installer')) {
        Assert-Condition ($receipt.$check -ceq 'PASS') 'The bridge lacks its completed canonical verification receipt.'
    }
    Assert-Condition ($receipt.version -ceq '1.6.1' -and $receipt.baseCommit -ceq '523e33b40a85e28cdb972f9401aba02ea02855d8') 'Bridge source identity mismatch.'
    Assert-Condition ($receipt.patchSha256 -ieq (Get-FileHash -LiteralPath (Join-Path $repository 'compat/bridge-1.6.1.patch')).Hash) 'Bridge compatibility patch is stale.'
    Assert-ExactFile (Join-Path $receiptRoot 'source.json') $receipt.sourceManifestSha256 2MB
    Assert-Condition ($receipt.installerSha256 -ieq $Payload.sha256) 'Bridge installer receipt mismatch.'
    foreach ($name in @('NetLights.exe','NetLights.UpdateAgent.exe')) {
        $records = @($receipt.binaries | Where-Object { $_.name -ceq $name })
        Assert-Condition ($records.Count -eq 1) 'Bridge binary receipt identity is absent or duplicated.'
        Assert-ExactFile (Join-Path $Directory $name) $records[0].sha256 160MB
    }
}

function Assert-ExactState([object]$Before, [object]$After) {
    Assert-Condition (($Before | ConvertTo-Json -Depth 12 -Compress) -ceq ($After | ConvertTo-Json -Depth 12 -Compress)) 'Rollback didn''t restore exact files, registration, Run value and shortcut bytes.'
}

function Assert-SeedRecords([object]$Seed, [object]$Settings, [object]$Locations, [object]$Availability) {
    foreach ($field in $Seed.settingFields) {
        Assert-Condition ($Settings.$field -ceq $Seed.settings.$field) 'A known persisted setting was lost.'
    }
    foreach ($row in @(
        @{ expected = $Seed.stays; actual = $Locations.stays; fields = @('iso','startedUtc','endedUtc') },
        @{ expected = $Seed.observations; actual = $Locations.observations; fields = @('observedUtc','observedUntilUtc','publicIp','publicIpHash','countryCode','localCountry','onlineCountry','thirdCountry','databaseVersion','iplocateCountry','iplocateDatabaseVersion','resolutionSource','confirmed','publicIpChanged','countryChanged','classificationChanged') },
        @{ expected = $Seed.spans; actual = $Availability.spans; fields = @('group','state','paused','startedUtc','endedUtc') }
    )) {
        $available = [Collections.Generic.List[object]]::new()
        foreach ($record in $row.actual) { $available.Add($record) }
        foreach ($expected in $row.expected) {
            $match = -1
            for ($index = 0; $index -lt $available.Count; $index++) {
                $same = $true
                foreach ($field in $row.fields) {
                    if ($field.EndsWith('Utc', [StringComparison]::Ordinal)) {
                        if ([DateTimeOffset]$available[$index].$field -ne [DateTimeOffset]$expected.$field) { $same = $false; break }
                    } elseif ($available[$index].$field -cne $expected.$field) { $same = $false; break }
                }
                if ($same) { $match = $index; break }
            }
            Assert-Condition ($match -ge 0) 'A seeded history record was lost, shortened, reclassified or had its provenance changed.'
            $available.RemoveAt($match)
        }
    }
}

function Assert-RetryRecord([object]$Guard, [string]$Version, [string]$Sha, [DateTimeOffset]$Now) {
    Assert-Condition ($Guard.version -ceq $Version -and $Guard.sha256 -ieq $Sha -and $Guard.attempts -ge 1 -and $Guard.attempts -le 8) 'The durable failed-candidate identity wasn''t preserved.'
    $hours = [Math]::Min(12 * [Math]::Pow(2, $Guard.attempts - 1), 168)
    $remaining = ([DateTimeOffset]$Guard.retryAfterUtc) - $Now
    Assert-Condition ($remaining.TotalHours -ge $hours - 0.1 -and $remaining.TotalHours -le $hours + 0.01) 'The failed-candidate cooldown isn''t the bounded production policy.'
}

function Assert-AttemptResult([object]$Status, [string]$Candidate, [DateTimeOffset]$Started, [DateTimeOffset]$Completed) {
    Assert-Condition ($Status.version -ceq $Candidate -and $Status.success -is [bool]) 'The result isn''t bound to this candidate.'
    $written = [DateTimeOffset]$Status.utc
    Assert-Condition ($written -ge $Started -and $written -le $Completed) 'The result predates this attempt or lies in the future.'
}

function Assert-FreshRetry([object]$Guard, [object]$Previous, [string]$Version, [string]$Sha,
    [DateTimeOffset]$Started, [DateTimeOffset]$Written, [DateTimeOffset]$Recovered, [DateTimeOffset]$Now) {
    Assert-RetryRecord $Guard $Version $Sha $Now
    $expectedAttempts = 1
    if ($Previous -and $Previous.version -ceq $Version -and $Previous.sha256 -ieq $Sha) {
        $expectedAttempts = [Math]::Min([int]$Previous.attempts + 1,8)
    }
    Assert-Condition ($Guard.attempts -eq $expectedAttempts) 'This attempt didn''t advance the durable retry guard.'
    Assert-Condition ($Written -ge $Started -and $Written -le $Recovered) 'The durable guard wasn''t newly written before this recovery process.'
}

function Test-Negative([scriptblock]$Body, [string]$Message) {
    $rejected = $false
    try { & $Body } catch { $rejected = $true }
    Assert-Condition $rejected $Message
}

if ($SelfTest) {
    Assert-Condition (-not $BaselineInstaller -and -not $BridgeInstaller -and -not $NativeInstaller) 'SelfTest can''t be combined with installer execution inputs.'
    Assert-Sha ('a' * 64)
    Test-Negative { Assert-Sha 'not-a-digest' } 'Digest negative control didn''t reject.'
    $unused = Join-Path $repository ('artifacts/ci-upgrade-pure-' + [Guid]::NewGuid().ToString('N'))
    Assert-Condition ((Get-NewResultsPath $unused (Join-Path $repository 'artifacts')) -ceq $unused) 'New owned results validation rejected its positive case.'
    Test-Negative { Get-NewResultsPath $repository (Join-Path $repository 'artifacts') } 'Results ownership negative control didn''t reject.'
    Test-Negative { Assert-ExactState @{ version = '1.6.1' } @{ version = '1.7.0' } } 'Metadata rollback negative control didn''t reject.'
    $at = [DateTimeOffset]::Parse('2026-01-01T00:00:00Z')
    $guard = @{ version = '1.7.0'; sha256 = ('a' * 64); attempts = 1; retryAfterUtc = $at.AddHours(12).ToString('o') }
    Assert-RetryRecord $guard '1.7.0' ('a' * 64) $at
    Test-Negative { Assert-RetryRecord $guard '1.7.0' ('b' * 64) $at } 'Retry digest negative control didn''t reject.'
    $guard.retryAfterUtc = $at.AddMinutes(2).ToString('o')
    Test-Negative { Assert-RetryRecord $guard '1.7.0' ('a' * 64) $at } 'Retry cooldown negative control didn''t reject.'
    $record = @{ iso = 'DE'; startedUtc = $at.ToString('o'); endedUtc = $at.AddMinutes(1).ToString('o') }
    $pureSeed = @{ settings = @{ autoUpdateEnabled = $false }; settingFields = @('autoUpdateEnabled'); stays = @($record); observations = @(); spans = @() }
    $pureSettings = @{ autoUpdateEnabled = $false }
    $pureLocations = @{ stays = @($record); observations = @() }
    $pureAvailability = @{ spans = @() }
    Assert-SeedRecords $pureSeed $pureSettings $pureLocations $pureAvailability
    Test-Negative { Assert-SeedRecords $pureSeed @{ autoUpdateEnabled = $true } $pureLocations $pureAvailability } 'Settings preservation negative control didn''t reject.'
    $early = @{ iso = 'DE'; startedUtc = $record.startedUtc; endedUtc = $at.AddSeconds(30).ToString('o') }
    Test-Negative { Assert-SeedRecords $pureSeed $pureSettings @{ stays = @($early); observations = @() } $pureAvailability } 'Shortened history negative control didn''t reject.'
    $duplicates = @{ settings = $pureSeed.settings; settingFields = $pureSeed.settingFields; stays = @($record,$record); observations = @(); spans = @() }
    Test-Negative { Assert-SeedRecords $duplicates $pureSettings $pureLocations $pureAvailability } 'Multiset history preservation negative control didn''t reject.'
    $freshResult = @{ version = 'NetLights-Setup-win-x64-1.7.0.exe'; success = $false; utc = $at.AddSeconds(1).ToString('o') }
    Assert-AttemptResult $freshResult $freshResult.version $at $at.AddSeconds(3)
    Test-Negative { Assert-AttemptResult $freshResult $freshResult.version $at.AddSeconds(2) $at.AddSeconds(3) } 'Stale result negative control didn''t reject.'
    Test-Negative { Assert-AttemptResult $freshResult 'NetLights-Setup-win-x64-999.0.0.exe' $at $at.AddSeconds(3) } 'Wrong-candidate result negative control didn''t reject.'
    $previous = @{ version = '1.7.0'; sha256 = ('a' * 64); attempts = 1; retryAfterUtc = $at.AddHours(12).ToString('o') }
    $fresh = @{ version = '1.7.0'; sha256 = ('a' * 64); attempts = 2; retryAfterUtc = $at.AddHours(24).AddSeconds(1).ToString('o') }
    Assert-FreshRetry $fresh $previous '1.7.0' ('a' * 64) $at $at.AddSeconds(1) $at.AddSeconds(2) $at.AddSeconds(3)
    Test-Negative { Assert-FreshRetry $previous $previous '1.7.0' ('a' * 64) $at $at.AddSeconds(1) $at.AddSeconds(2) $at.AddSeconds(3) } 'Reused retry record negative control didn''t reject.'
    Test-Negative { Assert-FreshRetry $fresh $previous '1.7.0' ('a' * 64) $at $at.AddSeconds(-1) $at.AddSeconds(2) $at.AddSeconds(3) } 'Stale retry timestamp negative control didn''t reject.'
    Write-Output 'RUN - PASS: pure digest, ownership, exact rollback, retry, settings and history controls. No process, registry or installer was changed.'
    return
}

# Operational code is added below this second guard, not callable by SelfTest.
Assert-HostedRunner
Assert-Condition ($BaselineInstaller -and $NativeInstaller -and $NativePublishDirectory) 'Supply the published baseline and the exact native installer/publish inputs.'
if ($ReleaseTag -ceq 'v1.6.0') {
    if ($BaselineSha256) { Assert-Condition ($BaselineSha256 -ieq $published160Sha) 'The official 1.6.0 baseline digest is pinned.' }
    if ($BaselineBytes) { Assert-Condition ($BaselineBytes -eq $published160Bytes) 'The official 1.6.0 baseline size is pinned.' }
    $BaselineSha256 = $published160Sha
    $BaselineBytes = $published160Bytes
} else {
    Assert-Sha $BaselineSha256
    Assert-Condition ($BaselineBytes -gt 0 -and $BaselineBytes -le 80MB) 'Historical input needs the exact published asset digest and size.'
    Assert-Condition ($Scenario -ceq 'Direct') 'Historical no-ready agents are tested by the Direct scenario; they don''t implement bridge safety.'
}
$baselineVersion = $ReleaseTag.Substring(1)
Assert-Unlinked $BaselineInstaller
Assert-ExactFile $BaselineInstaller $BaselineSha256
Assert-Condition ((Get-Item -LiteralPath $BaselineInstaller).Length -eq $BaselineBytes) 'Published baseline size mismatch.'
Assert-Condition ([IO.Path]::GetFileName($BaselineInstaller) -ceq ('NetLights-Setup-win-x64-' + $baselineVersion + '.exe')) 'Baseline filename doesn''t match ReleaseTag.'
$native = Get-Payload $NativeInstaller $NativePublishDirectory '1.7.0'
$bridge = $null
if ($Scenario -in @('All','Bridge')) {
    Assert-Condition ($BridgeInstaller -and $BridgePublishDirectory) 'This scenario requires verified bridge inputs.'
    $bridge = Get-Payload $BridgeInstaller $BridgePublishDirectory '1.6.1'
    Assert-BridgeReceipt $bridge $BridgePublishDirectory
}
$requested = if ([IO.Path]::IsPathFullyQualified($ResultsDirectory)) { $ResultsDirectory } else { Join-Path $repository $ResultsDirectory }
$results = Get-NewResultsPath $requested (Join-Path $repository 'artifacts')
$install = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\NetLights'
$app = Join-Path $install 'NetLights.exe'
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NetLights'
$updates = Join-Path $data 'updates'
$programGroup = Join-Path ([Environment]::GetFolderPath('Programs')) 'Net Lights'
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'Net Lights.lnk'
foreach ($path in @($install,$data,$programGroup,$startupShortcut,$results)) { Assert-Unlinked $path }
Assert-Condition (-not [IO.Path]::Exists($install) -and -not [IO.Path]::Exists($data) -and -not [IO.Path]::Exists($programGroup) -and -not [IO.Path]::Exists($startupShortcut)) 'The hosted runner already contains Net Lights data, installation or shortcuts; refusing to adopt it.'
Assert-Condition (@(Get-Process NetLights,NetLights.UpdateAgent -ErrorAction SilentlyContinue).Count -eq 0) 'An existing Net Lights process isn''t owned by this fixture.'
$existingRegistration = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($productKey)
try { Assert-Condition ($null -eq $existingRegistration) 'An existing product registration isn''t owned by this fixture.' }
finally { if ($existingRegistration) { $existingRegistration.Dispose() } }
$run = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
try { Assert-Condition ($null -eq $run -or 'NetLights' -notin $run.GetValueNames()) 'An existing Net Lights Run value isn''t owned by this fixture.' }
finally { if ($run) { $run.Dispose() } }

Assert-HostedRunner
$null = New-Item -ItemType Directory -Path $results
$ownsFixture = $true
$result = [ordered]@{
    status = 'FAIL'; environment = 'github-hosted Windows VM'; releaseTag = $ReleaseTag; scenario = $Scenario
    startedUtc = [DateTimeOffset]::UtcNow.ToString('o'); checks = @(); cleanup = 'NOT RUN'
    baseline = @{ sha256 = $BaselineSha256; bytes = $BaselineBytes; version = $baselineVersion
        digestAuthority = $(if ($ReleaseTag -ceq 'v1.6.0') { 'embedded official published digest' } else { 'caller-pinned official release metadata' }) }
    native = $native; bridge = $bridge
    automaticPublicRetryLoop = 'NOT RUN'; retryConsumerEvidence = 'Release unit FakeFeed'
}
[IO.File]::WriteAllText((Join-Path $results 'ownership.json'),(@{
    version = 1; purpose = 'netlights-ci-upgrade'; nonce = [Guid]::NewGuid().ToString('N')
    install = $install; data = $data; initiallyAbsent = $true; hostedRunner = $true
} | ConvertTo-Json),$utf8)

function Save-Result {
    Assert-HostedRunner
    $text = $result | ConvertTo-Json -Depth 18
    Assert-Condition ($text.Length -le 2MB) 'Result log exceeds its bound.'
    $temporary = Join-Path $results ('result-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporary,$text,$utf8)
    [IO.File]::Move($temporary,(Join-Path $results 'result.json'),$true)
}

function Add-Check([string]$Name, [string]$Status, [object]$Detail) {
    $result.checks += [ordered]@{ name = $Name; status = $Status; detail = $Detail }
    Save-Result
    Write-Output ($Status + ': ' + $Name)
}

function Start-Owned([string]$File, [string[]]$Arguments = @()) {
    Assert-HostedRunner
    Assert-Condition $ownsFixture 'Fixture ownership hasn''t been established.'
    Assert-Unlinked $File
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($File))
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    Assert-Condition ($null -ne $process) 'The owned process didn''t start.'
    $ownedProcesses.Add($process)
    return $process
}

function Wait-Owned([Diagnostics.Process]$Process, [int]$Seconds) {
    if (-not $Process.WaitForExit($Seconds * 1000)) {
        Stop-OwnedTree $Process
        throw 'An owned fixture process exceeded its bounded deadline.'
    }
    return $Process.ExitCode
}

function Stop-OwnedTree([Diagnostics.Process]$Process) {
    Assert-HostedRunner
    $Process.Refresh()
    if (-not $Process.HasExited) {
        $Process.Kill($true)
        Assert-Condition ($Process.WaitForExit(10000)) 'The owned process tree didn''t confirm exit; later writes and cleanup are blocked.'
    }
}

function Get-App {
    $found = @(Get-Process NetLights -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $app })
    Assert-Condition ($found.Count -eq 1) 'Expected one owned installed application process.'
    return $found[0]
}

function Wait-App {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(25)
    do {
        $found = @(Get-Process NetLights -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $app })
        if ($found.Count -eq 1) { return $found[0] }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The installed application didn''t restart.'
}

function Assert-Live([Diagnostics.Process]$Process) {
    $expectedId = $Process.Id
    Start-Sleep -Seconds 4
    $Process.Refresh()
    Assert-Condition (-not $Process.HasExited -and (Get-App).Id -eq $expectedId) 'The installed application didn''t remain alive with the same PID.'
}

function Stop-App([switch]$CleanupOnly) {
    $found = @(Get-Process NetLights -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $app })
    if (-not $found.Count) { return }
    Assert-Condition ($found.Count -eq 1) 'Several application owners exist.'
    $parent = $found[0]
    $command = Start-Owned $app @('--exit-local')
    $null = Wait-Owned $command 10
    if (-not $parent.WaitForExit(30000)) {
        if (-not $CleanupOnly) { throw 'The exact installed parent didn''t exit gracefully.' }
        Stop-OwnedTree $parent
    }
}

function Get-RegistrySnapshot {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($productKey)
    Assert-Condition ($null -ne $key) 'The installed product registration is absent.'
    try {
        Assert-Condition ([string]$key.GetValue('Inno Setup: App Path') -ieq $install) 'The product registration belongs to another installation.'
        $values = @($key.GetValueNames() | Sort-Object)
        Assert-Condition ($values.Count -le 100 -and $key.SubKeyCount -eq 0) 'Unexpected product registration shape.'
        return @($values | ForEach-Object {
            [ordered]@{ name = $_; kind = $key.GetValueKind($_).ToString(); value = $key.GetValue($_,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
        })
    } finally { $key.Dispose() }
}

function Get-InstallationState {
    Assert-Unlinked $install
    $entries = @(Get-ChildItem -LiteralPath $install -Recurse -Force)
    Assert-Condition ($entries.Count -le 1512) 'Installed fixture exceeds the snapshot entry bound.'
    foreach ($entry in $entries) {
        Assert-Condition (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Linked installation entries aren''t accepted.'
    }
    $files = @($entries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName)
    Assert-Condition ($files.Count -le 512) 'Installed fixture exceeds the snapshot file bound.'
    $registration = @(Get-RegistrySnapshot)
    $group = @($registration | Where-Object { $_.name -ceq 'Inno Setup: Icon Group' })
    Assert-Condition ($group.Count -eq 1 -and $group[0].value -ceq 'Net Lights') 'Unexpected owned Start Menu group.'
    $shortcut = Join-Path $programGroup 'Net Lights.lnk'
    Assert-Unlinked $shortcut
    Assert-Condition (Test-Path -LiteralPath $shortcut -PathType Leaf) 'The installed shortcut is absent.'
    $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run')
    try {
        $runValue = if ($runKey -and 'NetLights' -in $runKey.GetValueNames()) { $runKey.GetValue('NetLights') } else { $null }
    } finally { if ($runKey) { $runKey.Dispose() } }
    return [ordered]@{
        files = @($files | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($install.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash } })
        registration = $registration; runValue = $runValue
        shortcut = $shortcut; shortcutSha256 = (Get-FileHash -LiteralPath $shortcut).Hash
    }
}

function New-Seed {
    $start = [DateTimeOffset]::UtcNow.AddMinutes(-30)
    $settings = [ordered]@{
        settingsVersion = 8; autoStart = $false; autoUpdateEnabled = $false
        geoCountryDetectionEnabled = $false; geoCountryIconEnabled = $false
        availabilityPollingSeconds = 5; countryPollingSeconds = 10
        windowWidth = 1234; windowHeight = 987; emphasizeShortStatuses = $false
    }
    $stays = @(
        @{ iso = 'DE'; startedUtc = $start.AddMinutes(14).ToString('o'); endedUtc = $start.AddMinutes(15).ToString('o') },
        @{ iso = 'PL'; startedUtc = $start.AddMinutes(17).ToString('o'); endedUtc = $start.AddMinutes(18).ToString('o') }
    )
    $observations = @()
    $settingFields = @('windowWidth','windowHeight','availabilityPollingSeconds','countryPollingSeconds','emphasizeShortStatuses','autoUpdateEnabled')
    if ($ReleaseTag -ceq 'v1.6.0') {
        $stays += @{ iso = 'RU'; startedUtc = $start.AddMinutes(20).ToString('o'); endedUtc = $start.AddMinutes(20).ToString('o') }
        foreach ($index in 0..1) {
            $iso = if ($index -eq 0) { 'DE' } else { 'PL' }
            $observations += @{
                observedUtc = $start.AddMinutes(14 + $index * 3).ToString('o'); observedUntilUtc = $start.AddMinutes(15 + $index * 3).ToString('o')
                publicIp = ('192.0.2.' + ($index + 1)); publicIpHash = ('a' * 64); countryCode = $iso; localCountry = 'FR'; onlineCountry = $iso; thirdCountry = $iso
                databaseVersion = 'recorded-v2fly-v1'; iplocateCountry = 'NL'; iplocateDatabaseVersion = 'recorded-iplocate-v1'
                resolutionSource = 'country.is'; confirmed = $true; publicIpChanged = ($index -eq 1); countryChanged = ($index -eq 1); classificationChanged = $false
            }
        }
    } else {
        $settings.settingsVersion = 4
        $settings.Remove('availabilityPollingSeconds'); $settings.Remove('countryPollingSeconds')
        $settingFields = @('windowWidth','windowHeight','emphasizeShortStatuses','autoUpdateEnabled')
    }
    $spans = @(
        @{ group = 'Ru'; state = 'Online'; paused = $false; startedUtc = $start.ToString('o'); endedUtc = $start.AddMinutes(5).ToString('o') },
        @{ group = 'Ru'; state = 'Unknown'; paused = $false; startedUtc = $start.AddMinutes(6).ToString('o'); endedUtc = $start.AddMinutes(7).ToString('o') },
        @{ group = 'World'; state = 'Offline'; paused = $false; startedUtc = $start.ToString('o'); endedUtc = $start.AddMinutes(3).ToString('o') },
        @{ group = 'World'; state = 'Online'; paused = $true; startedUtc = $start.AddMinutes(4).ToString('o'); endedUtc = $start.AddMinutes(5).ToString('o') }
    )
    return @{ settings = $settings; settingFields = $settingFields; stays = $stays; observations = $observations; spans = $spans; lastObservedUtc = $start.AddMinutes(20).ToString('o') }
}

function Write-Seed {
    Assert-HostedRunner
    $script:seed = New-Seed
    $null = New-Item -ItemType Directory -Path $updates -Force
    $locations = @{ version = $(if ($ReleaseTag -ceq 'v1.6.0') { 3 } else { 1 }); lastObservedUtc = $seed.lastObservedUtc; stays = $seed.stays }
    if ($ReleaseTag -ceq 'v1.6.0') { $locations.observations = $seed.observations }
    $endpoints = @(foreach ($group in @('Ru','World')) { foreach ($index in 0..6) {
        @{ id = ($group + '-' + $index); group = $group; uri = ('https://127.0.0.1:1/' + $group + '/' + $index); infrastructureId = ('local-' + $group + '-' + $index) }
    } })
    foreach ($entry in @(
        @('settings.json',$seed.settings), @('location-history.json',$locations),
        @('availability-history.json',@{ lastObservedUtc = $seed.lastObservedUtc; spans = $seed.spans }), @('endpoints.json',$endpoints)
    )) {
        [IO.File]::WriteAllText((Join-Path $data $entry[0]),($entry[1] | ConvertTo-Json -Depth 18),$utf8)
    }
}

function Assert-Seed {
    $settings = Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
    $locations = Get-Content -LiteralPath (Join-Path $data 'location-history.json') -Raw | ConvertFrom-Json
    $availability = Get-Content -LiteralPath (Join-Path $data 'availability-history.json') -Raw | ConvertFrom-Json
    Assert-SeedRecords $seed $settings $locations $availability
}

function Install-Baseline {
    Write-Seed
    $process = Start-Owned $BaselineInstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR=' + $install))
    Assert-Condition ((Wait-Owned $process 120) -eq 0) 'The official baseline installer failed.'
    $owner = Wait-App
    Assert-Live $owner
    Assert-Condition ((Read-Version $app) -ceq $baselineVersion) 'The installed baseline executable has the wrong version.'
    $agent = Join-Path $install 'NetLights.UpdateAgent.exe'
    Assert-Condition ((Read-Version $agent) -ceq $baselineVersion) 'The installed old agent has the wrong version.'
    return (Get-FileHash -LiteralPath $agent).Hash
}

function Invoke-InstalledAgent([string]$Setup, [string]$ExpectedAgentHash, [string]$ExpectedAgentVersion, [string]$ExpectedSetupHash) {
    Assert-HostedRunner
    Assert-ExactFile $Setup $ExpectedSetupHash
    $parent = Get-App
    $stage = Join-Path $updates ([Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $stage
    $stagedAgent = Join-Path $stage 'NetLights.UpdateAgent.exe'
    Copy-Item -LiteralPath (Join-Path $install 'NetLights.UpdateAgent.exe') -Destination $stagedAgent
    Assert-ExactFile $stagedAgent $ExpectedAgentHash 160MB
    Assert-Condition ((Read-Version $stagedAgent) -ceq $ExpectedAgentVersion) 'The staged agent isn''t the exact installed baseline or bridge agent.'
    $pending = Join-Path $stage ([IO.Path]::GetFileName($Setup))
    Copy-Item -LiteralPath $Setup -Destination $pending
    $digest = $ExpectedSetupHash
    Assert-ExactFile $pending $digest
    $requiresReady = $ExpectedAgentVersion -in @('1.6.0','1.6.1')
    $ready = $null
    $arguments = @('--parent',[string]$parent.Id,'--pending',$pending,'--sha256',$digest.ToLowerInvariant(),'--restart',$app)
    if ($requiresReady) {
        $readyName = 'Local\NetLights.UpdateAgentReady.' + [Guid]::NewGuid().ToString('N')
        $ready = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$readyName)
        $arguments += @('--ready',$readyName)
    }
    $statusPath = Join-Path $updates 'result.json'
    if ([IO.File]::Exists($statusPath)) {
        Assert-Unlinked $statusPath
        [IO.File]::Move($statusPath,(Join-Path $results ('previous-result-' + [Guid]::NewGuid().ToString('N') + '.json')))
    }
    $guardPath = Join-Path $updates 'failed-candidate.json'
    $previousGuard = if ([IO.File]::Exists($guardPath)) {
        Assert-Condition ((Get-Item -LiteralPath $guardPath).Length -le 4096) 'The previous retry guard exceeds its bound.'
        Get-Content -LiteralPath $guardPath -Raw | ConvertFrom-Json
    } else { $null }
    $started = [DateTimeOffset]::UtcNow
    $agent = $null
    try {
        $agent = Start-Owned $stagedAgent $arguments
        if ($requiresReady) {
            Assert-Condition ($ready.WaitOne(5000) -and -not $agent.HasExited) 'The real installed agent didn''t acknowledge ready.'
        } else {
            Start-Sleep -Milliseconds 250
            Assert-Condition (-not $agent.HasExited) 'The historical agent exited before parent handoff.'
        }
        $parent.Refresh()
        Assert-Condition (-not $parent.HasExited) 'The parent exited before handoff acknowledgement.'
        $exit = Start-Owned $app @('--exit-local')
        $null = Wait-Owned $exit 10
        Assert-Condition ($parent.WaitForExit(30000)) 'The exact handoff parent didn''t exit.'
        $code = Wait-Owned $agent 720
        $restarted = Wait-App
        Assert-Live $restarted
        Assert-Condition ([IO.File]::Exists($statusPath) -and (Get-Item -LiteralPath $statusPath).Length -le 64KB) 'This attempt didn''t write a bounded new result file.'
        $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
        Assert-AttemptResult $status ([IO.Path]::GetFileName($pending)) $started ([DateTimeOffset]::UtcNow)
        return [ordered]@{
            exitCode = $code; success = [bool]$status.success; setupSha256 = $digest; agentSha256 = $ExpectedAgentHash
            agentVersion = $ExpectedAgentVersion; ready = $(if ($requiresReady) { 'PASS' } else { 'NOT SUPPORTED by this release' })
            exactParentExited = $true; recoveryPid = $restarted.Id; stablePidSeconds = 4
            agentHealthProtocol = $(if ($requiresReady) { 'monitor signal plus 3-second agent liveness' } else { 'NOT SUPPORTED by this release' })
            recoveredVersion = Read-Version $app
            startedUtc = $started.ToString('o'); recoveryStartedUtc = $restarted.StartTime.ToUniversalTime().ToString('o')
            previousRetry = $previousGuard; resultUtc = $status.utc
        }
    } finally {
        if ($ready) { $ready.Dispose() }
        $parent.Dispose()
        if ($agent -and -not $agent.HasExited) {
            Stop-OwnedTree $agent
        }
    }
}

function Assert-Native([object]$Attempt) {
    Assert-Condition ($Attempt.exitCode -eq 0 -and $Attempt.success) 'The installed agent didn''t report native upgrade success.'
    Assert-ExactFile $app $native.appSha256 160MB
    Assert-ExactFile (Join-Path $install 'NetLights.UpdateAgent.exe') $native.agentSha256 160MB
    Assert-Condition ((Read-Version $app) -ceq '1.7.0') 'The installed native version isn''t 1.7.0.'
    $registration = @(Get-RegistrySnapshot)
    $displayVersion = @($registration | Where-Object { $_.name -ceq 'DisplayVersion' })
    Assert-Condition ($displayVersion.Count -eq 1 -and $displayVersion[0].value -ceq '1.7.0') 'The successful upgrade didn''t register native 1.7.0.'
    Stop-App
    Assert-Seed
    # For pre-ready agents this is explicitly an external verification restart,
    # not a claim that the historical agent implemented monitor confirmation.
    $healthName = 'Local\NetLights.UpdateHealth.' + [Guid]::NewGuid().ToString('N')
    $health = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$healthName)
    try {
        $owner = Start-Owned $app @('--update-health-event',$healthName)
        Assert-Condition ($health.WaitOne(25000)) 'Native monitoring didn''t acknowledge the external health restart.'
        Assert-Live $owner
    } finally { $health.Dispose() }
    Stop-App
    Assert-Seed
    return @{ externalNativeMonitorSignal = 'PASS'; stablePidSeconds = 4; exactAppAndAgentDigests = 'PASS'; settingsAndBothHistories = 'PASS' }
}

function New-FaultInstaller {
    $fixtureDirectory = Join-Path $results 'fixtures'
    if (-not [IO.Directory]::Exists($fixtureDirectory)) { $null = New-Item -ItemType Directory -Path $fixtureDirectory }
    $output = Join-Path $fixtureDirectory 'NetLights-Setup-win-x64-1.7.0.exe'
    if ([IO.File]::Exists($output)) {
        Assert-ExactFile $output $script:faultSha
        return $output
    }
    $source = @'
using System; using System.IO; using System.Reflection; using System.Threading;
[assembly: AssemblyVersion("1.7.0.0")]
[assembly: AssemblyFileVersion("1.7.0.0")]
[assembly: AssemblyInformationalVersion("1.7.0")]
public static class Fault {
 public static int Main(string[] args) {
  if(!String.Equals(Environment.UserName,"runneradmin",StringComparison.OrdinalIgnoreCase)
   || Environment.GetEnvironmentVariable("GITHUB_ACTIONS")!="true"
   || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT")!="github-hosted"
   || Environment.GetEnvironmentVariable("CI")!="true") return 90;
  string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","NetLights");
  if(!File.Exists(Path.Combine(root,"unins000.exe"))) return 90;
  if(args.Length>0 && args[0].StartsWith("/",StringComparison.Ordinal)) {
   string mode=Environment.GetEnvironmentVariable("NL_CI_UPGRADE_FAULT");
   if(mode=="partial") {File.WriteAllText(Path.Combine(root,"NetLights.exe"),"partial installer bytes");File.WriteAllText(Path.Combine(root,"added-fixture.bin"),"added");return 17;}
   if(mode=="health") {File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(root,"NetLights.exe"),true);return 0;}
   return 91;
  }
  Thread.Sleep(TimeSpan.FromMinutes(5)); return 0;
 }
}
'@
    $sourcePath = Join-Path $fixtureDirectory 'fault.cs'
    $compilePath = Join-Path $fixtureDirectory 'compile.ps1'
    [IO.File]::WriteAllText($sourcePath,$source,$utf8)
    [IO.File]::WriteAllText($compilePath,'param([string]$Source,[string]$Output); $ErrorActionPreference="Stop"; Add-Type -TypeDefinition ([IO.File]::ReadAllText($Source)) -OutputAssembly $Output -OutputType ConsoleApplication',$utf8)
    $compiler = Start-Owned (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$compilePath,'-Source',$sourcePath,'-Output',$output)
    Assert-Condition ((Wait-Owned $compiler 60) -eq 0 -and (Read-Version $output) -ceq '1.7.0') 'The explicit .NET Framework failure fixture didn''t compile.'
    $script:faultSha = (Get-FileHash -LiteralPath $output).Hash
    return $output
}

function Assert-FailureSafety([object]$Attempt, [object]$Before, [int]$ExpectedCode, [string]$Version) {
    $checks = [ordered]@{}
    $errors = [Collections.Generic.List[string]]::new()
    foreach ($check in @('rejected','filesRegistryShortcutRestored','durableRetry','richDataPreserved','activeMarkerCleared')) {
        try {
            switch ($check) {
                'rejected' { Assert-Condition ($Attempt.exitCode -eq $ExpectedCode -and -not $Attempt.success) 'The agent accepted an intentional installer or health failure.' }
                'filesRegistryShortcutRestored' { Assert-ExactState $Before (Get-InstallationState) }
                'durableRetry' {
                    $guardPath = Join-Path $updates 'failed-candidate.json'
                    $guard = Get-Content -LiteralPath $guardPath -Raw | ConvertFrom-Json
                    Assert-FreshRetry $guard $Attempt.previousRetry $Version $Attempt.setupSha256 ([DateTimeOffset]$Attempt.startedUtc) ([DateTimeOffset](Get-Item -LiteralPath $guardPath).LastWriteTimeUtc) ([DateTimeOffset]$Attempt.recoveryStartedUtc) ([DateTimeOffset]::UtcNow)
                }
                'richDataPreserved' { Stop-App; Assert-Seed; $null = Start-Owned $app; $null = Wait-App }
                'activeMarkerCleared' { Assert-Condition (@(Get-ChildItem -LiteralPath $updates -Filter 'active-*.json' -File).Count -eq 0) 'A resolved update retained an active transaction marker.' }
            }
            $checks[$check] = 'PASS'
        } catch {
            $checks[$check] = 'FAIL'
            $errors.Add($check)
        }
    }
    return @{ checks = $checks; failedChecks = @($errors.ToArray()); allPassed = ($errors.Count -eq 0); attempt = $Attempt }
}

function Archive-Profile([string]$Name) {
    Assert-HostedRunner
    if (-not [IO.Directory]::Exists($data)) { return }
    $archive = Join-Path $results ($Name + '-profile.zip')
    $stream = [IO.File]::Open($archive,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $zip = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
    try {
        $total = 0L
        foreach ($relative in @('settings.json','location-history.json','availability-history.json','endpoints.json','updates/result.json','updates/failed-candidate.json')) {
            $path = Join-Path $data $relative
            if (-not [IO.File]::Exists($path)) { continue }
            Assert-Unlinked $path
            $bytes = (Get-Item -LiteralPath $path).Length
            $total += $bytes
            Assert-Condition ($bytes -le 32MB -and $total -le 96MB) 'Fixture archive exceeds its bound.'
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$path,$relative,[IO.Compression.CompressionLevel]::Fastest)
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
    Assert-Condition ((Get-Item -LiteralPath $archive).Length -gt 0) 'The diagnostic archive wasn''t written.'
}

function Remove-OwnedDirectory([string]$Path) {
    Assert-HostedRunner
    Assert-Condition ($ownsFixture -and $Path -in @($install,$data)) 'Refusing a cleanup target outside the two initially absent fixture directories.'
    if (-not [IO.Path]::Exists($Path)) { return }
    Assert-Unlinked $Path
    $entries = @(Get-ChildItem -LiteralPath $Path -Recurse -Force)
    Assert-Condition ($entries.Count -le 10000) 'Cleanup entry count exceeds the owned fixture bound.'
    $total = 0L
    foreach ($entry in $entries) {
        Assert-Condition (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'A linked fixture entry prevents recursive cleanup.'
        if (-not $entry.PSIsContainer) { $total += $entry.Length }
    }
    Assert-Condition ($total -le 2GB) 'Cleanup size exceeds the owned fixture bound.'
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Reset-OwnedFixture([string]$ArchiveName) {
    Assert-HostedRunner
    foreach ($process in $ownedProcesses) {
        $process.Refresh()
        if (-not $process.HasExited -and $process.ProcessName -ne 'NetLights') { Stop-OwnedTree $process }
    }
    try { Stop-App -CleanupOnly }
    catch {
        foreach ($owner in @(Get-Process NetLights -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $app })) {
            Stop-OwnedTree $owner
        }
    }
    Archive-Profile $ArchiveName
    if ([IO.Directory]::Exists($install)) {
        $null = Get-RegistrySnapshot
        $uninstall = Join-Path $install 'unins000.exe'
        Assert-Unlinked $uninstall
        $process = Start-Owned $uninstall @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
        Assert-Condition ((Wait-Owned $process 120) -eq 0) 'The owned installation uninstaller failed.'
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($productKey)
    try { Assert-Condition ($null -eq $key) 'Uninstall retained a product registration; it wasn''t manually deleted.' }
    finally { if ($key) { $key.Dispose() } }
    Remove-OwnedDirectory $install
    Remove-OwnedDirectory $data
    Assert-Condition (-not [IO.Path]::Exists($programGroup)) 'Uninstall retained a Start Menu group; unexpected entries weren''t deleted.'
    Assert-Condition (-not [IO.Path]::Exists($startupShortcut)) 'Uninstall retained its Startup shortcut; unexpected entries weren''t deleted.'
}

function Invoke-Case([string]$Name, [scriptblock]$Body) {
    $caseError = $null
    try { & $Body }
    catch {
        $caseError = 'The case failed its protocol or preservation assertion.'
        Add-Check ($Name + '-execution') 'FAIL' @{ error = $caseError; errorType = $_.Exception.GetType().Name }
    } finally {
        try { Reset-OwnedFixture $Name }
        catch {
            Add-Check ($Name + '-cleanup') 'FAIL' @{ error = 'Owned fixture cleanup failed; preserved remaining evidence and stopped further scenarios.'; errorType = $_.Exception.GetType().Name }
            throw
        }
    }
}

try {
    if ($Scenario -in @('All','Direct')) {
        Invoke-Case 'direct' {
            $oldSha = Install-Baseline
            $attempt = Invoke-InstalledAgent $native.installer $oldSha $baselineVersion $native.sha256
            $nativeEvidence = Assert-Native $attempt
            Add-Check 'published-baseline-direct-native-success' 'PASS' @{ attempt = $attempt; native = $nativeEvidence }
        }
    }
    if ($Scenario -in @('All','LegacyFailures')) {
        foreach ($mode in @('wrong-version','health')) {
            Invoke-Case ('legacy-' + $mode) {
                $oldSha = Install-Baseline
                $before = Get-InstallationState
                $setup = if ($mode -ceq 'wrong-version') {
                    $wrong = Join-Path $updates 'NetLights-Setup-win-x64-999.0.0.exe'
                    Copy-Item -LiteralPath $native.installer -Destination $wrong
                    $wrong
                } else { New-FaultInstaller }
                $oldFault = [Environment]::GetEnvironmentVariable('NL_CI_UPGRADE_FAULT','Process')
                try {
                    if ($mode -ceq 'health') { [Environment]::SetEnvironmentVariable('NL_CI_UPGRADE_FAULT','health','Process') }
                    $setupSha = if ($mode -ceq 'wrong-version') { $native.sha256 } else { $script:faultSha }
                    $attempt = Invoke-InstalledAgent $setup $oldSha $baselineVersion $setupSha
                } finally { [Environment]::SetEnvironmentVariable('NL_CI_UPGRADE_FAULT',$oldFault,'Process') }
                $version = if ($mode -ceq 'wrong-version') { '999.0.0' } else { '1.7.0' }
                $evidence = Assert-FailureSafety $attempt $before 8 $version
                $evidence['legacySafety'] = if ($evidence.allPassed) { 'PASS' } else { 'KNOWN LIMITATION - safety assertions failed; this remains a failing case' }
                Add-Check ('published-baseline-' + $mode + '-failure-safety') $(if ($evidence.allPassed) { 'PASS' } else { 'FAIL' }) $evidence
            }
        }
    }
    if ($Scenario -in @('All','Bridge')) {
        Invoke-Case 'bridge' {
            $oldSha = Install-Baseline
            $transition = Invoke-InstalledAgent $bridge.installer $oldSha $baselineVersion $bridge.sha256
            Assert-Condition ($transition.exitCode -eq 0 -and $transition.success) 'The actual old agent didn''t install bridge 1.6.1.'
            Assert-ExactFile $app $bridge.appSha256 160MB
            Assert-ExactFile (Join-Path $install 'NetLights.UpdateAgent.exe') $bridge.agentSha256 160MB
            Stop-App; Assert-Seed
            $null = Start-Owned $app; $null = Wait-App
            Add-Check 'published-old-agent-verified-bridge-success' 'PASS' $transition
            $before = Get-InstallationState
            foreach ($mode in @('partial','health','wrong-version')) {
                $setup = if ($mode -ceq 'wrong-version') {
                    $wrong = Join-Path $updates 'NetLights-Setup-win-x64-999.0.0.exe'
                    Copy-Item -LiteralPath $native.installer -Destination $wrong
                    $wrong
                } else { New-FaultInstaller }
                $oldFault = [Environment]::GetEnvironmentVariable('NL_CI_UPGRADE_FAULT','Process')
                try {
                    if ($mode -cne 'wrong-version') { [Environment]::SetEnvironmentVariable('NL_CI_UPGRADE_FAULT',$mode,'Process') }
                    $setupSha = if ($mode -ceq 'wrong-version') { $native.sha256 } else { $script:faultSha }
                    $attempt = Invoke-InstalledAgent $setup $bridge.agentSha256 '1.6.1' $setupSha
                } finally { [Environment]::SetEnvironmentVariable('NL_CI_UPGRADE_FAULT',$oldFault,'Process') }
                $expectedCode = if ($mode -ceq 'partial') { 17 } else { 8 }
                $version = if ($mode -ceq 'wrong-version') { '999.0.0' } else { '1.7.0' }
                $evidence = Assert-FailureSafety $attempt $before $expectedCode $version
                Add-Check ('bridge-' + $mode + '-failure-safety') $(if ($evidence.allPassed) { 'PASS' } else { 'FAIL' }) $evidence
                Assert-Condition $evidence.allPassed 'Bridge failure safety didn''t pass; later stages won''t consume an unproven recovery.'
            }
            $attempt = Invoke-InstalledAgent $native.installer $bridge.agentSha256 '1.6.1' $native.sha256
            $nativeEvidence = Assert-Native $attempt
            Add-Check 'verified-bridge-native-success' 'PASS' @{ attempt = $attempt; native = $nativeEvidence }
        }
    }
    $failures = @($result.checks | Where-Object { $_.status -ceq 'FAIL' })
    $result.cleanup = 'PASS'
    $result.status = if ($failures.Count -eq 0 -and $result.checks.Count -gt 0) { 'PASS' } else { 'FAIL' }
} finally {
    $result.completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    Save-Result
    foreach ($process in $ownedProcesses) { $process.Dispose() }
}
Assert-Condition ($result.status -ceq 'PASS') 'Upgrade matrix has failing safety assertions; inspect result.json. Known legacy limitations aren''t converted to PASS.'
Write-Output ('RUN - PASS: real hosted-VM upgrade scenario ' + $Scenario + '. Results: ' + $results)
