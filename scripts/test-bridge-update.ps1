param(
    [string]$BaselineInstaller, [string]$BridgeInstaller, [string]$BridgePublishDirectory,
    [string]$NativeInstaller, [string]$NativePublishDirectory,
    [string]$ResultsDirectory = ('artifacts/bridge-1.6.1-sandbox-' + [Guid]::NewGuid().ToString('N')),
    [switch]$RunSandbox, [switch]$Guest, [string]$GuestNonce, [switch]$SelfTest
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$publishedBaselineHash='73d993416a298690f26891936d3b794179edc62ad86e67a7144d695840c2dd97'

function Assert-Digest([string]$Path,[string]$Expected) {
    if($Expected -cnotmatch '^[A-Fa-f0-9]{64}$' -or -not(Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected){throw 'Exact executable or installer digest mismatch.'}
}
function Read-Version([string]$Path){([Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion -split '\+')[0]}

if($Guest){
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(($identity.Name -split '\\')[-1] -cne 'WDAGUtilityAccount' -or -not $identity.User -or
        -not(Test-Path -LiteralPath 'C:\NLBridgeInput\input.json' -PathType Leaf) -or -not(Test-Path -LiteralPath 'C:\NLBridgeResult' -PathType Container)){
        throw 'Guest mode requires the real Sandbox account and this scenario mapped folders.'
    }
    $config=Get-Content -LiteralPath 'C:\NLBridgeInput\input.json' -Raw|ConvertFrom-Json
    if($GuestNonce -cnotmatch '^[a-f0-9]{32}$' -or $GuestNonce -cne $config.nonce){throw 'Wrong Sandbox scenario nonce.'}
    $install=Join-Path $env:LOCALAPPDATA 'Programs\NetLights';$app=Join-Path $install 'NetLights.exe'
    $data=Join-Path $env:LOCALAPPDATA 'NetLights';$updates=Join-Path $data 'updates'
    $utf8=New-Object Text.UTF8Encoding($false)
    $result=[ordered]@{status='FAIL';steps=@();startedUtc=[DateTimeOffset]::UtcNow.ToString('o');automaticPublicRetryLoop='NOT RUN';retryConsumerEvidence='Release unit FakeFeed';native='NOT RUN'}
    function Write-Step([string]$Name,[object]$Detail){
        $result.steps+=@{name=$Name;status='PASS';detail=$Detail}
        $line=([DateTimeOffset]::UtcNow.ToString('o'))+' PASS '+$Name+"`r`n"
        $log='C:\NLBridgeResult\guest.log'
        if((Test-Path -LiteralPath $log)-and(Get-Item -LiteralPath $log).Length -gt 1MB){throw 'Guest log limit reached.'}
        [IO.File]::AppendAllText($log,$line,$utf8)
    }
    function Stop-App {
        $owned=@(Get-Process NetLights -ErrorAction SilentlyContinue|Where-Object{$_.Path -ieq $app})
        if(-not $owned.Count){return}
        $command=Start-Process -FilePath $app -ArgumentList '--exit-local' -WindowStyle Hidden -PassThru
        try{if(-not$command.WaitForExit(10000)){throw 'Graceful exit command timed out.'}}finally{$command.Dispose()}
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
        while(@(Get-Process NetLights -ErrorAction SilentlyContinue|Where-Object{$_.Path -ieq $app}).Count){
            if([DateTimeOffset]::UtcNow -ge $deadline){throw 'Owned installed application did not exit gracefully.'}
            Start-Sleep -Milliseconds 100
        }
    }
    function Get-App {
        $processes=@(Get-Process NetLights -ErrorAction SilentlyContinue|Where-Object{$_.Path -ieq $app})
        if($processes.Count -ne 1){throw 'Expected one owned installed application process.'}
        return $processes[0]
    }
    function Wait-App {
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds(25)
        while([DateTimeOffset]::UtcNow -lt $deadline){
            $found=@(Get-Process NetLights -ErrorAction SilentlyContinue|Where-Object{$_.Path -ieq $app})
            if($found.Count -eq 1){return $found[0]}
            Start-Sleep -Milliseconds 100
        }
        throw 'Installed application did not start.'
    }
    function Assert-Live([Diagnostics.Process]$Process){
        $id=$Process.Id
        Start-Sleep -Seconds 4
        $Process.Refresh();if($Process.HasExited -or (Get-App).Id -ne $id){throw 'Recovered or updated process did not remain alive.'}
    }
    function Get-InstallationState {
        $files=@(Get-ChildItem -LiteralPath $install -Recurse -File|Sort-Object FullName|ForEach-Object{
            @{path=$_.FullName.Substring($install.Length+1);hash=(Get-FileHash -LiteralPath $_.FullName).Hash}
        })
        $key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C2B1A9E-4F31-4A6D-9C2E-1B8E5D2A0F11}_is1'
        $registration=Get-ItemProperty -LiteralPath $key
        $group=$registration.'Inno Setup: Icon Group'
        $shortcut=Join-Path ([Environment]::GetFolderPath('Programs')) (Join-Path $group 'Net Lights.lnk')
        if(-not(Test-Path -LiteralPath $shortcut -PathType Leaf)){throw 'Owned Inno shortcut missing.'}
        return @{files=$files;version=[string]$registration.DisplayVersion;shortcut=$shortcut;shortcutHash=(Get-FileHash -LiteralPath $shortcut).Hash;uninstall=[string]$registration.UninstallString}
    }
    function Assert-Restored([object]$Before){
        $after=Get-InstallationState
        if($after.version -cne $Before.version -or $after.uninstall -cne $Before.uninstall -or
            $after.shortcut -cne $Before.shortcut -or $after.shortcutHash -cne $Before.shortcutHash){throw 'Rollback metadata or exact shortcut bytes were not restored.'}
        if(($Before.files|ConvertTo-Json -Depth 5 -Compress) -cne ($after.files|ConvertTo-Json -Depth 5 -Compress)){throw 'Rollback did not restore the complete installation file manifest.'}
        if(@(Get-ChildItem -LiteralPath $updates -Filter 'active-*.json' -File).Count){throw 'A resolved update left an active transaction marker.'}
    }
    function Assert-Seed {
        $settings=Get-Content -LiteralPath (Join-Path $data 'settings.json') -Raw|ConvertFrom-Json
        foreach($pair in @(@('windowWidth',1234),@('windowHeight',987),@('availabilityPollingSeconds',5),@('countryPollingSeconds',10),@('emphasizeShortStatuses',$false),@('autoUpdateEnabled',$false))){
            if($settings.($pair[0]) -ne $pair[1]){throw 'A known persisted setting was lost.'}
        }
        $locations=Get-Content -LiteralPath (Join-Path $data 'location-history.json') -Raw|ConvertFrom-Json
        $availability=Get-Content -LiteralPath (Join-Path $data 'availability-history.json') -Raw|ConvertFrom-Json
        foreach($stay in $seed.stays){
            $matches=@($locations.stays|Where-Object{$_.iso -ceq $stay.iso -and [DateTimeOffset]$_.startedUtc -eq [DateTimeOffset]$stay.startedUtc -and [DateTimeOffset]$_.endedUtc -eq [DateTimeOffset]$stay.endedUtc})
            if($matches.Count -ne 1){throw 'A seeded country interval was lost, duplicated or extended.'}
        }
        foreach($observation in $seed.observations){
            $matches=@($locations.observations|Where-Object{[DateTimeOffset]$_.observedUtc -eq [DateTimeOffset]$observation.observedUtc})
            if($matches.Count -ne 1){throw 'A seeded observation was lost or duplicated.'}
            foreach($field in @('publicIp','publicIpHash','countryCode','localCountry','onlineCountry','thirdCountry','databaseVersion','iplocateCountry','iplocateDatabaseVersion','resolutionSource','confirmed','publicIpChanged','countryChanged','classificationChanged')){
                if($matches[0].$field -cne $observation.$field){throw 'Historical IP, classification, provenance or database version changed.'}
            }
            if([DateTimeOffset]$matches[0].observedUntilUtc -ne [DateTimeOffset]$observation.observedUntilUtc){throw 'An observation was extended without an IP poll.'}
        }
        foreach($span in $seed.spans){
            if(@($availability.spans|Where-Object{$_.group -ceq $span.group -and $_.state -ceq $span.state -and $_.paused -eq $span.paused -and [DateTimeOffset]$_.startedUtc -eq [DateTimeOffset]$span.startedUtc -and [DateTimeOffset]$_.endedUtc -eq [DateTimeOffset]$span.endedUtc}).Count -ne 1){throw 'A seeded availability interval was lost, duplicated or extended.'}
        }
    }
    function Invoke-InstalledAgent([string]$Setup,[string]$ExpectedAgentHash,[string]$ExpectedAgentVersion){
        $parent=Get-App
        $stage=Join-Path $updates ([Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $stage|Out-Null
        $stagedAgent=Join-Path $stage 'NetLights.UpdateAgent.exe';Copy-Item -LiteralPath (Join-Path $install 'NetLights.UpdateAgent.exe') -Destination $stagedAgent
        Assert-Digest $stagedAgent $ExpectedAgentHash
        if((Read-Version $stagedAgent) -cne $ExpectedAgentVersion){throw 'The installed agent version is not the expected legacy or bridge agent.'}
        $pending=Join-Path $stage ([IO.Path]::GetFileName($Setup));Copy-Item -LiteralPath $Setup -Destination $pending
        $digest=(Get-FileHash -LiteralPath $pending).Hash.ToLowerInvariant()
        $readyName='Local\NetLights.UpdateAgentReady.'+[Guid]::NewGuid().ToString('N')
        $ready=New-Object Threading.EventWaitHandle($false,[Threading.EventResetMode]::ManualReset,$readyName)
        $arguments='--parent '+$parent.Id+' --pending "'+$pending+'" --sha256 '+$digest+' --restart "'+$app+'" --ready '+$readyName
        $agent=Start-Process -FilePath $stagedAgent -ArgumentList $arguments -WindowStyle Hidden -PassThru
        try{
            if(-not$ready.WaitOne(5000)-or$agent.HasExited){throw 'The real installed agent did not acknowledge its ready handoff.'}
            $parent.Refresh();if($parent.HasExited){throw 'The parent exited before handoff was acknowledged.'}
            # Wait for this parent identity, not an empty process-name interval:
            # a fast failing installer may already have restarted the old version.
            $exitCommand=Start-Process -FilePath $app -ArgumentList '--exit-local' -WindowStyle Hidden -PassThru
            try{if(-not$exitCommand.WaitForExit(10000)){throw 'Parent exit command timed out.'}}finally{$exitCommand.Dispose()}
            if(-not$parent.WaitForExit(30000)){throw 'The exact handoff parent did not exit.'}
            if(-not$agent.WaitForExit(720000)){throw 'Owned installed agent exceeded its bounded installer and health waits.'}
            $agent.Refresh();$restarted=Wait-App;Assert-Live $restarted
            $status=Get-Content -LiteralPath (Join-Path $updates 'result.json') -Raw|ConvertFrom-Json
            return @{exitCode=$agent.ExitCode;success=[bool]$status.success;setupSha256=$digest;agentSha256=$ExpectedAgentHash;agentVersion=$ExpectedAgentVersion;recoveryPid=$restarted.Id}
        }
        finally{$ready.Dispose();$agent.Dispose();$parent.Dispose()}
    }
    function Assert-Retry([object]$Attempt,[string]$Version){
        $guardPath=Join-Path $updates 'failed-candidate.json';$guard=Get-Content -LiteralPath $guardPath -Raw|ConvertFrom-Json
        if($guard.version -cne $Version -or $guard.sha256 -ine $Attempt.setupSha256 -or $guard.attempts -lt 1 -or $guard.attempts -gt 8){throw 'Failed candidate durable identity was not persisted.'}
        $hours=[Math]::Min(12*[Math]::Pow(2,$guard.attempts-1),168)
        $remaining=([DateTimeOffset]$guard.retryAfterUtc)-[DateTimeOffset]::UtcNow
        if($remaining.TotalHours -lt $hours-0.1 -or $remaining.TotalHours -gt $hours+0.01){throw 'Failed candidate cooldown is not the expected bounded policy.'}
        if((Get-Item -LiteralPath $guardPath).LastWriteTimeUtc -gt (Get-App).StartTime.ToUniversalTime()){throw 'Recovery started before the retry guard was durable.'}
    }
    try{
        New-Item -ItemType Directory -Path $updates|Out-Null
        foreach($item in @($config.baseline,$config.bridge)) {Assert-Digest (Join-Path 'C:\NLBridgeInput' $item.path) $item.sha256}
        if($config.native){Assert-Digest (Join-Path 'C:\NLBridgeInput' $config.native.path) $config.native.sha256}
        if($config.baseline.sha256 -ine $publishedBaselineHash){throw 'Guest baseline is not the published release.'}
        $seed=Get-Content -LiteralPath 'C:\NLBridgeInput\seed.json' -Raw|ConvertFrom-Json
        foreach($entry in @(@('settings.json',$seed.settings),@('location-history.json',@{version=3;lastObservedUtc=$seed.lastObservedUtc;stays=$seed.stays;observations=$seed.observations}),@('availability-history.json',@{lastObservedUtc=$seed.lastObservedUtc;spans=$seed.spans}),@('endpoints.json',$seed.endpoints))){
            [IO.File]::WriteAllText((Join-Path $data $entry[0]),($entry[1]|ConvertTo-Json -Depth 16),$utf8)
        }
        $setup=Start-Process -FilePath (Join-Path 'C:\NLBridgeInput' $config.baseline.path) -ArgumentList ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="'+$install+'"') -WindowStyle Hidden -PassThru
        try{if(-not$setup.WaitForExit(120000)-or$setup.ExitCode-ne0){throw 'Official baseline Inno install failed.'}}finally{$setup.Dispose()}
        $null=Wait-App
        if((Read-Version $app)-cne'1.6.0'){throw 'Installed baseline is not 1.6.0.'}
        $legacyAgentHash=(Get-FileHash -LiteralPath (Join-Path $install 'NetLights.UpdateAgent.exe')).Hash
        Write-Step 'official-CSharp-1.6.0-installed' @{agentSha256=$legacyAgentHash}
        $transition=Invoke-InstalledAgent (Join-Path 'C:\NLBridgeInput' $config.bridge.path) $legacyAgentHash '1.6.0'
        if($transition.exitCode-ne0-or-not$transition.success){throw 'The published old agent did not install and confirm bridge 1.6.1.'}
        Assert-Digest $app $config.bridge.appSha256;Assert-Digest (Join-Path $install 'NetLights.UpdateAgent.exe') $config.bridge.agentSha256
        Stop-App;Assert-Seed
        $null=Start-Process -FilePath $app -WindowStyle Hidden -PassThru
        Write-Step 'real-old-agent-ready-parent-exit-health-live-bridge' $transition
        $bridgeState=Get-InstallationState

        # This negative fixture is explicit: it tests the real installed bridge agent,
        # not Inno's write-failure implementation. The real Inno mismatch follows.
        $fixture=Join-Path $updates 'NetLights-Setup-win-x64-1.7.0.exe'
        Add-Type -OutputAssembly $fixture -OutputType ConsoleApplication -TypeDefinition @'
using System; using System.IO; using System.Reflection; using System.Threading;
[assembly: AssemblyVersion("1.7.0.0")]
[assembly: AssemblyFileVersion("1.7.0.0")]
[assembly: AssemblyInformationalVersion("1.7.0")]
public static class Fault {
 public static int Main(string[] args) {
  string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","NetLights");
  if(Environment.UserName!="WDAGUtilityAccount" || !File.Exists(Path.Combine(root,"unins000.exe"))) return 90;
  if(args.Length>0 && args[0].StartsWith("/",StringComparison.Ordinal)) {
   string mode=Environment.GetEnvironmentVariable("NL_BRIDGE_FAULT");
   if(mode=="partial") {File.WriteAllText(Path.Combine(root,"NetLights.exe"),"partial installer bytes");File.WriteAllText(Path.Combine(root,"added-fixture.bin"),"added");return 17;}
   if(mode=="health") {File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(root,"NetLights.exe"),true);return 0;}
   return 91;
  }
  Thread.Sleep(TimeSpan.FromMinutes(5)); return 0;
 }
}
'@
        foreach($mode in @('partial','health')){
            $env:NL_BRIDGE_FAULT=$mode
            try{$attempt=Invoke-InstalledAgent $fixture $config.bridge.agentSha256 '1.6.1'}finally{Remove-Item Env:NL_BRIDGE_FAULT -ErrorAction SilentlyContinue}
            $expected=if($mode-eq'partial'){17}else{8}
            if($attempt.exitCode-ne$expected-or$attempt.success){throw 'The bridge agent accepted an intentional installation or health failure.'}
            Assert-Restored $bridgeState;Assert-Retry $attempt '1.7.0'
            Stop-App;Assert-Seed;$null=Start-Process -FilePath $app -WindowStyle Hidden -PassThru
            Write-Step ("bridge-$mode-fixture-rollback-data-durable-retry") $attempt
        }
        if($config.native){
            $wrong=Join-Path $updates 'NetLights-Setup-win-x64-999.0.0.exe';Copy-Item -LiteralPath (Join-Path 'C:\NLBridgeInput' $config.native.path) -Destination $wrong
            $attempt=Invoke-InstalledAgent $wrong $config.bridge.agentSha256 '1.6.1'
            if($attempt.exitCode-ne8-or$attempt.success){throw 'Real Inno version mismatch was not rejected.'}
            Assert-Restored $bridgeState;Assert-Retry $attempt '999.0.0'
            Stop-App;Assert-Seed;$null=Start-Process -FilePath $app -WindowStyle Hidden -PassThru
            Write-Step 'real-native-Inno-wrong-version-exact-files-registry-shortcut-rollback' $attempt
            $attempt=Invoke-InstalledAgent (Join-Path 'C:\NLBridgeInput' $config.native.path) $config.bridge.agentSha256 '1.6.1'
            if($attempt.exitCode-ne0-or-not$attempt.success){throw 'The installed bridge agent did not install and confirm native 1.7.0.'}
            Assert-Digest $app $config.native.appSha256;Assert-Digest (Join-Path $install 'NetLights.UpdateAgent.exe') $config.native.agentSha256
            if((Read-Version $app)-cne'1.7.0'){throw 'The installed native version is not 1.7.0.'}
            Stop-App;Assert-Seed
            $result.native='PASS';Write-Step 'real-bridge-agent-native-1.7.0-health-live-and-rich-data' $attempt
            $result.status='PASS'
        } else {$result.status='PARTIAL'}
    }
    catch{$result.error=$_.Exception.Message}
    finally{
        $result.completedUtc=[DateTimeOffset]::UtcNow.ToString('o')
        [IO.File]::WriteAllText('C:\NLBridgeResult\result.json.tmp',($result|ConvertTo-Json -Depth 12),$utf8)
        [IO.File]::Move('C:\NLBridgeResult\result.json.tmp','C:\NLBridgeResult\result.json')
        Start-Process -FilePath "$env:WINDIR\System32\shutdown.exe" -ArgumentList '/s /t 0' -WindowStyle Hidden|Out-Null
    }
    return
}

if($PSVersionTable.PSVersion.Major -lt 7){throw 'Host preparation requires PowerShell 7; only guarded Guest mode uses Windows PowerShell.'}
$repository=Split-Path -Parent $PSScriptRoot
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
function Assert-HostOutput([string]$Path){
    $full=[IO.Path]::GetFullPath($Path)
    if([IO.Path]::GetDirectoryName($full)-ine$artifactRoot-or[IO.Path]::GetFileName($full)-cnotmatch'^bridge-1\.6\.1-[A-Za-z0-9-]+$'){throw 'Sandbox output must be a named direct child of repository artifacts.'}
    for($current=$full;$current;$current=[IO.Path]::GetDirectoryName($current)){
        try{$attributes=[IO.File]::GetAttributes($current)} catch [IO.FileNotFoundException] {continue} catch [IO.DirectoryNotFoundException] {continue}
        if($attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Linked Sandbox output rejected.'}
    }
    if(Test-Path -LiteralPath $full){throw 'Sandbox output must be new.'};return $full
}
if($SelfTest){
    if($RunSandbox -or $BaselineInstaller -or $BridgeInstaller -or $NativeInstaller){throw 'SelfTest cannot start an installation scenario.'}
    $root=Assert-HostOutput (Join-Path $artifactRoot ('bridge-1.6.1-controls-'+[Guid]::NewGuid().ToString('N')))
    New-Item -ItemType Directory -Path $root|Out-Null
    $file=Join-Path $root 'owned.bin';[IO.File]::WriteAllText($file,'original')
    $hash=(Get-FileHash -LiteralPath $file).Hash;Assert-Digest $file $hash
    [IO.File]::WriteAllText($file,'different');$rejected=$false;try{Assert-Digest $file $hash}catch{$rejected=$true}
    if(-not$rejected){throw 'Digest negative control failed.'}
    $rejected=$false;try{$null=Assert-HostOutput $repository}catch{$rejected=$true};if(-not$rejected){throw 'Output ownership negative control failed.'}
    $rejected=$false;try{$null=Assert-HostOutput $root}catch{$rejected=$true};if(-not$rejected-or[IO.File]::ReadAllText($file)-cne'different'){throw 'Existing output preservation control failed.'}
    Write-Output 'RUN - PASS: exact digest, output ownership and existing-data controls. No Sandbox or installer was started.';return
}
if(-not$BaselineInstaller-or-not$BridgeInstaller-or-not$BridgePublishDirectory){throw 'Supply the exact published baseline, bridge installer and bridge publish directory.'}
if([bool]$NativeInstaller-ne[bool]$NativePublishDirectory){throw 'Supply both native installer and exact native publish directory, or neither.'}
Assert-Digest $BaselineInstaller $publishedBaselineHash
if((Get-Item -LiteralPath $BaselineInstaller).Length-ne59140690){throw 'Published baseline size mismatch.'}
$bridgePublish=[IO.Path]::GetFullPath($BridgePublishDirectory).TrimEnd('\','/')
$bridgeRoot=[IO.Path]::GetDirectoryName($bridgePublish)
$receipt=Get-Content -LiteralPath (Join-Path $bridgeRoot 'bridge.json') -Raw|ConvertFrom-Json
foreach($check in @('prepare','build','unit','integration','soak','coverage','publish','installer')){
    if($receipt.$check-cne'PASS'){throw 'Bridge input lacks a completed canonical verification receipt.'}
}
if($receipt.version-cne'1.6.1'-or$receipt.baseCommit-cne'523e33b40a85e28cdb972f9401aba02ea02855d8'-or
    $receipt.patchSha256-ine(Get-FileHash -LiteralPath (Join-Path $repository 'compat/bridge-1.6.1.patch')).Hash){throw 'Bridge inputs were produced from a stale or different compatibility patch.'}
Assert-Digest (Join-Path $bridgeRoot 'source.json') $receipt.sourceManifestSha256
Assert-Digest $BridgeInstaller $receipt.installerSha256
foreach($name in @('NetLights.exe','NetLights.UpdateAgent.exe')){
    $record=@($receipt.binaries|Where-Object{$_.name-ceq$name})
    if($record.Count-ne1){throw 'Bridge receipt has missing or repeated binary identity.'}
    Assert-Digest (Join-Path $bridgePublish $name) $record[0].sha256
}
$requested=if([IO.Path]::IsPathFullyQualified($ResultsDirectory)){$ResultsDirectory}else{Join-Path $repository $ResultsDirectory}
$root=Assert-HostOutput $requested
$inputs=Join-Path $root 'input';$output=Join-Path $root 'output';New-Item -ItemType Directory -Path $inputs,$output|Out-Null
$config=[ordered]@{nonce=[Guid]::NewGuid().ToString('N');baseline=@{path='NetLights-Setup-win-x64-1.6.0.exe';sha256=$publishedBaselineHash};bridge=$null;native=$null}
Copy-Item -LiteralPath $BaselineInstaller -Destination (Join-Path $inputs $config.baseline.path)
foreach($item in @(@{version='1.6.1';installer=$BridgeInstaller;publish=$BridgePublishDirectory;key='bridge'},@{version='1.7.0';installer=$NativeInstaller;publish=$NativePublishDirectory;key='native'})){
    if(-not$item.installer){continue}
    $expectedName='NetLights-Setup-win-x64-'+$item.version+'.exe'
    if([IO.Path]::GetFileName($item.installer)-cne$expectedName){throw 'Installer filename does not match its exact intended version.'}
    $exe=Join-Path $item.publish 'NetLights.exe';$agent=Join-Path $item.publish 'NetLights.UpdateAgent.exe'
    foreach($file in @($exe,$agent)){if(-not(Test-Path -LiteralPath $file -PathType Leaf)-or(Read-Version $file)-cne$item.version){throw 'Published PE version mismatch.'}}
    Copy-Item -LiteralPath $item.installer -Destination (Join-Path $inputs $expectedName)
    $config[$item.key]=@{path=$expectedName;sha256=(Get-FileHash -LiteralPath $item.installer).Hash;appSha256=(Get-FileHash -LiteralPath $exe).Hash;agentSha256=(Get-FileHash -LiteralPath $agent).Hash}
}
$start=[DateTimeOffset]::UtcNow.AddMinutes(-30)
function Stamp([int]$Minutes){$start.AddMinutes($Minutes).ToString('o')}
$observations=@();foreach($index in @(0,1)){
    $iso=if($index-eq0){'DE'}else{'PL'}
    $observations+=@{observedUtc=(Stamp (14+$index*3));observedUntilUtc=(Stamp (15+$index*3));publicIp=('192.0.2.'+($index+1));publicIpHash=('a'*64);countryCode=$iso;localCountry='FR';onlineCountry=$iso;thirdCountry=$iso;databaseVersion='recorded-v2fly-v1';iplocateCountry='NL';iplocateDatabaseVersion='recorded-iplocate-v1';resolutionSource='country.is';confirmed=$true;publicIpChanged=($index-eq1);countryChanged=($index-eq1);classificationChanged=$false}
}
$seed=@{lastObservedUtc=(Stamp 20);settings=@{settingsVersion=8;autoStart=$false;autoUpdateEnabled=$false;geoCountryDetectionEnabled=$false;geoCountryIconEnabled=$false;availabilityPollingSeconds=5;countryPollingSeconds=10;windowWidth=1234;windowHeight=987;emphasizeShortStatuses=$false};
    stays=@(@{iso='DE';startedUtc=(Stamp 14);endedUtc=(Stamp 15)},@{iso='PL';startedUtc=(Stamp 17);endedUtc=(Stamp 18)},@{iso='RU';startedUtc=(Stamp 20);endedUtc=(Stamp 20)});observations=$observations;
    spans=@(@{group='Ru';state='Online';paused=$false;startedUtc=(Stamp 0);endedUtc=(Stamp 5)},@{group='Ru';state='Unknown';paused=$false;startedUtc=(Stamp 6);endedUtc=(Stamp 7)},@{group='World';state='Offline';paused=$false;startedUtc=(Stamp 0);endedUtc=(Stamp 3)},@{group='World';state='Online';paused=$true;startedUtc=(Stamp 4);endedUtc=(Stamp 5)});
    endpoints=@(foreach($group in @('Ru','World')){foreach($index in 0..6){@{id=($group+'-'+$index);group=$group;uri=('https://127.0.0.1:1/'+$group+'/'+$index);infrastructureId=('local-'+$group+'-'+$index)}}})}
[IO.File]::WriteAllText((Join-Path $inputs 'seed.json'),($seed|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $inputs 'input.json'),($config|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $inputs 'run.ps1')
$xml="<Configuration><Networking>Enable</Networking><ClipboardRedirection>Disable</ClipboardRedirection><MappedFolders><MappedFolder><HostFolder>$([Security.SecurityElement]::Escape($inputs))</HostFolder><SandboxFolder>C:\NLBridgeInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder><MappedFolder><HostFolder>$([Security.SecurityElement]::Escape($output))</HostFolder><SandboxFolder>C:\NLBridgeResult</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder></MappedFolders><LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\NLBridgeInput\run.ps1 -Guest -GuestNonce $($config.nonce)</Command></LogonCommand></Configuration>"
$configuration=Join-Path $root 'bridge.wsb';[IO.File]::WriteAllText($configuration,$xml,[Text.UTF8Encoding]::new($false))
if(-not$RunSandbox){Write-Output "PREPARED: $root. Sandbox and installations NOT RUN. Explicit RunSandbox and exclusive ownership are required.";return}

$wsb=(Get-Command wsb.exe -CommandType Application -ErrorAction Stop|Select-Object -First 1).Source
function Invoke-SandboxCli([string[]]$Arguments){
    $start=[Diagnostics.ProcessStartInfo]::new($wsb);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try{
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not$process.WaitForExit(60000)){if(-not$process.HasExited){$process.Kill();$null=$process.WaitForExit(5000)};throw 'Sandbox CLI timed out.'}
        $text=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult()
        if($text.Length-gt65536-or$errorText.Length-gt65536-or$process.ExitCode-ne0){throw 'Sandbox CLI failed or exceeded its output bound.'};return $text
    }finally{$process.Dispose()}
}
function Get-SandboxIds {
    $inventory=Invoke-SandboxCli @('list','--raw')|ConvertFrom-Json
    if(-not$inventory-or-not$inventory.PSObject.Properties['WindowsSandboxEnvironments']){throw 'Unknown Sandbox inventory schema.'}
    foreach($entry in $inventory.WindowsSandboxEnvironments){$id=[Guid]::Empty;if(-not[Guid]::TryParse([string]$entry.Id,[ref]$id)){throw 'Invalid Sandbox ID.'};$id.ToString('D')}
}
if(@(Get-SandboxIds).Count){throw 'Another Sandbox exists; refusing to start, adopt, connect to or stop it.'}
$ownedId=[Guid]::NewGuid().ToString('D');$attempted=$false;$connection=$null
try{
    if(@(Get-SandboxIds).Count){throw 'A Sandbox appeared before startup; refusing to proceed.'}
    $attempted=$true;Invoke-SandboxCli @('start','--id',$ownedId,'--config',$xml,'--raw')|Out-Null
    $connection=Start-Process -FilePath $wsb -ArgumentList @('connect','--id',$ownedId) -WindowStyle Hidden -PassThru
    $deadline=[DateTimeOffset]::UtcNow.AddMinutes(35);$resultFile=Join-Path $output 'result.json'
    while(-not(Test-Path -LiteralPath $resultFile -PathType Leaf)){
        if([DateTimeOffset]::UtcNow-ge$deadline){throw 'Bridge Sandbox scenario exceeded its bounded deadline.'};Start-Sleep -Seconds 1
    }
    $result=Get-Content -LiteralPath $resultFile -Raw|ConvertFrom-Json
    if($result.status-cne'PASS'){throw "Bridge scenario is $($result.status); see owned Sandbox results."}
    Write-Output "RUN - PASS: installed published C# 1.6.0 to bridge 1.6.1 to native 1.7.0. Results: $root"
}
finally{
    if($attempted-and@(Get-SandboxIds)-contains$ownedId){
        try{Invoke-SandboxCli @('stop','--id',$ownedId,'--raw')|Out-Null}catch{if(@(Get-SandboxIds)-contains$ownedId){throw}}
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
        while(@(Get-SandboxIds)-contains$ownedId){if([DateTimeOffset]::UtcNow-ge$deadline){throw 'Owned Sandbox did not stop.'};Start-Sleep -Milliseconds 200}
    }
    if($connection){$connection.Dispose()}
}
