param([string]$BaselineInstaller, [string]$CandidateInstaller, [string]$AgentPath,
    [string]$CandidatePublishDirectory,
    [string]$ResultsDirectory = ('TestResults/sandbox-' + [Guid]::NewGuid().ToString('N')), [switch]$Guest, [string]$GuestNonce,
    [switch]$SelfTest)

$ErrorActionPreference = 'Stop'
function Assert-CandidateBinaries([string]$Application, [string]$Agent, [string]$ExpectedAppSha256, [string]$ExpectedAgentSha256) {
    foreach ($expected in @($ExpectedAppSha256,$ExpectedAgentSha256)) {
        if ($expected -cnotmatch '^[a-fA-F0-9]{64}$') { throw 'Verified publish executable hashes are required.' }
    }
    if ((Get-FileHash -LiteralPath $Application -Algorithm SHA256).Hash -ine $ExpectedAppSha256 -or
        (Get-FileHash -LiteralPath $Agent -Algorithm SHA256).Hash -ine $ExpectedAgentSha256) {
        throw 'Installed application or agent differs from the verified published binaries.'
    }
}
if ($SelfTest) {
    if ($Guest -or $BaselineInstaller -or $CandidateInstaller -or $AgentPath -or $CandidatePublishDirectory) { throw 'SelfTest cannot be combined with an installation scenario.' }
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $fixtureRoot = Join-Path $repositoryRoot ('artifacts/sandbox-hash-controls-' + [Guid]::NewGuid().ToString('N'))
    $fixture = Join-Path $fixtureRoot 'fixture'
    New-Item -ItemType Directory -Path $fixture | Out-Null
    # Copy, but never execute, the current real PE. An appended overlay changes
    # its bytes while preserving ProductVersion, reproducing a stale-version gate.
    $source = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $app = Join-Path $fixture 'NetLights.exe'; $agent = Join-Path $fixture 'NetLights.UpdateAgent.exe'
    Copy-Item -LiteralPath $source -Destination $app
    Copy-Item -LiteralPath $source -Destination $agent
    $appHash = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
    $agentHash = (Get-FileHash -LiteralPath $agent -Algorithm SHA256).Hash
    Assert-CandidateBinaries $app $agent $appHash $agentHash
    $versionBefore = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion
    if (-not $versionBefore) { throw 'The real PE control has no ProductVersion.' }
    $stream = [IO.File]::Open($app,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $bytes=[Text.Encoding]::UTF8.GetBytes('different-same-version-binary'); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion -cne $versionBefore) { throw 'The negative control changed ProductVersion.' }
    $rejected = $false
    try { Assert-CandidateBinaries $app $agent $appHash $agentHash } catch { if ($_.Exception.Message -cne 'Installed application or agent differs from the verified published binaries.') { throw }; $rejected=$true }
    if (-not $rejected) { throw 'A different application with the same ProductVersion passed.' }
    Copy-Item -LiteralPath $source -Destination $app -Force
    $agentVersionBefore = [Diagnostics.FileVersionInfo]::GetVersionInfo($agent).ProductVersion
    $stream = [IO.File]::Open($agent,[IO.FileMode]::Append,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try { $stream.WriteByte(42) } finally { $stream.Dispose() }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($agent).ProductVersion -cne $agentVersionBefore) { throw 'The agent negative control changed ProductVersion.' }
    $rejected = $false
    try { Assert-CandidateBinaries $app $agent $appHash $agentHash } catch { if ($_.Exception.Message -cne 'Installed application or agent differs from the verified published binaries.') { throw }; $rejected=$true }
    if (-not $rejected) { throw 'A different agent with the same ProductVersion passed.' }
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'result.json'),(@{status='PASS';matchingBinaries=$true;sameVersionWrongAppRejected=$true;sameVersionWrongAgentRejected=$true;executedCandidate=$false}|ConvertTo-Json))
    Copy-Item -LiteralPath (Join-Path $fixtureRoot 'result.json') -Destination (Join-Path $fixture 'result.json')
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $fixture 'check.ps1')
    $archive = Join-Path $fixtureRoot 'evidence.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($fixture,$archive)
    if ((Get-Item -LiteralPath $archive).Length -le 0) { throw 'The diagnostic archive is empty.' }
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if ($resolvedFixture -cne (Join-Path $fixtureRoot 'fixture') -or [IO.Path]::GetFileName($fixtureRoot) -cnotmatch '^sandbox-hash-controls-[a-f0-9]{32}$') { throw 'Unexpected fixture path; retained.' }
    [IO.Directory]::Delete($resolvedFixture,$true)
    Write-Output 'RUN - PASS: exact published app and agent hashes; same-version wrong-PE controls rejected. No Sandbox or candidate process was started.'
    return
}
if ($Guest) {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (($identity.Name -split '\\')[-1] -cne 'WDAGUtilityAccount' -or -not $identity.User -or
        -not (Test-Path -LiteralPath 'C:\NLInput\input.json' -PathType Leaf) -or
        -not (Test-Path -LiteralPath 'C:\NLResult' -PathType Container)) { throw 'Guest mode requires the real Windows Sandbox account and mapped test directories.' }
    $config = Get-Content -LiteralPath 'C:\NLInput\input.json' -Raw | ConvertFrom-Json
    if ($GuestNonce -cnotmatch '^[a-f0-9]{32}$' -or $GuestNonce -cne $config.guestNonce) { throw 'Guest command does not match this prepared Sandbox.' }
    $result = [ordered]@{status='FAIL'; steps=@(); startedUtc=[DateTimeOffset]::UtcNow.ToString('o')}
    $install = Join-Path $env:LOCALAPPDATA 'Programs\NetLights'
    $data = Join-Path $env:LOCALAPPDATA 'NetLights'
    $updates = Join-Path $data 'updates'
    $app = Join-Path $install 'NetLights.exe'
    function Stop-TestApplication {
        if (-not (Test-Path $app)) { return }
        $command = Start-Process -FilePath $app -ArgumentList '--exit-local' -PassThru -WindowStyle Hidden
        if (-not $command.WaitForExit(10000)) { throw 'Exit command timed out.' }
        $until = [DateTimeOffset]::UtcNow.AddSeconds(25)
        do {
            $running = @(Get-Process NetLights -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $app })
            if (-not $running.Count) { return }
            Start-Sleep -Milliseconds 100
        } while ([DateTimeOffset]::UtcNow -lt $until)
        throw 'Installed application did not exit gracefully.'
    }
    function Invoke-TestAgent([string]$setup) {
        $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
        $agent = Start-Process -FilePath 'C:\NLInput\NetLights.UpdateAgent.exe' -ArgumentList @('--parent','-123','--pending',$setup,'--sha256',$hash,'--restart',$app) -WindowStyle Hidden -PassThru
        if (-not $agent.WaitForExit(240000)) { throw 'Agent exceeded four minutes.' }
        $agent.Refresh(); return $agent.ExitCode
    }
    function Test-ExplorerRecovery {
        # This destructive shell check is available only after the Guest identity guard above.
        Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SandboxTray {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct IconId { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    public delegate bool Visitor(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(Visitor visitor, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int size);
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconId icon,out Rect rect);
    public static uint ShellProcess() { uint id; GetWindowThreadProcessId(GetShellWindow(),out id); return id; }
    public static bool BothIcons(uint process) {
        IntPtr owner=IntPtr.Zero;
        Visitor visitor=(window,data)=>{uint id; GetWindowThreadProcessId(window,out id); if(id==process) {var text=new StringBuilder(256); GetClassName(window,text,256); if(text.ToString().StartsWith("NetLights.NativeTray.",StringComparison.Ordinal)) {owner=window;return false;}} return true;};
        EnumWindows(visitor,IntPtr.Zero); GC.KeepAlive(visitor);
        if(owner==IntPtr.Zero) return false;
        foreach(uint id in new uint[]{1,2}) { var icon=new IconId {Size=(uint)Marshal.SizeOf<IconId>(),Window=owner,Id=id}; Rect rect; if(Shell_NotifyIconGetRect(ref icon,out rect)!=0 || rect.Right<=rect.Left || rect.Bottom<=rect.Top) return false; }
        return true;
    }
}
'@
        Stop-TestApplication
        $current = Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
        $current | Add-Member -NotePropertyName geoCountryDetectionEnabled -NotePropertyValue $true -Force
        $current | Add-Member -NotePropertyName geoCountryIconEnabled -NotePropertyValue $true -Force
        [IO.File]::WriteAllText((Join-Path $data 'settings.json'),($current | ConvertTo-Json -Depth 20),$utf8)
        $process = Start-Process -FilePath $app -PassThru -WindowStyle Hidden
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(25)
        while (-not [SandboxTray]::BothIcons($process.Id)) {
            if ($process.HasExited -or [DateTimeOffset]::UtcNow -ge $deadline) { throw 'Both owned tray icons were not present before the shell restart.' }
            Start-Sleep -Milliseconds 100
        }
        $shellProcess = [SandboxTray]::ShellProcess()
        if (-not $shellProcess) { throw 'Sandbox shell process was not identified.' }
        $shell = Get-Process -Id $shellProcess -ErrorAction Stop
        if ($shell.Path -ine (Join-Path $env:WINDIR 'explorer.exe') -or $shell.SessionId -ne $process.SessionId) { throw 'Refusing to restart an unowned or foreign shell.' }
        $restartUtc = [DateTimeOffset]::UtcNow
        $shell.Kill()
        if (-not $shell.WaitForExit(5000)) { throw 'Owned Sandbox shell did not exit.' }
        Start-Sleep -Seconds 3
        if (-not [SandboxTray]::ShellProcess()) { Start-Process -FilePath "$env:WINDIR\explorer.exe" -WindowStyle Hidden }
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) { throw 'Net Lights exited during the real Sandbox Explorer restart.' }
            if ([SandboxTray]::ShellProcess() -and [SandboxTray]::ShellProcess() -ne $shellProcess -and [SandboxTray]::BothIcons($process.Id)) {
                try {
                    $history = Get-Content -LiteralPath (Join-Path $data 'availability-history.json') -Raw | ConvertFrom-Json
                    if ([DateTimeOffset]$history.lastObservedUtc -gt $restartUtc) {
                        return @{name='real-explorer-restart-both-icons-same-process-new-history';status='PASS';pid=$process.Id}
                    }
                } catch { }
            }
            Start-Sleep -Milliseconds 200
        }
        throw 'Both tray icons did not return after the real Sandbox Explorer restart.'
    }
    try {
        Start-Transcript -LiteralPath 'C:\NLResult\guest.log' | Out-Null
        New-Item -ItemType Directory -Path $updates -Force | Out-Null
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        $seedStart = [DateTimeOffset]::UtcNow.AddMinutes(-10).ToString('o')
        $seedEnd = [DateTimeOffset]::UtcNow.AddMinutes(-9).ToString('o')
        [IO.File]::WriteAllText((Join-Path $data 'settings.json'), '{"settingsVersion":8,"autoStart":false,"autoUpdateEnabled":false,"geoCountryDetectionEnabled":false,"windowWidth":1234,"windowHeight":987}', $utf8)
        [IO.File]::WriteAllText((Join-Path $data 'location-history.json'), (@{version=3; lastObservedUtc=$seedEnd; stays=@(@{iso='AQ'; startedUtc=$seedStart; endedUtc=$seedEnd}); observations=@()} | ConvertTo-Json -Depth 6),$utf8)
        [IO.File]::WriteAllText((Join-Path $data 'availability-history.json'), (@{lastObservedUtc=$seedEnd; spans=@(@{group='World'; state='Offline'; paused=$false; startedUtc=$seedStart; endedUtc=$seedEnd})} | ConvertTo-Json -Depth 6),$utf8)
        $baseline = Join-Path 'C:\NLInput' $config.baseline
        $candidate = Join-Path 'C:\NLInput' $config.candidate
        if ((Get-FileHash -LiteralPath $baseline -Algorithm SHA256).Hash -ne $config.baselineSha256 -or
            (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $config.candidateSha256 -or
            (Get-FileHash -LiteralPath 'C:\NLInput\NetLights.UpdateAgent.exe' -Algorithm SHA256).Hash -ne $config.agentSha256) { throw 'Sandbox inputs do not match the prepared hashes.' }
        $setup = Start-Process -FilePath $baseline -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=$install") -WindowStyle Hidden -PassThru
        if (-not $setup.WaitForExit(120000) -or $setup.ExitCode -ne 0) { throw 'Baseline Inno install failed.' }
        Stop-TestApplication
        $oldHash = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
        $oldVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion
        if (($oldVersion -split '\+')[0] -ne $config.baselineVersion) { throw 'Installed baseline version does not match its input.' }
        $result.steps += @{name='baseline'; status='PASS'; version=$oldVersion}
        # Real partial install followed by an expected version mismatch, without changing source versions.
        $wrong = Join-Path $updates 'NetLights-Setup-win-x64-999.0.0.exe'
        Copy-Item -LiteralPath $candidate -Destination $wrong
        if ((Invoke-TestAgent $wrong) -ne 8) { throw 'Expected version-mismatch failure was not detected.' }
        if ((Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash -ne $oldHash) { throw 'Rollback did not restore exact baseline bytes.' }
        if (-not @(Get-Process NetLights -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $app }).Count) { throw 'Rollback did not restart baseline.' }
        $result.steps += @{name='partial-install-file-rollback'; status='PASS'}
        Stop-TestApplication
        $registry = @(Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.InstallLocation -and $_.InstallLocation.TrimEnd('\') -eq $install.TrimEnd('\') })
        $result.rollbackRegistryVersions = @($registry | ForEach-Object { $_.DisplayVersion })
        $result.rollbackFileVersion = $oldVersion
        $result.rollbackRegistryConsistent = $registry.Count -eq 1 -and
            $registry[0].DisplayVersion -eq ($oldVersion -split '\+')[0]
        $pending = Join-Path $updates $config.candidateName
        Copy-Item -LiteralPath $candidate -Destination $pending
        if ((Invoke-TestAgent $pending) -ne 0) { throw 'Real Inno update failed.' }
        $status = Get-Content (Join-Path $updates 'result.json') -Raw | ConvertFrom-Json
        if (-not $status.success) { throw 'Agent did not confirm monitor readiness.' }
        Start-Sleep -Seconds 10
        if (-not @(Get-Process NetLights -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $app }).Count) { throw 'Updated process exited after readiness.' }
        if (([Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion -split '\+')[0] -ne $config.candidateVersion) { throw 'Installed candidate version does not match its input.' }
        $result.steps += @{name='real-inno-update-and-monitor'; status='PASS'; version=[Diagnostics.FileVersionInfo]::GetVersionInfo($app).ProductVersion}
        Assert-CandidateBinaries $app (Join-Path $install 'NetLights.UpdateAgent.exe') $config.publishedAppSha256 $config.publishedAgentSha256
        $result.steps += @{name='exact-installed-published-binaries';status='PASS';appSha256=$config.publishedAppSha256;agentSha256=$config.publishedAgentSha256}
        $result.steps += Test-ExplorerRecovery
        Stop-TestApplication
        $settings = Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
        $locations = Get-Content (Join-Path $data 'location-history.json') -Raw | ConvertFrom-Json
        $availability = Get-Content (Join-Path $data 'availability-history.json') -Raw | ConvertFrom-Json
        if ($settings.windowWidth -ne 1234) { throw 'Settings were lost.' }
        if (-not @($locations.stays | Where-Object { $_.iso -eq 'AQ' -and [DateTimeOffset]$_.startedUtc -eq [DateTimeOffset]$seedStart }).Count) { throw 'Seeded location history was lost.' }
        if (-not @($availability.spans | Where-Object { $_.group -eq 'World' -and [DateTimeOffset]$_.startedUtc -eq [DateTimeOffset]$seedStart }).Count) { throw 'Seeded availability history was lost.' }
        $result.steps += @{name='settings-and-both-nonempty-histories'; status='PASS'}
        if (-not $result.rollbackRegistryConsistent) { throw 'Rollback left the Inno uninstall registration inconsistent with the restored executable.' }
        $result.status = 'PASS'
    }
    catch { $result.error = $_.Exception.Message }
    finally {
        $result.completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $serialized = $result | ConvertTo-Json -Depth 8
        [IO.File]::WriteAllText('C:\NLResult\result.json.tmp', $serialized, (New-Object System.Text.UTF8Encoding($false)))
        [IO.File]::Move('C:\NLResult\result.json.tmp', 'C:\NLResult\result.json')
        try { Stop-Transcript | Out-Null } catch { }
        Start-Process -FilePath "$env:WINDIR\System32\shutdown.exe" -ArgumentList @('/s','/t','0') -WindowStyle Hidden
    }
    return
}

if (-not $BaselineInstaller -or -not $CandidateInstaller -or -not $AgentPath -or -not $CandidatePublishDirectory) { throw 'Supply baseline installer, candidate installer, candidate agent and the verified CandidatePublishDirectory.' }
$publishedApp = Join-Path ([IO.Path]::GetFullPath($CandidatePublishDirectory)) 'NetLights.exe'
$publishedAgent = Join-Path ([IO.Path]::GetFullPath($CandidatePublishDirectory)) 'NetLights.UpdateAgent.exe'
foreach ($file in @($publishedApp,$publishedAgent,$AgentPath)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'A verified published executable is missing.' } }
$publishedAppHash = (Get-FileHash -LiteralPath $publishedApp -Algorithm SHA256).Hash
$publishedAgentHash = (Get-FileHash -LiteralPath $publishedAgent -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath $AgentPath -Algorithm SHA256).Hash -ine $publishedAgentHash) { throw 'The supplied agent differs from the verified publish.' }
$wsbCommand = Get-Command wsb.exe -CommandType Application -ErrorAction Stop
# https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli
function Invoke-SandboxCli([string[]]$CliArguments, [int]$TimeoutSeconds = 60) {
    $start = [Diagnostics.ProcessStartInfo]::new($wsbCommand.Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $CliArguments) { $start.ArgumentList.Add($argument) }
    $command = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $command.StandardOutput.ReadToEndAsync()
        $stderr = $command.StandardError.ReadToEndAsync()
        if (-not $command.WaitForExit($TimeoutSeconds * 1000)) {
            # Kill only this CLI client, never a shared Sandbox service process.
            if (-not $command.HasExited) { $command.Kill(); $command.WaitForExit(5000) | Out-Null }
            throw 'Windows Sandbox CLI exceeded its deadline.'
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        if ($output.Length -gt 65536 -or $errorOutput.Length -gt 65536) { throw 'Unexpectedly large Windows Sandbox CLI output.' }
        if ($command.ExitCode -ne 0) { throw "Windows Sandbox CLI failed with exit $($command.ExitCode): $($errorOutput.Substring(0,[Math]::Min(2048,$errorOutput.Length)))" }
        return $output
    }
    finally { $command.Dispose() }
}
function Get-SandboxIds {
    $inventory = Invoke-SandboxCli -CliArguments @('list','--raw') | ConvertFrom-Json
    if (-not $inventory -or -not $inventory.PSObject.Properties['WindowsSandboxEnvironments']) { throw 'Unknown Windows Sandbox inventory schema; refusing to adopt an instance.' }
    foreach ($entry in $inventory.WindowsSandboxEnvironments) {
        $parsed = [Guid]::Empty
        if (-not [Guid]::TryParse([string]$entry.Id, [ref]$parsed)) { throw 'Invalid Windows Sandbox instance ID.' }
        $parsed.ToString('D')
    }
}
if (@(Get-SandboxIds).Count) { throw 'Another Sandbox exists; refusing to reuse, reconnect or stop it.' }
$results = [IO.Path]::GetFullPath($ResultsDirectory)
if (Test-Path -LiteralPath $results) { throw 'ResultsDirectory must be new.' }
$inputDirectory = Join-Path $results 'input'
$outputDirectory = Join-Path $results 'output'
New-Item -ItemType Directory -Path $inputDirectory,$outputDirectory -Force | Out-Null
$baselineDirectory = Join-Path $inputDirectory 'baseline'
$candidateDirectory = Join-Path $inputDirectory 'candidate'
New-Item -ItemType Directory -Path $baselineDirectory,$candidateDirectory | Out-Null
foreach ($file in @($BaselineInstaller,$CandidateInstaller)) {
    if ([IO.Path]::GetFileName($file) -notmatch '^NetLights-Setup-win-x64-\d+\.\d+\.\d+\.exe$') { throw 'Unexpected installer filename.' }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Installer input must be a file.' }
}
Copy-Item -LiteralPath $BaselineInstaller -Destination $baselineDirectory
Copy-Item -LiteralPath $CandidateInstaller -Destination $candidateDirectory
$baselineHash = (Get-FileHash -LiteralPath $BaselineInstaller -Algorithm SHA256).Hash
$candidateHash = (Get-FileHash -LiteralPath $CandidateInstaller -Algorithm SHA256).Hash
if ($baselineHash -eq $candidateHash) { throw 'Baseline and candidate must be different builds.' }
$baselineName = [IO.Path]::GetFileName($BaselineInstaller)
$candidateName = [IO.Path]::GetFileName($CandidateInstaller)
$baselineVersion = $baselineName -replace '^NetLights-Setup-win-x64-','' -replace '\.exe$',''
$candidateVersion = $candidateName -replace '^NetLights-Setup-win-x64-','' -replace '\.exe$',''
$agentVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($AgentPath).ProductVersion -split '\+')[0]
if ($agentVersion -ne $candidateVersion) { throw 'Candidate agent version does not match the candidate installer filename.' }
if (([Diagnostics.FileVersionInfo]::GetVersionInfo($publishedApp).ProductVersion -split '\+')[0] -ne $candidateVersion -or
    ([Diagnostics.FileVersionInfo]::GetVersionInfo($publishedAgent).ProductVersion -split '\+')[0] -ne $candidateVersion) { throw 'Verified published binaries do not match the candidate installer version.' }
