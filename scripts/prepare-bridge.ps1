#Requires -Version 7.0
param(
    [string]$OutputDirectory = ('artifacts/bridge-1.6.1-prepared-' + [Guid]::NewGuid().ToString('N')),
    [switch]$Verify, [switch]$Publish, [switch]$Installer, [switch]$DownloadBaseline,
    [string]$InnoCompiler, [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot
$baseCommit = '523e33b40a85e28cdb972f9401aba02ea02855d8'
$baselineSha256 = '73d993416a298690f26891936d3b794179edc62ad86e67a7144d695840c2dd97'
$patch = Join-Path $repository 'compat/bridge-1.6.1.patch'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))

function Assert-RegularPath([string]$Path) {
    for ($current = [IO.Path]::GetFullPath($Path); $current; $current = [IO.Path]::GetDirectoryName($current)) {
        try { $attributes=[IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Bridge paths must not contain reparse points.'
        }
    }
}
function Assert-FreshBridgeDirectory([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($full) -ine $artifactRoot -or
        [IO.Path]::GetFileName($full) -cnotmatch '^bridge-1\.6\.1-[A-Za-z0-9-]+$') {
        throw 'Bridge preparation only owns a named direct child of repository artifacts.'
    }
    Assert-RegularPath $full
    if (Test-Path -LiteralPath $full) { throw 'Bridge preparation requires a new directory.' }
    return $full
}
function Assert-ScopedPatch([string]$Path) {
    Assert-RegularPath $Path
    if ((Get-Item -LiteralPath $Path).Length -gt 1024 * 1024) { throw 'Bridge patch exceeds its size limit.' }
    $allowed = @(
        'Directory.Build.props','installer/net-lights.iss',
        'src/NetLights.App/Hosting/Program.cs','src/NetLights.App/Hosting/NetLightsContext.cs',
        'src/NetLights.App/Hosting/SilentUpdateRuntime.cs','src/NetLights.UpdateAgent/Program.cs',
        'src/NetLights.Updates/InstallationSnapshot.cs','src/NetLights.Updates/OwnedInstallationMetadata.cs',
        'src/NetLights.Updates/PendingUpdateStore.cs','src/NetLights.Updates/UpdateRetryStore.cs',
        'src/NetLights.Updates/UpdatePolicy.cs',
        'src/NetLights.Updates/SilentSetupLauncher.cs','src/NetLights.Updates/SilentUpdateCoordinator.cs',
        'tests/NetLights.UnitTests/UpdateRetryStoreTests.cs','tests/NetLights.UnitTests/OwnedInstallationMetadataTests.cs',
        'tests/NetLights.UnitTests/AgentSilentSetupInstallerTests.cs','tests/NetLights.UnitTests/SilentUpdateCoordinatorTests.cs',
        'tests/NetLights.UnitTests/BridgeSnapshotTests.cs','tests/NetLights.IntegrationTests/UpdateAgentProgramTests.cs',
        'tests/NetLights.TestApp/Program.cs','tests/NetLights.TestInstaller/Program.cs'
    ) + @('App','Core','Networking','Updates','UpdateAgent' | ForEach-Object { "src/NetLights.$_/packages.publish.lock.json" })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -cmatch '^diff --git a/(.+) b/(.+)$') {
            if ($Matches[1] -cne $Matches[2] -or $Matches[1] -cnotin $allowed -or -not $seen.Add($Matches[1])) {
                throw 'Bridge patch contains an unrelated, renamed or repeated file.'
            }
        }
        elseif ($line -cmatch '^(new file mode|old mode|new mode) (?!100644$)' -or
                $line -cmatch '^(GIT binary patch|rename |copy |deleted file mode)') {
            throw 'Bridge patch cannot alter file types, remove files or contain binary payloads.'
        }
    }
    if ($seen.Count -lt 11) { throw 'Bridge patch is incomplete.' }
}

