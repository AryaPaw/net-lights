param([string]$ResultsDirectory = ('TestResults/local-publish-'+[Guid]::NewGuid().ToString('N')))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'local-publish-transaction.ps1')
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'The locked-directory regression requires Windows.' }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class LocalPublishDirectoryLock {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    public static SafeFileHandle Open(string path) {
        var handle = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }
}
'@

function Assert-TransactionCondition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function New-TestStage([string]$Directory, [string]$Value) {
    New-Item -ItemType Directory -Path $Directory | Out-Null
    foreach ($name in @('NetLights.exe', 'NetLights.UpdateAgent.exe')) { [IO.File]::WriteAllText((Join-Path $Directory $name), $Value) }
}
function Assert-TestPayload([string]$Directory, [string]$Value) {
    foreach ($name in @('NetLights.exe', 'NetLights.UpdateAgent.exe')) {
        Assert-TransactionCondition ([IO.File]::ReadAllText((Join-Path $Directory $name)) -eq $Value) "Unexpected payload: $name"
    }
}

$artifacts = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts'))
$results=[IO.Path]::GetFullPath($ResultsDirectory,(Split-Path -Parent $PSScriptRoot))
if(Test-Path -LiteralPath $results) {throw 'Local publication tests require fresh results.'}
if(-not $results.StartsWith(([IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) 'TestResults'))+[IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)) {throw 'Test results must stay within workspace TestResults.'}
for($part=$results;$part;$part=[IO.Path]::GetDirectoryName($part)) {if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw 'Linked test results are forbidden.'}}
New-Item -ItemType Directory -Path $results -Force|Out-Null
$owners=[Collections.Generic.List[hashtable]]::new()
$controls=[Collections.Generic.List[object]]::new()
$testSucceeded=$false
$fixture = Join-Path $artifacts ('local-publish-test-' + [Guid]::NewGuid().ToString('N'))
Assert-TransactionCondition ([IO.Path]::GetDirectoryName($fixture) -eq $artifacts) 'Fixture must stay within artifacts.'
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $publish = Join-Path $fixture 'publish'
    New-TestStage $publish 'old'
    [IO.File]::WriteAllText((Join-Path $publish 'old-only.txt'), 'old extra file')

    # A real Windows directory handle prevents only the second rename. The old
    # directory must be restored after it has already been renamed to a backup.
    $stage = Join-Path $fixture ('publish-next-' + [Guid]::NewGuid().ToString('N'))
    New-TestStage $stage 'new'
    $lockedStage = [LocalPublishDirectoryLock]::Open($stage)
    $rejected = $false
    try { Complete-LocalPublishTransaction $fixture $stage $publish -ConfirmCandidate {} -StopCandidate {} | Out-Null }
    catch { $rejected = $true }
    finally { $lockedStage.Dispose() }
    Assert-TransactionCondition $rejected 'A locked staging rename must fail.'
    Assert-TestPayload $publish 'old'
    Assert-TestPayload $stage 'new'
    Assert-TransactionCondition ([IO.File]::ReadAllText((Join-Path $publish 'old-only.txt')) -eq 'old extra file') 'Rollback lost old files.'
    Assert-TransactionCondition (@(Get-ChildItem -LiteralPath $fixture -Directory -Filter 'publish-previous-*').Count -eq 0) 'Successful rollback retained a backup.'

    # Retrying the exact same prepared stage must atomically promote it and
    # remove obsolete files rather than overlaying old and new outputs.
    Complete-LocalPublishTransaction $fixture $stage $publish -ConfirmCandidate {} -StopCandidate {} | Out-Null
    Assert-TestPayload $publish 'new'
    Assert-TransactionCondition (-not [IO.File]::Exists((Join-Path $publish 'old-only.txt'))) 'Old output leaked into new publish.'
    Assert-TransactionCondition (-not [IO.Directory]::Exists($stage)) 'Promoted staging directory still exists.'
    Assert-TransactionCondition (@(Get-ChildItem -LiteralPath $fixture -Directory -Filter 'publish-previous-*').Count -eq 0) 'Completed transaction retained a removable backup.'

    $invalid = Join-Path $fixture 'not-owned'
    New-TestStage $invalid 'unrelated'
    $rejected = $false
    try { Complete-LocalPublishTransaction $fixture $invalid $publish -ConfirmCandidate {} -StopCandidate {} | Out-Null }
    catch { $rejected = $true }
    Assert-TransactionCondition $rejected 'Unowned stage must be rejected.'
    Assert-TestPayload $publish 'new'
    Assert-TestPayload $invalid 'unrelated'
    $rejected = $false
    try { Remove-OwnedLocalPublishDirectory $fixture $publish }
    catch { $rejected = $true }
    Assert-TransactionCondition $rejected 'Cleanup must never delete active publish.'
    Assert-TestPayload $publish 'new'
    # These are actual disposable native processes. Their synthetic health
    # events test the publisher protocol, not HTTPS monitoring itself.
    $root=Split-Path -Parent $PSScriptRoot
    $resource=@(Get-ChildItem -Path (Join-Path $artifacts 'rust-target/x86_64-pc-windows-msvc/*/build/netlights-update-agent-*/out/main_res.res') -File|Sort-Object LastWriteTimeUtc -Descending|Select-Object -First 1)
    if($resource.Count -ne 1) {throw 'Build the native workspace before the process controls.'}
    [xml]$props=Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
    $version=[string]$props.Project.PropertyGroup.Version
    $payload=Join-Path $fixture 'native-publisher-fixture.exe'
    $compilerStart=[Diagnostics.ProcessStartInfo]::new(@(Get-Command rustc -CommandType Application)[0].Source)
    $compilerStart.UseShellExecute=$false;$compilerStart.CreateNoWindow=$true
    $compilerStart.RedirectStandardOutput=$true;$compilerStart.RedirectStandardError=$true
    foreach($argument in @('--edition=2024','--crate-name','local_publish_fixture',(Join-Path $PSScriptRoot 'fixtures/local-publish-process.rs'),'-o',$payload,'-C','debuginfo=0','-C',"link-arg=$($resource[0].FullName)")) {$compilerStart.ArgumentList.Add($argument)}
    $compiler=[Diagnostics.Process]::Start($compilerStart)
    $compilerOut=$compiler.StandardOutput.ReadToEndAsync();$compilerError=$compiler.StandardError.ReadToEndAsync()
    try {
        if(-not $compiler.WaitForExit(120000)) {throw 'Publisher process fixture compiler timed out.'}
        $diagnostics=$compilerOut.GetAwaiter().GetResult()+$compilerError.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $results 'fixture-build.log'),$diagnostics.Substring(0,[Math]::Min(65536,$diagnostics.Length)))
        if($compiler.ExitCode -ne 0) {throw 'Publisher process fixture compilation failed.'}
    } finally {if(-not $compiler.HasExited) {$compiler.Kill($true);$compiler.WaitForExit(5000)|Out-Null};$compiler.Dispose()}
    function New-ProcessStage([string]$Path,[string]$Mode,[string]$Overlay) {
        New-Item -ItemType Directory -Path $Path|Out-Null
        foreach($name in @('NetLights.exe','NetLights.UpdateAgent.exe')) {Copy-Item -LiteralPath $payload -Destination (Join-Path $Path $name);[IO.File]::AppendAllText((Join-Path $Path $name),$Overlay)}
        [IO.File]::WriteAllText((Join-Path $Path 'fixture-mode.txt'),$Mode)
    }
    function New-ProcessProfile([string]$Path) {
        New-Item -ItemType Directory -Path $Path|Out-Null
        [IO.File]::WriteAllText((Join-Path $Path 'fixture-exit-event.txt'),('Local\NetLights.PublisherFixture.'+[Guid]::NewGuid().ToString('N')))
        [IO.File]::WriteAllText((Join-Path $Path 'data-sentinel.txt'),'history-preserved')
    }
    $foreignDirectory=Join-Path $fixture 'foreign';New-ProcessStage $foreignDirectory 'healthy' 'FOREIGN'
    $foreignProfile=Join-Path $fixture 'foreign-profile';New-ProcessProfile $foreignProfile
    $foreign=@{};$owners.Add($foreign)
    $foreignExe=Join-Path $foreignDirectory 'NetLights.exe';$foreignHash=(Get-FileHash -LiteralPath $foreignExe).Hash
    Start-VerifiedLocalProcess $foreignExe $foreignHash $version $foreign -ProfileDirectory $foreignProfile -ReadyMilliseconds 2000 -StableMilliseconds 100|Out-Null
    foreach($mode in @('healthy','crash','no-ready','no-ready-ignore-exit','no-start')) {
        $scenario=Join-Path $fixture ('process-'+$mode);New-Item -ItemType Directory -Path $scenario|Out-Null
        $active=Join-Path $scenario 'publish';New-ProcessStage $active 'healthy' 'OLD-IMAGE'
        $next=Join-Path $scenario ('publish-next-'+[Guid]::NewGuid().ToString('N'));New-ProcessStage $next $(if($mode -eq 'no-start'){'crash'}else{$mode}) 'NEW-IMAGE'
        $profile=Join-Path $scenario 'profile';New-ProcessProfile $profile
        $oldHash=(Get-FileHash -LiteralPath (Join-Path $active 'NetLights.exe')).Hash
        $newHash=(Get-FileHash -LiteralPath (Join-Path $next 'NetLights.exe')).Hash
        $prior=@{};$owners.Add($prior)
        Start-VerifiedLocalProcess (Join-Path $active 'NetLights.exe') $oldHash $version $prior -ProfileDirectory $profile -ReadyMilliseconds 2000 -StableMilliseconds 100|Out-Null
        Stop-OwnedLocalProcess $prior -ExitMilliseconds 1000
        $candidate=@{};$resumed=@{};$owners.Add($candidate);$owners.Add($resumed)
        $counts=@{started=0;restored=0};$failure=$null;$receipt=$null
        try {
            $receipt=Complete-LocalPublishTransaction $scenario $next $active -NoStart:($mode -eq 'no-start') -ConfirmCandidate {
                param($directory)
                $counts.started++
                Start-VerifiedLocalProcess (Join-Path $directory 'NetLights.exe') $newHash $version $candidate -ProfileDirectory $profile -ReadyMilliseconds 1000 -StableMilliseconds 100|Out-Null
            } -StopCandidate {Stop-OwnedLocalProcess $candidate -ExitMilliseconds 700} -RestorePrevious {
                param($directory)
                $counts.restored++
                Start-VerifiedLocalProcess (Join-Path $directory 'NetLights.exe') $oldHash $version $resumed -ProfileDirectory $profile -ReadyMilliseconds 2000 -StableMilliseconds 100|Out-Null
            }
        } catch {$failure=$_.Exception.Message}
        $backups=@(Get-ChildItem -LiteralPath $scenario -Directory -Filter 'publish-previous-*')
        if($mode -eq 'healthy') {
            Assert-TransactionCondition (-not $failure -and $receipt.healthConfirmed -and $backups.Count -eq 0 -and -not $candidate.Process.HasExited) 'Healthy promotion was not confirmed.'
        } elseif($mode -eq 'no-start') {
            Assert-TransactionCondition (-not $failure -and $receipt.status -eq 'NOT RUN' -and -not $receipt.healthConfirmed -and $backups.Count -eq 1 -and $counts.started -eq 0) 'NoStart deleted an unverified previous image or started a process.'
            Assert-TransactionCondition ((Get-FileHash -LiteralPath (Join-Path $backups[0].FullName 'NetLights.exe')).Hash -eq $oldHash) 'NoStart backup differs from the old image.'
        } elseif($mode -eq 'no-ready-ignore-exit') {
            Assert-TransactionCondition ([bool]$failure -and $backups.Count -eq 1 -and $counts.restored -eq 0 -and -not $candidate.Process.HasExited) 'Failed graceful exit permitted rollback or a second writer.'
            Assert-TransactionCondition ((Get-FileHash -LiteralPath (Join-Path $active 'NetLights.exe')).Hash -eq $newHash) 'Live unconfirmed candidate was moved.'
        } else {
            Assert-TransactionCondition ([bool]$failure -and $counts.restored -eq 1 -and -not $resumed.Process.HasExited -and $backups.Count -eq 0) 'Failed candidate was accepted or the old process was not restored.'
            Assert-TransactionCondition ((Get-FileHash -LiteralPath (Join-Path $active 'NetLights.exe')).Hash -eq $oldHash) 'Rollback did not restore the exact old bytes.'
            Assert-TransactionCondition ((Get-FileHash -LiteralPath (Join-Path $next 'NetLights.exe')).Hash -eq $newHash) 'Failed candidate source was not retained.'
        }
        Assert-TransactionCondition ([IO.File]::ReadAllText((Join-Path $profile 'data-sentinel.txt')) -ceq 'history-preserved') 'Publication altered profile data.'
        Assert-TransactionCondition (-not $foreign.Process.HasExited -and (Get-FileHash -LiteralPath $foreignExe).Hash -eq $foreignHash) 'A foreign process or its files were changed.'
        $controls.Add(@{mode=$mode;result='PASS';failureReported=[bool]$failure;restartCount=$counts.restored;backupCount=$backups.Count;foreignAlive=$true;profilePreserved=$true})
        foreach($owner in @($candidate,$resumed)) {if($owner.ContainsKey('Process') -and -not $owner.Process.HasExited) {
            if($mode -eq 'no-ready-ignore-exit') {$owner.Process.Kill();$owner.Process.WaitForExit(5000)|Out-Null} else {Stop-OwnedLocalProcess $owner -ExitMilliseconds 1000}
        }}
    }
    $detachedDirectory=Join-Path $fixture 'detached';New-ProcessStage $detachedDirectory 'healthy' 'DETACHED'
    $detachedProfile=Join-Path $fixture 'detached-profile';New-ProcessProfile $detachedProfile
    $detachedExe=Join-Path $detachedDirectory 'NetLights.exe';$detachedHash=(Get-FileHash -LiteralPath $detachedExe).Hash
    $launcher=Join-Path $fixture 'launch-detached.ps1'
    [IO.File]::WriteAllText($launcher,@'
param($Helper,$Exe,$Hash,$Version,$Profile)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. $Helper
$owner=@{}
Start-VerifiedLocalProcess $Exe $Hash $Version $owner -ProfileDirectory $Profile -ReadyMilliseconds 2000 -StableMilliseconds 100|Out-Null
Write-Output 'PUBLISHER_EXIT_WITH_LIVE_CHILD'
'@)
    $parentStart=[Diagnostics.ProcessStartInfo]::new(@(Get-Command pwsh -CommandType Application)[0].Source)
    $parentStart.UseShellExecute=$false;$parentStart.CreateNoWindow=$true;$parentStart.RedirectStandardOutput=$true;$parentStart.RedirectStandardError=$true
    foreach($argument in @('-NoProfile','-File',$launcher,'-Helper',(Join-Path $PSScriptRoot 'local-publish-transaction.ps1'),'-Exe',$detachedExe,'-Hash',$detachedHash,'-Version',$version,'-Profile',$detachedProfile)) {$parentStart.ArgumentList.Add($argument)}
    $parent=[Diagnostics.Process]::Start($parentStart);$parentOutput=$parent.StandardOutput.ReadToEndAsync();$parentError=$parent.StandardError.ReadToEndAsync()
    try {
        if(-not $parent.WaitForExit(10000)) {throw 'Detached launch parent did not exit.'}
        Assert-TransactionCondition ($parent.ExitCode -eq 0) 'Detached launch parent failed.'
        $detachedPid=[int][IO.File]::ReadAllText((Join-Path $detachedProfile 'fixture-last-pid.txt'))
        $detachedProcess=Get-Process -Id $detachedPid
        Assert-TransactionCondition ([string]::Equals($detachedProcess.Path,$detachedExe,[StringComparison]::OrdinalIgnoreCase)) 'Detached child identity differs from the created fixture.'
        $detached=@{Process=$detachedProcess;Executable=$detachedExe;ProfileDirectory=$detachedProfile};$owners.Add($detached)
        Assert-TransactionCondition ($parentOutput.Wait(5000) -and $parentError.Wait(5000) -and -not $detachedProcess.HasExited) 'Surviving child kept parent output pipes open.'
        Assert-TransactionCondition ($parentOutput.GetAwaiter().GetResult().Contains('PUBLISHER_EXIT_WITH_LIVE_CHILD')) 'Detached parent output was missing.'
        $controls.Add(@{mode='detached-parent-output';result='PASS';parentExited=$true;childAlive=$true;outputClosed=$true})
    } finally {if(-not $parent.HasExited){$parent.Kill($true);$parent.WaitForExit(5000)|Out-Null};$parent.Dispose()}
    $controls|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $results 'process-controls.json') -Encoding utf8
    Write-Output 'PASS: locked second rename rollback, complete promotion, obsolete-file removal, ownership and cleanup guards.'
    Write-Output "PASS: real process controls (healthy, crash, no readiness, failed graceful exit, NoStart, foreign process and profile preservation). Results: $results"
    $testSucceeded=$true
}
finally {
    foreach($owner in $owners) {if($owner.ContainsKey('Process')) {
        $ownedProcess=$owner.Process
        if(-not $ownedProcess.HasExited) {
            # These are only the disposable test executables compiled above.
            try {Stop-OwnedLocalProcess $owner -ExitMilliseconds 1000}catch {if(-not $ownedProcess.HasExited){$ownedProcess.Kill();$ownedProcess.WaitForExit(5000)|Out-Null}}
        }
        $ownedProcess.Dispose()
    }}
    # This unique fixture was created by this script directly within artifacts.
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($resolvedFixture) -ne $artifacts -or [IO.Path]::GetFileName($resolvedFixture) -cnotmatch '^local-publish-test-[a-f0-9]{32}$') {
        throw 'Refusing fixture cleanup outside artifacts.'
    }
    @{status=$(if($testSucceeded){'PASS'}else{'FAIL'});controls=@($controls)}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $results 'result.json') -Encoding utf8
    $archivePath=Join-Path $results 'evidence.zip'
    $archive=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Create)
    try {foreach($file in Get-ChildItem -LiteralPath $results -File|Where-Object {$_.Extension -in @('.json','.log')}) {[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$file.Name)|Out-Null}}finally{$archive.Dispose()}
    $archive=[IO.Compression.ZipFile]::OpenRead($archivePath)
    try {foreach($entry in $archive.Entries) {
        $input=$entry.Open();$original=[IO.File]::ReadAllBytes((Join-Path $results $entry.FullName));$hasher=[Security.Cryptography.SHA256]::Create()
        try {if(-not [Linq.Enumerable]::SequenceEqual[byte]($hasher.ComputeHash($input),[Security.Cryptography.SHA256]::HashData($original))) {throw 'Test evidence archive verification failed; fixture retained.'}}finally{$input.Dispose();$hasher.Dispose()}
    }}finally{$archive.Dispose()}
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