Copy-Item -LiteralPath $AgentPath -Destination (Join-Path $inputDirectory 'NetLights.UpdateAgent.exe')
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $inputDirectory 'run.ps1')
$guestNonce = [Guid]::NewGuid().ToString('N')
@{baseline=('baseline\' + $baselineName); candidate=('candidate\' + $candidateName); candidateName=$candidateName;
    baselineVersion=$baselineVersion; candidateVersion=$candidateVersion; baselineSha256=$baselineHash; candidateSha256=$candidateHash;
    guestNonce=$guestNonce; agentSha256=(Get-FileHash -LiteralPath $AgentPath -Algorithm SHA256).Hash;
    publishedAppSha256=$publishedAppHash;publishedAgentSha256=$publishedAgentHash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $inputDirectory 'input.json') -Encoding utf8
$escapedInput = [Security.SecurityElement]::Escape($inputDirectory)
$escapedOutput = [Security.SecurityElement]::Escape($outputDirectory)
$configuration = "<Configuration><Networking>Enable</Networking><ClipboardRedirection>Disable</ClipboardRedirection><MappedFolders><MappedFolder><HostFolder>$escapedInput</HostFolder><SandboxFolder>C:\NLInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder><MappedFolder><HostFolder>$escapedOutput</HostFolder><SandboxFolder>C:\NLResult</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder></MappedFolders><LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\NLInput\run.ps1 -Guest -GuestNonce $guestNonce</Command></LogonCommand></Configuration>"
$wsb = Join-Path $results 'update.wsb'
[IO.File]::WriteAllText($wsb,$configuration)
$ownedId = [Guid]::NewGuid().ToString('D')
$startAttempted = $false
$connectionClient = $null
try {
    if (@(Get-SandboxIds).Count) { throw 'A Sandbox appeared during preparation; refusing to adopt it.' }
    $startAttempted = $true
    Invoke-SandboxCli -CliArguments @('start','--id',$ownedId,'--config',$configuration,'--raw') -TimeoutSeconds 120 | Out-Null
    if (@(Get-SandboxIds) -notcontains $ownedId) { throw 'Sandbox CLI did not create the requested isolated instance; no foreign instance will be stopped.' }
    # `start` creates the VM, while `connect` establishes the user session that
    # runs LogonCommand. Starting a VM alone doesn't test an interactive tray app.
    $connectionClient = Start-Process -FilePath $wsbCommand.Source -ArgumentList @('connect','--id',$ownedId,'--raw') -WindowStyle Hidden -PassThru
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(15)
    $resultPath = Join-Path $outputDirectory 'result.json'
    while (-not (Test-Path -LiteralPath $resultPath)) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Sandbox did not finish in fifteen minutes.' }
        Start-Sleep -Seconds 2
    }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 8
    if ($result.status -ne 'PASS') { throw 'Installed update/rollback Sandbox gate failed.' }
}
finally {
    # Never infer ownership from a service PID, reused window or inventory difference.
    if ($startAttempted -and @(Get-SandboxIds) -contains $ownedId) {
        try { Invoke-SandboxCli -CliArguments @('stop','--id',$ownedId,'--raw') -TimeoutSeconds 60 | Out-Null }
        catch { if (@(Get-SandboxIds) -contains $ownedId) { throw } }
        $cleanupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
        while (@(Get-SandboxIds) -contains $ownedId) {
            if ([DateTimeOffset]::UtcNow -ge $cleanupDeadline) { throw "Owned Sandbox did not stop: $ownedId" }
            Start-Sleep -Milliseconds 200
        }
    }
    if ($connectionClient) { $connectionClient.Dispose() }
}