if (-not ('NetLightsBridgeProcess' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
public static class NetLightsBridgeProcess {
    public static int Run(string executable, string[] arguments, string directory, string log, int seconds) {
        var start = new ProcessStartInfo(executable) { WorkingDirectory=directory, UseShellExecute=false,
            CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var output = new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        object gate = new object(); long written = 0; const long limit = 16L*1024*1024;
        Action<Stream> drain = stream => { byte[] buffer=new byte[16384]; int count;
            while ((count=stream.Read(buffer,0,buffer.Length))!=0) lock(gate) {
                int keep=(int)Math.Min(count,Math.Max(0,limit-written));
                output.Write(buffer,0,keep); written+=keep;
            }
        };
        using var process = new Process { StartInfo=start };
        if (!process.Start()) throw new IOException("Bridge child did not start.");
        Task stdout=Task.Run(()=>drain(process.StandardOutput.BaseStream));
        Task stderr=Task.Run(()=>drain(process.StandardError.BaseStream));
        if (!process.WaitForExit(checked(seconds*1000))) {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            process.WaitForExit(10000);
            if (!Task.WaitAll(new[]{stdout,stderr},10000)) throw new TimeoutException("Bridge child output did not close.");
            throw new TimeoutException("Bridge child exceeded its deadline.");
        }
        if (!Task.WaitAll(new[]{stdout,stderr},10000)) throw new TimeoutException("Bridge child output did not close.");
        output.Flush(true);
        if (written>=limit) throw new IOException("Bridge log reached its limit; verification is incomplete.");
        return process.ExitCode;
    }
}
'@
}
function Invoke-BridgeTool([string]$File, [string[]]$Arguments, [string]$WorkingDirectory, [string]$Log, [int]$Seconds=600) {
    if ([NetLightsBridgeProcess]::Run($File,$Arguments,$WorkingDirectory,$Log,$Seconds) -ne 0) {
        throw "Bridge command failed. See $Log"
    }
}
function Assert-CompletedTrx([string]$Path) {
    if(-not(Test-Path -LiteralPath $Path -PathType Leaf)-or(Get-Item -LiteralPath $Path).Length-gt16MB){throw 'Missing or oversized bridge test receipt.'}
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null;$settings.MaxCharactersInDocument=16MB
    $reader=[Xml.XmlReader]::Create($Path,$settings)
    try{$document=[Xml.XmlDocument]::new();$document.Load($reader)}finally{$reader.Dispose()}
    $counts=$document.TestRun.ResultSummary.Counters
    if([int]$counts.total -le 0-or[int]$counts.passed-ne[int]$counts.total-or[int]$counts.failed-ne0-or[int]$counts.notExecuted-ne0){throw 'Bridge test receipt contains a failure, skipped scenario or no executed tests.'}
    return @{total=[int]$counts.total;passed=[int]$counts.passed;failed=[int]$counts.failed;notExecuted=[int]$counts.notExecuted}
}
function Get-BridgeSourceState([string]$Directory) {
    @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Where-Object {
        [IO.Path]::GetRelativePath($Directory,$_.FullName).Replace('\','/') -notmatch '(^|/)(bin|obj|TestResults|artifacts)/'
    } | Sort-Object FullName | ForEach-Object {@{path=[IO.Path]::GetRelativePath($Directory,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
}
function Get-PinnedBaseline([string]$Destination) {
    $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false
    $client=[Net.Http.HttpClient]::new($handler);$cancel=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(180))
    $temporary=$Destination+'.partial';$response=$null;$stream=$null;$output=$null
    try {
        $url=[uri]'https://github.com/AryaPaw/net-lights/releases/download/v1.6.0/NetLights-Setup-win-x64-1.6.0.exe'
        for($redirect=0;$redirect -le 5;$redirect++) {
            if($url.Scheme -cne 'https' -or $url.Port -ne 443 -or $url.UserInfo -or
                $url.Host -cnotin @('github.com','objects.githubusercontent.com','release-assets.githubusercontent.com','github-releases.githubusercontent.com')) { throw 'Untrusted baseline download address.' }
            $response=$client.GetAsync($url,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cancel.Token).GetAwaiter().GetResult()
            if([int]$response.StatusCode -in @(301,302,303,307,308)) {
                if($redirect -eq 5 -or -not $response.Headers.Location) { throw 'Baseline redirect limit exceeded.' }
                $url=[uri]::new($url,$response.Headers.Location);$response.Dispose();$response=$null;continue
            }
            if([int]$response.StatusCode -ne 200) { throw 'Baseline download failed.' }
            break
        }
        if($response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -ne 59140690) { throw 'Unexpected published baseline size.' }
        $stream=$response.Content.ReadAsStreamAsync($cancel.Token).GetAwaiter().GetResult()
        $output=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        $buffer=[byte[]]::new(65536);$total=0L
        while(($count=$stream.ReadAsync($buffer,0,$buffer.Length,$cancel.Token).GetAwaiter().GetResult()) -gt 0) {
            $total+=$count;if($total -gt 59140690){throw 'Baseline download exceeded the exact published size.'}
            $output.Write($buffer,0,$count)
        }
        $output.Flush($true);$output.Dispose();$output=$null
        if($total -ne 59140690 -or (Get-FileHash -LiteralPath $temporary).Hash -ine $baselineSha256) { throw 'Baseline digest does not match the exact published release.' }
        [IO.File]::Move($temporary,$Destination)
    }
    finally {if($output){$output.Dispose()};if($stream){$stream.Dispose()};if($response){$response.Dispose()};$client.Dispose();$handler.Dispose();$cancel.Dispose()}
}

if ($SelfTest) {
    if ($Verify -or $Publish -or $Installer -or $DownloadBaseline) { throw 'SelfTest cannot execute a bridge build.' }
    Assert-ScopedPatch $patch
    foreach ($invalid in @($repository, (Join-Path $artifactRoot 'publish'), (Join-Path $artifactRoot 'bridge-1.7.0-wrong'))) {
        $rejected=$false; try { $null=Assert-FreshBridgeDirectory $invalid } catch { $rejected=$true }
        if (-not $rejected) { throw 'Bridge ownership negative control failed.' }
    }
    $existing=Join-Path $artifactRoot ('bridge-1.6.1-selftest-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $existing | Out-Null
    [IO.File]::WriteAllText((Join-Path $existing 'sentinel'),'preserved')
    $rejected=$false; try { $null=Assert-FreshBridgeDirectory $existing } catch { $rejected=$true }
    if (-not $rejected -or [IO.File]::ReadAllText((Join-Path $existing 'sentinel')) -cne 'preserved') { throw 'Existing tree was not protected.' }
    $badPatch=Join-Path $existing 'foreign.patch'
    $bad=[IO.File]::ReadAllText($patch).Replace('diff --git a/Directory.Build.props b/Directory.Build.props','diff --git a/../foreign.txt b/../foreign.txt')
    [IO.File]::WriteAllText($badPatch,$bad)
    $rejected=$false;try{Assert-ScopedPatch $badPatch}catch{$rejected=$true}
    if(-not $rejected){throw 'Foreign patch path negative control failed.'}
    [IO.File]::WriteAllText($badPatch,([IO.File]::ReadAllText($patch)+"`nnew file mode 120000`n"))
    $rejected=$false;try{Assert-ScopedPatch $badPatch}catch{$rejected=$true}
    if(-not $rejected){throw 'Symlink patch negative control failed.'}
    $trx=Join-Path $existing 'control.trx'
    [IO.File]::WriteAllText($trx,'<TestRun><ResultSummary><Counters total="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>')
    $null=Assert-CompletedTrx $trx
    foreach($counters in @('total="0" passed="0" failed="0" notExecuted="0"','total="1" passed="0" failed="1" notExecuted="0"','total="1" passed="0" failed="0" notExecuted="1"')){
        [IO.File]::WriteAllText($trx,('<TestRun><ResultSummary><Counters '+$counters+' /></ResultSummary></TestRun>'))
        $rejected=$false;try{$null=Assert-CompletedTrx $trx}catch{$rejected=$true}
        if(-not$rejected){throw 'Incomplete test receipt negative control failed.'}
    }
    Write-Output 'RUN - PASS: bridge patch scope, path ownership and existing-tree negative controls. No build or installation ran.'
    return
}
if ($Installer -and -not $Publish) { throw 'Installer requires Publish.' }
Assert-ScopedPatch $patch
$requested=if([IO.Path]::IsPathFullyQualified($OutputDirectory)){$OutputDirectory}else{Join-Path $repository $OutputDirectory}
$taskRoot = Assert-FreshBridgeDirectory ([IO.Path]::GetFullPath($requested))
$git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
New-Item -ItemType Directory -Path $taskRoot | Out-Null
$source=Join-Path $taskRoot 'source'
$evidence=[ordered]@{version='1.6.1';baseCommit=$baseCommit;patchSha256=(Get-FileHash -LiteralPath $patch).Hash;prepare='NOT RUN';build='NOT RUN';unit='NOT RUN';integration='NOT RUN';soak='NOT RUN';coverage='NOT RUN';publish='NOT RUN';installer='NOT RUN';sandbox='NOT RUN'}
$oldTelemetry=$env:DOTNET_CLI_TELEMETRY_OPTOUT
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    $archive=Join-Path $taskRoot 'base.zip'
    Invoke-BridgeTool $git @('archive','--format=zip',"--output=$archive",$baseCommit) $repository (Join-Path $taskRoot 'archive.log') 60
    Expand-Archive -LiteralPath $archive -DestinationPath $source
    $prefix=[IO.Path]::GetRelativePath($repository,$source).Replace('\','/')
    Invoke-BridgeTool $git @('apply','--check',"--directory=$prefix",$patch) $repository (Join-Path $taskRoot 'patch-check.log') 60
    Invoke-BridgeTool $git @('apply',"--directory=$prefix",$patch) $repository (Join-Path $taskRoot 'patch-apply.log') 60
    [xml]$props=Get-Content -LiteralPath (Join-Path $source 'Directory.Build.props') -Raw
    if ($props.Project.PropertyGroup.Version -cne '1.6.1') { throw 'Bridge version is not 1.6.1.' }
    foreach ($preserved in @('src/NetLights.Updates/UpdateCoordinator.cs','tests/NetLights.UnitTests/UpdateCoordinatorTests.cs','tests/NetLights.UnitTests/SilentSetupLauncherTests.cs')) {
        if (-not (Test-Path -LiteralPath (Join-Path $source $preserved) -PathType Leaf)) { throw 'A baseline API or test was removed.' }
    }
    $evidence.prepare='PASS'
    $sourceState=@(Get-BridgeSourceState $source)
    [IO.File]::WriteAllText((Join-Path $taskRoot 'source.json'),($sourceState|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
    $evidence.sourceManifestSha256=(Get-FileHash -LiteralPath (Join-Path $taskRoot 'source.json')).Hash
    if ($Verify -or $Publish) {
        Invoke-BridgeTool $dotnet @('restore','NetLights.slnx','--locked-mode','--disable-parallel','-maxcpucount:4') $source (Join-Path $taskRoot 'restore.log') 300
        Invoke-BridgeTool $dotnet @('build','NetLights.slnx','-c','Release','--no-restore','-maxcpucount:4') $source (Join-Path $taskRoot 'build.log') 300
        $evidence.build='PASS'
    }
    if ($Verify) {
        foreach ($suite in @('Unit','Integration','Soak')) {
            $name=$suite.ToLowerInvariant(); $results=Join-Path $taskRoot "tests/$name"
            $arguments=@('test',"tests/NetLights.$($suite)Tests/NetLights.$($suite)Tests.csproj",'-c','Release','--no-build','--no-restore','--logger',"trx;LogFileName=$name.trx",'--results-directory',$results)
            if($suite -ne 'Soak'){$arguments+=@('--collect:XPlat Code Coverage','--settings','tests/coverlet.runsettings')}
            $arguments+=@('--','RunConfiguration.TestSessionTimeout=600000')
            Invoke-BridgeTool $dotnet $arguments $source (Join-Path $taskRoot "$name.log") 660
            $evidence[$name+'Counts']=Assert-CompletedTrx (Join-Path $results "$name.trx")
            $evidence[$name]='PASS'
        }
        $shell=(Get-Process -Id $PID).Path
        Invoke-BridgeTool $shell @('-NoProfile','-File',(Join-Path $source 'scripts/check-coverage.ps1'),'-ResultsDirectory',(Join-Path $taskRoot 'tests')) $source (Join-Path $taskRoot 'coverage.log') 60
        $evidence.coverage='PASS'
    }
    if ($Publish) {
        $output=Join-Path $taskRoot 'publish'
        foreach ($project in @('App','UpdateAgent')) {
            $projectPath="src/NetLights.$project/NetLights.$project.csproj"
            Invoke-BridgeTool $dotnet @('restore',$projectPath,'-r','win-x64','-p:PublishSingleFile=true','-p:SelfContained=true','-p:NuGetLockFilePath=packages.publish.lock.json','--locked-mode','--disable-parallel','-maxcpucount:4') $source (Join-Path $taskRoot "restore-publish-$project.log") 300
        }
        foreach ($project in @('App','UpdateAgent')) {
            Invoke-BridgeTool $dotnet @('publish',"src/NetLights.$project/NetLights.$project.csproj",'-c','Release','-r','win-x64','--self-contained','true','--no-restore','-p:NuGetLockFilePath=packages.publish.lock.json','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:DebugType=none','-o',$output,'-maxcpucount:4') $source (Join-Path $taskRoot "publish-$project.log") 600
        }
        $evidence.publish='PASS'
        $evidence.binaries=@(Get-ChildItem -LiteralPath $output -File | ForEach-Object {
            if ($_.Extension -eq '.exe' -and ($_.VersionInfo.ProductVersion -split '\+')[0] -cne '1.6.1') { throw 'Published bridge executable has the wrong version.' }
            @{name=$_.Name;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
        })
        if ($Installer) {
            if (-not $InnoCompiler) {
                $InnoCompiler=@("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe","$env:LOCALAPPDATA\Programs\InnoSetup6\ISCC.exe") | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
            }
            if (-not $InnoCompiler) { throw 'Inno Setup compiler is required.' }
            $setup=Join-Path $taskRoot 'setup'; New-Item -ItemType Directory -Path $setup | Out-Null
            Invoke-BridgeTool $InnoCompiler @('/DMyAppVersion=1.6.1',"/DPublishDir=$output","/O$setup",'installer/net-lights.iss') $source (Join-Path $taskRoot 'installer.log') 600
            $installerFile=Join-Path $setup 'NetLights-Setup-win-x64-1.6.1.exe'
            $length=(Get-Item -LiteralPath $installerFile).Length
            if ($length -le 0 -or $length -gt 80MB) { throw 'Bridge installer exceeds the legacy updater limit.' }
            $evidence.installer='PASS';$evidence.installerSha256=(Get-FileHash -LiteralPath $installerFile).Hash
        }
    }
    if ($DownloadBaseline) {
        $baseline=Join-Path $taskRoot 'NetLights-Setup-win-x64-1.6.0.exe'
        Get-PinnedBaseline $baseline
        $evidence.baselineSha256=$baselineSha256
    }
    foreach($entry in $sourceState){
        $file=Join-Path $source $entry.path
        if(-not(Test-Path -LiteralPath $file -PathType Leaf)-or(Get-FileHash -LiteralPath $file).Hash -cne $entry.sha256){throw 'Bridge source changed during verification.'}
    }
    if(@(Get-BridgeSourceState $source).Count -ne $sourceState.Count){throw 'Bridge source inventory changed during verification.'}
}
finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT=$oldTelemetry
    [IO.File]::WriteAllText((Join-Path $taskRoot 'bridge.json'),($evidence|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
}
Write-Output "Bridge prepared: $taskRoot. Installed/user-data copies and publication were not changed."
