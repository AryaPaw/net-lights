#requires -Version 7.5
param(
    [string]$Executable,
    [string]$ResultsDirectory = ('TestResults/native-ui-isolated-' + [Guid]::NewGuid().ToString('N')),
    [string]$ExpectedSha256,
    [switch]$FunctionalUI,
    [string]$RawProfileDirectory,
    [switch]$CoverageInstrumented,
    [ValidateRange(0,3600)][int]$MeasureSeconds = 0,
    [switch]$EnableCountryForMeasurement,
    [switch]$MaximumHistory,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'ci-environment.ps1')
$captureSource = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
public static class NativeUiClient {
    private delegate bool EnumProc(IntPtr hwnd,IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct Rect {public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)] private struct Placement {public uint Size,Flags,Show; public Point Minimum,Maximum;public Rect Normal;}
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback,IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd,ref Placement placement);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd,int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    public sealed class State {public long Hwnd; public uint ProcessId;public bool Visible,Iconic;}
    public static uint Owner(long hwnd) {uint process;GetWindowThreadProcessId(new IntPtr(hwnd),out process);return process;}
    public static long Foreground() {return GetForegroundWindow().ToInt64();}
    public static State Read(long hwnd) {return new State{Hwnd=hwnd,ProcessId=Owner(hwnd),Visible=IsWindowVisible(new IntPtr(hwnd)),Iconic=IsIconic(new IntPtr(hwnd))};}
    public static State[] Windows(int[] processes) {
        var owned=new HashSet<int>(processes);var result=new List<State>();
        EnumProc callback=delegate(IntPtr hwnd,IntPtr data) {
            uint process;GetWindowThreadProcessId(hwnd,out process);
            if(!owned.Contains((int)process))return true;
            var placement=new Placement();placement.Size=(uint)Marshal.SizeOf(typeof(Placement));
            if(GetWindowPlacement(hwnd,ref placement) && placement.Normal.Right-placement.Normal.Left>=320 && placement.Normal.Bottom-placement.Normal.Top>=240)
                result.Add(Read(hwnd.ToInt64()));
            return result.Count<32;
        };
        EnumWindows(callback,IntPtr.Zero);GC.KeepAlive(callback);return result.ToArray();
    }
    public static void Background(long hwnd,uint expectedProcess) {
        if(Owner(hwnd)!=expectedProcess)throw new InvalidOperationException("Owned client HWND changed process.");
        if(!IsWindowVisible(new IntPtr(hwnd)) || IsIconic(new IntPtr(hwnd)))
            ShowWindowAsync(new IntPtr(hwnd),4); // SW_SHOWNOACTIVATE
        if(!SetWindowPos(new IntPtr(hwnd),new IntPtr(1),0,0,0,0,0x4213)) // BOTTOM, ASYNC, NOACTIVATE, NOMOVE, NOSIZE, NOOWNERZORDER
            throw new InvalidOperationException("Owned client couldn't be placed behind the user's window.");
    }
}
public static class NativeUiCommand {
    private static string Quote(string value) {
        var text = new StringBuilder("\""); int slashes = 0;
        foreach(char c in value) {
            if(c == '\\') { slashes++; continue; }
            if(c == '"') { text.Append('\\',slashes*2+1); text.Append(c); slashes=0; continue; }
            text.Append('\\',slashes); slashes=0; text.Append(c);
        }
        text.Append('\\',slashes*2); return text.Append('"').ToString();
    }
    private static async Task Drain(Stream input,string path) {
        using(var output = new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)) {
            byte[] bytes = new byte[8192]; int remaining = 131072;
            for(;;) {
                int count = await input.ReadAsync(bytes,0,bytes.Length);
                if(count == 0) break;
                int keep = Math.Min(remaining,count);
                if(keep > 0) await output.WriteAsync(bytes,0,keep);
                remaining -= keep;
            }
        }
    }
    public static int Run(string executable,string[] arguments,string directory,Dictionary<string,string> environment,string stdout,string stderr,int timeout,bool tree) {
        var start = new ProcessStartInfo(executable);
        start.UseShellExecute=false; start.CreateNoWindow=true;
        start.RedirectStandardOutput=true; start.RedirectStandardError=true;
        start.WorkingDirectory=directory;
        var quoted = new List<string>(); foreach(string argument in arguments) quoted.Add(Quote(argument));
        start.Arguments=string.Join(" ",quoted.ToArray());
        if(environment != null) foreach(var pair in environment) {
            if(pair.Value == null) start.EnvironmentVariables.Remove(pair.Key); else start.EnvironmentVariables[pair.Key]=pair.Value;
        }
        using(var process = Process.Start(start)) {
            var output=Drain(process.StandardOutput.BaseStream,stdout);
            var error=Drain(process.StandardError.BaseStream,stderr);
            try {
                if(!process.WaitForExit(timeout)) throw new TimeoutException("Owned verification command timed out.");
                if(!Task.WaitAll(new Task[]{output,error},5000)) throw new TimeoutException("Owned command output didn't close.");
                return process.ExitCode;
            } finally {
                if(!process.HasExited) {
                    var method = tree ? typeof(Process).GetMethod("Kill",new Type[]{typeof(bool)}) : null;
                    if(method != null) method.Invoke(process,new object[]{true}); else process.Kill();
                    process.WaitForExit(5000);
                }
                process.StandardOutput.Dispose(); process.StandardError.Dispose();
            }
        }
    }
}
'@
if (-not ('NativeUiCommand' -as [type])) { Add-Type -TypeDefinition $captureSource }

function Assert-Unlinked([string]$Path) {
    for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)) {
        try{$attributes=[IO.File]::GetAttributes($part)}
        catch [IO.FileNotFoundException]{continue}
        catch [IO.DirectoryNotFoundException]{continue}
        if($attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Verification path contains a reparse point.'}
    }
}
function Assert-ArtifactPath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path,$repository)
    $allowed=$false
    foreach($name in @('artifacts','TestResults')) {
        if($full.StartsWith(([IO.Path]::GetFullPath((Join-Path $repository $name))+'\'),[StringComparison]::OrdinalIgnoreCase)) {$allowed=$true}
    }
    if(-not $allowed) {throw 'Verification output must be a child of artifacts or TestResults.'}
    Assert-Unlinked $full
    return $full
}
function Write-NewJson([string]$Path,$Value) {
    $stream=[IO.File]::Open($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {$bytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Value -Depth 32));$stream.Write($bytes)}finally{$stream.Dispose()}
}
function Invoke-CommandBounded([string]$Name,[string]$File,[string[]]$Arguments,[int]$Seconds=60,[hashtable]$Environment=@{},[switch]$ClientOnly) {
    $changes=[Collections.Generic.Dictionary[string,string]]::new()
    foreach($key in $Environment.Keys) {$changes.Add($key,$Environment[$key])}
    $stdout=Join-Path $results ($Name+'.stdout.log'); $stderr=Join-Path $results ($Name+'.stderr.log')
    $code=[NativeUiCommand]::Run($File,$Arguments,$repository,$changes,$stdout,$stderr,$Seconds*1000,(-not $ClientOnly))
    if($code -ne 0){throw "Owned $Name command failed with exit $code; see bounded logs."}
    return [IO.File]::ReadAllText($stdout)
}
function Test-ClientImage([string]$Path) {
    if(-not $Path){return $false}
    $full=[IO.Path]::GetFullPath($Path)
    foreach($name in @('WindowsSandbox.exe','WindowsSandboxClient.exe')) {
        if($full -ieq (Join-Path ([Environment]::SystemDirectory) $name)){return $true}
    }
    $programFiles=[Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $prefix=Join-Path $programFiles 'WindowsApps'
    if(-not $full.StartsWith($prefix+'\',[StringComparison]::OrdinalIgnoreCase)){return $false}
    return $full.Substring($prefix.Length+1) -imatch '^MicrosoftWindows\.WindowsSandbox_[0-9]+(?:\.[0-9]+){3}_(?:x64|arm64)__cw5n1h2txyewy\\WindowsSandboxRemoteSession\.exe$'
}
function Resolve-RuntimeRoot([string]$BasePrefix,[string]$BaseExecutable,[string]$Version) {
    $reported=[IO.Path]::GetFullPath($BasePrefix)
    $reportedExe=[IO.Path]::GetFullPath($BaseExecutable)
    if($reportedExe -ine (Join-Path $reported 'python.exe')){throw 'Reported Python executable is outside its runtime root.'}
    $item=Get-Item -LiteralPath $reported -Force
    $canonical=$reported
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        # uv exposes a minor-version junction to its versioned sibling. Resolve
        # this read-only source only; all destinations and descendants stay strict.
        $match=[regex]::Match($Version,'^(?<minor>3\.[0-9]+)\.[0-9]+')
        if(-not $match.Success -or $item.LinkType -cne 'Junction' -or $item.Name -cne ('cpython-'+$match.Groups['minor'].Value+'-windows-x86_64-none')){throw 'Unsupported Python runtime source link.'}
        Assert-Unlinked ([IO.Path]::GetDirectoryName($reported))
        $target=$item.ResolveLinkTarget($false)
        if(-not $target -or $target.Attributes -band [IO.FileAttributes]::ReparsePoint -or
           [IO.Path]::GetDirectoryName($target.FullName) -ine [IO.Path]::GetDirectoryName($reported) -or
           $target.Name -cne ('cpython-'+$match.Value+'-windows-x86_64-none')){throw 'Python alias does not point directly to its exact-version sibling.'}
        $canonical=$target.FullName
    }
    Assert-Unlinked $canonical
    $canonicalExe=Join-Path $canonical 'python.exe';Assert-Unlinked $canonicalExe
    $identity=(Get-FileHash -LiteralPath $canonicalExe -Algorithm SHA256).Hash
    if($identity -ine (Get-FileHash -LiteralPath $reportedExe -Algorithm SHA256).Hash){throw 'Reported and canonical Python executables differ.'}
    return [pscustomobject]@{Root=$canonical;ExecutableSha256=$identity}
}
function Assert-Amd64Pe([string]$Path) {
    Assert-Unlinked $Path
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $reader=[IO.BinaryReader]::new($stream)
    try {
        if($stream.Length -lt 64 -or $stream.Length -gt 16MB -or $reader.ReadUInt16() -ne 0x5a4d){throw 'Expected a bounded AMD64 PE runtime library.'}
        $stream.Position=0x3c;$offset=$reader.ReadUInt32()
        if($offset -gt $stream.Length-6){throw 'Runtime PE header is outside the file.'}
        $stream.Position=$offset
        if($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664){throw 'Runtime library is not an AMD64 PE.'}
    }finally{$reader.Dispose()}
}
function Assert-UiEvidence($Evidence,[string]$Scope,[string]$Nonce,[string]$AppHash,[string]$RunnerHash,[bool]$ExpectCoverageInstrumented=$false) {
    $smoothness=if($ExpectCoverageInstrumented){'NOT RUN'}else{'PASS'}
    if($Evidence.metadata.coverage_instrumented -isnot [bool] -or $Evidence.metadata.coverage_instrumented -ne $ExpectCoverageInstrumented -or
       $Evidence.metadata.navigation_smoothness -cne $smoothness) {throw 'UI coverage mode or navigation smoothness evidence differs from the requested run.'}
    if($Evidence.metadata.exe_sha256 -ine $AppHash -or $Evidence.metadata.physical_input -cne $true -or
        $Evidence.metadata.input_isolation.scope -cne $Scope -or
        $Evidence.metadata.fixture.offline_availability_endpoints -cne $true -or
        $Evidence.metadata.fixture.live_country_detection -cne $false -or
        @($Evidence.checks|Where-Object {$_.result -cne 'PASS' -and -not($_.check -ceq 'resource-measurement' -and $_.result -ceq 'NOT RUN')}).Count -or
        @($Evidence.cleanup_errors).Count -or @($Evidence.resource_errors).Count) {throw 'Isolated UI evidence is failed, incomplete or belongs to another input.'}
    if($Scope -ceq 'Windows Sandbox' -and ($Evidence.metadata.input_isolation.nonce -cne $Nonce -or
       $Evidence.metadata.input_isolation.app_sha256 -ine $AppHash -or $Evidence.metadata.input_isolation.runner_sha256 -ine $RunnerHash)) {throw 'Sandbox evidence nonce/source hashes differ.'}
    if($Evidence.metadata.assertions_enabled -cne $true -or $Evidence.metadata.locale_required -cne $true -or $Evidence.metadata.locale_verified -cne $true -or [int]$Evidence.resource_samples -lt 1) {throw 'Locale/assertion/resource evidence is incomplete.'}
    if(@($Evidence.metadata.pids).Count -ne 5 -or @($Evidence.metadata.pids|Sort-Object -Unique).Count -ne 5 -or @($Evidence.metadata.pids|Where-Object {[int]$_ -lt 1}).Count) {throw 'Actual native process identities are missing.'}
    $required=@('fluent-tabs-about-and-settings-navigation','physical-outer-geometry-reset-and-restart','native-caption-controls-and-hittest','default-debug-off',
        'dropdown-wheel-does-not-change-settings','X-hide-and-show-local-same-PID','Debug-on-and-off-survive-restart','history-end-and-single-scroll-owner',
        'state-no-duplicate-provider-world','locale-English-restart-system-Russian')
    foreach($page in @('Состояние','Локации')){foreach($hours in @(1,3,6,12,24,72,120,168)){$required+="$page-${hours}h"}}
    foreach($name in $required){$matching=@($Evidence.checks|Where-Object check -CEQ $name);if($matching.Count -ne 1 -or $matching[0].result -cne 'PASS'){throw 'Required UI matrix evidence is missing, duplicated or failed.'}}
    $navigation=@($Evidence.checks|Where-Object check -CEQ 'fluent-tabs-about-and-settings-navigation')
    if($navigation[0].detail.navigation_motion.smoothness -cne $smoothness) {throw 'Navigation detail contradicts the requested smoothness evidence.'}
}

if($SelfTest) {
    if($Executable -or $ExpectedSha256 -or $RawProfileDirectory -or $FunctionalUI -or $CoverageInstrumented -or $MeasureSeconds -or $EnableCountryForMeasurement -or $MaximumHistory) {throw 'SelfTest cannot be combined with a UI run.'}
    & uv run --offline --cache-dir (Join-Path $repository 'artifacts/uv-cache') --python 3.11 python (Join-Path $PSScriptRoot 'native-ui-smoke.py') --self-test-uia
    if($LASTEXITCODE -ne 0){throw 'Read-only UIA retry controls FAIL.'}
    $rejected=$false;try{Assert-ArtifactPath (Join-Path $repository 'native')|Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw 'A source directory was accepted as an output.'}
    if(Test-ClientImage (Join-Path ([Environment]::SystemDirectory) 'conhost.exe')){throw 'A console process was accepted as a Sandbox UI client.'}
    if(Test-ClientImage (Join-Path $repository 'WindowsSandbox.exe')){throw 'A same-name non-system image was accepted as a Sandbox client.'}
    if(-not(Test-ClientImage (Join-Path ([Environment]::SystemDirectory) 'WindowsSandboxClient.exe'))){throw 'A system Sandbox client path was rejected.'}
    $storeImage=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'WindowsApps/MicrosoftWindows.WindowsSandbox_0.8.114.0_x64__cw5n1h2txyewy/WindowsSandboxRemoteSession.exe'
    if(-not(Test-ClientImage $storeImage)){throw 'The observed protected Sandbox client was rejected.'}
    foreach($badImage in @($storeImage.Replace('cw5n1h2txyewy','untrustedpublisher'),$storeImage.Replace('WindowsSandboxRemoteSession.exe','WindowsSandboxServer.exe'),$storeImage.Replace('MicrosoftWindows.WindowsSandbox_','Other.WindowsSandbox_'))) {
        if(Test-ClientImage $badImage){throw 'An unrelated package publisher, image or family was accepted as the Sandbox client.'}
    }
    $functionText=${function:Assert-UiEvidence}.ToString()
    if(-not $functionText.Contains("'locale-English-restart-system-Russian'") -or -not $functionText.Contains("'state-no-duplicate-provider-world'")){throw 'Locale acceptance guards are missing.'}
    $good=@{metadata=@{exe_sha256=('a'*64);physical_input=$true;input_isolation=@{scope='Windows Sandbox';nonce=('b'*32);app_sha256=('a'*64);runner_sha256=('c'*64)};
        fixture=@{offline_availability_endpoints=$true;live_country_detection=$false};coverage_instrumented=$false;navigation_smoothness='PASS';assertions_enabled=$true;locale_required=$true;locale_verified=$true;pids=@(1,2,3,4,5)};
        cleanup_errors=@();resource_errors=@();resource_samples=1;checks=@()}
    $names=@('fluent-tabs-about-and-settings-navigation','physical-outer-geometry-reset-and-restart','native-caption-controls-and-hittest','default-debug-off','dropdown-wheel-does-not-change-settings','X-hide-and-show-local-same-PID','Debug-on-and-off-survive-restart','history-end-and-single-scroll-owner','state-no-duplicate-provider-world','locale-English-restart-system-Russian')
    foreach($page in @('Состояние','Локации')){foreach($hours in @(1,3,6,12,24,72,120,168)){$names+="$page-${hours}h"}}
    $good.checks=@($names|ForEach-Object {@{check=$_;result='PASS'}})+@(@{check='resource-measurement';result='NOT RUN'})
    $good.checks[0].detail=@{navigation_motion=@{smoothness='PASS'}}
    Assert-UiEvidence ([pscustomobject]$good) 'Windows Sandbox' ('b'*32) ('a'*64) ('c'*64)
    foreach($kind in @('nonce','hash','physical','pid','missing-check','failed-check','locale','cleanup','instrumented-mode','instrumented-type','smoothness','motion-smoothness')) {
        $bad=ConvertFrom-Json (ConvertTo-Json $good -Depth 12)
        switch($kind){
            'nonce'{$bad.metadata.input_isolation.nonce='different'}
            'hash'{$bad.metadata.exe_sha256='different'}
            'physical'{$bad.metadata.physical_input=$false}
            'pid'{$bad.metadata.pids=@(1,2,3,4,4)}
            'missing-check'{$bad.checks=@($bad.checks|Where-Object check -CNE 'state-no-duplicate-provider-world')}
            'failed-check'{$bad.checks[0].result='FAIL'}
            'locale'{$bad.metadata.locale_verified=$false}
            'cleanup'{$bad.cleanup_errors=@('owned cleanup failed')}
            'instrumented-mode'{$bad.metadata.coverage_instrumented=$true}
            'instrumented-type'{$bad.metadata.coverage_instrumented='false'}
            'smoothness'{$bad.metadata.navigation_smoothness='NOT RUN'}
            'motion-smoothness'{$bad.checks[0].detail.navigation_motion.smoothness='NOT RUN'}
        }
        $rejected=$false;try{Assert-UiEvidence $bad 'Windows Sandbox' ('b'*32) ('a'*64) ('c'*64)}catch{$rejected=$true}
        if(-not $rejected){throw "Invalid $kind evidence was accepted."}
    }
    $instrumented=ConvertFrom-Json (ConvertTo-Json $good -Depth 12)
    $instrumented.metadata.coverage_instrumented=$true
    $instrumented.metadata.navigation_smoothness='NOT RUN'
    $instrumented.checks[0].detail.navigation_motion.smoothness='NOT RUN'
    Assert-UiEvidence $instrumented 'Windows Sandbox' ('b'*32) ('a'*64) ('c'*64) $true
    foreach($kind in @('instrumented-mode','smoothness','motion-smoothness','missing-mode','missing-smoothness')) {
        $bad=ConvertFrom-Json (ConvertTo-Json $instrumented -Depth 12)
        switch($kind){
            'instrumented-mode'{$bad.metadata.coverage_instrumented=$false}
            'smoothness'{$bad.metadata.navigation_smoothness='PASS'}
            'motion-smoothness'{$bad.checks[0].detail.navigation_motion.smoothness='PASS'}
            'missing-mode'{$bad.metadata.PSObject.Properties.Remove('coverage_instrumented')}
            'missing-smoothness'{$bad.metadata.PSObject.Properties.Remove('navigation_smoothness')}
        }
        $rejected=$false;try{Assert-UiEvidence $bad 'Windows Sandbox' ('b'*32) ('a'*64) ('c'*64) $true}catch{$rejected=$true}
        if(-not $rejected){throw "Invalid instrumented $kind evidence was accepted."}
    }
    $fixture=Join-Path $repository ('artifacts/ui-runtime-controls-'+[Guid]::NewGuid().ToString('N'))
    $alias=Join-Path $fixture 'cpython-3.11-windows-x86_64-none'
    $physical=Join-Path $fixture 'cpython-3.11.15-windows-x86_64-none'
    $foreign=Join-Path $fixture 'unexpected'
    [void][IO.Directory]::CreateDirectory($physical);[void][IO.Directory]::CreateDirectory($foreign)
    [IO.File]::WriteAllText((Join-Path $physical 'python.exe'),'isolated runtime identity fixture; never executed')
    $archivePath=Join-Path $fixture 'controls.zip'
    $control=@{status='FAIL';runtimeAliasResolved=$false;wrongVersionRejected=$false;outsideSiblingRejected=$false}
    try {
        New-Item -ItemType Junction -Path $alias -Target $physical|Out-Null
        $rejected=$false;try{Assert-Unlinked (Join-Path $alias 'python.exe')}catch{$rejected=$true}
        if(-not $rejected){throw 'A runtime source junction bypassed the general write-path guard.'}
        Assert-Unlinked (Join-Path $physical 'not-yet-created/child.json')
        $resolved=Resolve-RuntimeRoot $alias (Join-Path $alias 'python.exe') '3.11.15 fixture'
        if($resolved.Root -ine $physical){throw 'Runtime alias did not resolve to its actual source.'}
        $control.runtimeAliasResolved=$true
        Assert-Amd64Pe ([Diagnostics.Process]::GetCurrentProcess().MainModule.FileName)
        $rejected=$false;try{Assert-Amd64Pe (Join-Path $physical 'python.exe')}catch{$rejected=$true}
        if(-not $rejected){throw 'A non-PE runtime fixture was accepted as an AMD64 library.'}
        $rejected=$false;try{Resolve-RuntimeRoot $alias (Join-Path $alias 'python.exe') '3.11.16 fixture'|Out-Null}catch{$rejected=$true}
        if(-not $rejected){throw 'A different exact runtime version was accepted.'};$control.wrongVersionRejected=$true
        [IO.Directory]::Delete($alias)
        New-Item -ItemType Junction -Path $alias -Target $foreign|Out-Null
        $rejected=$false;try{Resolve-RuntimeRoot $alias (Join-Path $alias 'python.exe') '3.11.15 fixture'|Out-Null}catch{$rejected=$true}
        if(-not $rejected){throw 'An unrelated source junction was accepted.'};$control.outsideSiblingRejected=$true
        $control.status='PASS'
    }finally {
        Write-NewJson (Join-Path $fixture 'controls.json') $control
        $archive=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Create)
        try{foreach($file in @((Join-Path $fixture 'controls.json'),(Join-Path $physical 'python.exe'))){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file,[IO.Path]::GetFileName($file))|Out-Null}}finally{$archive.Dispose()}
        if(Test-Path -LiteralPath $alias){if((Get-Item -LiteralPath $alias -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){[IO.Directory]::Delete($alias)}else{throw 'Owned fixture alias unexpectedly became a real directory; retained.'}}
    }
    Write-Output 'RUN - PASS: output boundary, normal/instrumented matrix positives, evidence negatives and actual uv runtime-junction controls. No VM, UI or cursor action performed.'
    return
}
if(-not $Executable -or $ExpectedSha256 -cnotmatch '^[A-Fa-f0-9]{64}$') {throw 'Supply an exact executable and SHA-256.'}
if($CoverageInstrumented -and -not $RawProfileDirectory) {throw 'CoverageInstrumented requires an explicit RawProfileDirectory.'}
if($FunctionalUI -and ($MeasureSeconds -or $EnableCountryForMeasurement)) {throw 'FunctionalUI requires offline checks without measurement.'}
if($EnableCountryForMeasurement -and -not $MeasureSeconds) {throw 'Country measurement requires a positive duration.'}
$app=[IO.Path]::GetFullPath($Executable,$repository); Assert-Unlinked $app
if(-not(Test-Path -LiteralPath $app -PathType Leaf) -or (Get-FileHash -LiteralPath $app).Hash -ine $ExpectedSha256) {throw 'The exact candidate executable hash differs.'}
$runner=Join-Path $repository 'scripts/native-ui-smoke.py'; Assert-Unlinked $runner
$runnerHash=(Get-FileHash -LiteralPath $runner -Algorithm SHA256).Hash
$nonce=[Guid]::NewGuid().ToString('N')
$results=Assert-ArtifactPath $ResultsDirectory
if(Test-Path -LiteralPath $results){throw 'The result directory must be fresh.'}
if($RawProfileDirectory){$rawTarget=Assert-ArtifactPath $RawProfileDirectory}else{$rawTarget=$null}
$hosted=Test-HostedRunner
if(-not $hosted) {
    [void][IO.Directory]::CreateDirectory($results)
    Write-NewJson (Join-Path $results 'result.json') ([ordered]@{
        status='BLOCKED';reason='Windows Sandbox RDP can synchronize the host cursor. Local interactive UI runs are disabled.';
        appSha256=$ExpectedSha256.ToUpperInvariant();runnerSha256=$runnerHash;
        sandboxStarted=$false;pointerInputPerformed=$false;checkedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    })
    throw 'BLOCKED: interactive Windows Sandbox checks can affect the host cursor. Use a disposable hosted CI VM; no Sandbox was started.'
}
$wsb=$null
if(-not $hosted){$wsb=(Get-Command wsb.exe -CommandType Application -ErrorAction Stop).Source}
[void][IO.Directory]::CreateDirectory($results)
$receipt=[ordered]@{status='FAIL';nonce=$nonce;appSha256=$ExpectedSha256.ToUpperInvariant();runnerSha256=$runnerHash;startedUtc=[DateTimeOffset]::UtcNow.ToString('o');mode=$(if($hosted){'github-hosted'}else{'windows-sandbox'});physicalHostInput=$false}
$ownedId=$null;$started=$false;$connection=$null
$clientProcesses=[Collections.Generic.Dictionary[int,Diagnostics.Process]]::new()

function Invoke-Sandbox([string[]]$Arguments,[int]$Seconds=60) {
    Invoke-CommandBounded ('wsb-'+[Guid]::NewGuid().ToString('N')) $wsb $Arguments $Seconds -ClientOnly
}
function Sandbox-Ids {
    $inventory=Invoke-Sandbox @('list','--raw')|ConvertFrom-Json
    if(-not $inventory -or -not $inventory.PSObject.Properties['WindowsSandboxEnvironments']){throw 'Unknown Sandbox inventory; no environment will be adopted.'}
    foreach($entry in $inventory.WindowsSandboxEnvironments){$id=[Guid]::Empty;if(-not[Guid]::TryParse([string]$entry.Id,[ref]$id)){throw 'Invalid Sandbox identity.'};$id.ToString('D')}
}
function Confirm-OwnedClient {
    $connectStarted=$connection.StartTime.ToUniversalTime()
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(25)
    $known=[Collections.Generic.HashSet[int]]::new();[void]$known.Add($connection.Id)
    [void]$connection.Handle
    $clientProcesses.Add($connection.Id,$connection)
    $receipt.clientConnectionPid=$connection.Id
    $receipt.clientOwnership=@()
    function Add-ClientClaim($Candidate,[string]$Proof) {
        if($known.Contains([int]$Candidate.ProcessId) -or -not(Test-ClientImage $Candidate.ExecutablePath)){return}
        if($known.Count -ge 32){throw 'Too many process claims for one owned Sandbox client.'}
        $process=$null
        try {
            $process=[Diagnostics.Process]::GetProcessById([int]$Candidate.ProcessId)
            [void]$process.Handle
            if([Math]::Abs(($process.StartTime.ToUniversalTime()-$Candidate.CreationDate.ToUniversalTime()).Ticks) -gt 10){throw 'Client process identity changed after observation.'}
            $clientProcesses.Add($process.Id,$process)
            [void]$known.Add($process.Id)
            $receipt.clientOwnership+=@(@{pid=$process.Id;proof=$Proof;systemSandboxImage=$true})
            $process=$null
        }finally{if($process){$process.Dispose()}}
    }
    $nextQuery=[DateTimeOffset]::MinValue
    while([DateTimeOffset]::UtcNow -lt $deadline) {
        if([DateTimeOffset]::UtcNow -ge $nextQuery) {
            $nextQuery=[DateTimeOffset]::UtcNow.AddSeconds(2)
            $matching=@(Get-CimInstance Win32_Process -Filter "CommandLine LIKE '%$ownedId%'" -OperationTimeoutSec 2 -ErrorAction Stop)
            foreach($candidate in $matching) {
                if($candidate.CreationDate.ToUniversalTime() -ge $connectStarted -and
                   $candidate.CommandLine -match ('(?<![0-9a-fA-F])'+[regex]::Escape($ownedId)+'(?![0-9a-fA-F])')) {
                    Add-ClientClaim $candidate 'exact-new-VM-GUID-and-creation-time'
                }
            }
            $children=@(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($connection.Id)" -OperationTimeoutSec 2 -ErrorAction Stop)
            foreach($candidate in $children) {
                if($candidate.CreationDate.ToUniversalTime() -ge $connectStarted) {
                    Add-ClientClaim $candidate 'direct-child-of-created-connection'
                }
            }
        }
        $windows=@([NativeUiClient]::Windows([int[]]@($known)))
        $receipt.clientWindowsBefore=$windows
        if($windows.Count -gt 1){throw 'Multiple owned client HWNDs are ambiguous; no window will be changed.'}
        if($windows.Count -eq 1) {
            $client=$windows[0];$foreground=[NativeUiClient]::Foreground()
            $receipt.clientForegroundPidBefore=[NativeUiClient]::Owner($foreground)
            [NativeUiClient]::Background($client.Hwnd,$client.ProcessId)
            $readyDeadline=[DateTimeOffset]::UtcNow.AddSeconds(5)
            do {
                $after=[NativeUiClient]::Read($client.Hwnd)
                if($after.ProcessId -ne $client.ProcessId){throw 'Owned client changed process during visibility preparation.'}
                $unchanged=[NativeUiClient]::Foreground() -eq $foreground
                if(-not $unchanged){throw 'Foreground changed during owned client preparation; stopped without activating another window.'}
                if($after.Visible -and -not $after.Iconic) {
                    $receipt.clientWindowAfter=$after;$receipt.clientForegroundUnchanged=$true
                    Write-NewJson (Join-Path $outputDirectory 'client-ready.tmp') @{nonce=$nonce;pid=$after.ProcessId;hwnd=$after.Hwnd;visible=$true;iconic=$false;foregroundUnchanged=$true}
                    [IO.File]::Move((Join-Path $outputDirectory 'client-ready.tmp'),(Join-Path $outputDirectory 'client-ready.json'))
                    return
                }
                Start-Sleep -Milliseconds 100
            }while([DateTimeOffset]::UtcNow -lt $readyDeadline)
            throw 'Owned client did not become visible and nonminimized.'
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Sandbox client ownership could not be tied to the created connection or exact VM GUID; no unrelated window was adopted.'
}

function Add-PayloadTree([string]$Directory,[string]$Prefix) {
    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($Directory)
    while($pending.Count) {
        if([DateTimeOffset]::UtcNow -ge $packagingDeadline){throw 'Runtime packaging exceeded its deadline.'}
        $current=$pending.Pop();Assert-Unlinked $current
        foreach($item in Get-ChildItem -LiteralPath $current -Force) {
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Runtime payload contains a reparse point.'}
            if($item.PSIsContainer) {if($item.Name -cne '__pycache__'){$pending.Push($item.FullName)};continue}
            if($item.Extension -in @('.pyc','.pyo')){continue}
            $relative=[IO.Path]::GetRelativePath($Directory,$item.FullName).Replace('\','/')
            Add-PayloadFile $item.FullName ($Prefix+'/'+$relative)
        }
    }
}
function Add-PayloadFile([string]$Path,[string]$Name) {
    if([DateTimeOffset]::UtcNow -ge $packagingDeadline){throw 'Runtime packaging exceeded its deadline.'}
    Assert-Unlinked $Path
    if($Name -cmatch '(^/|(^|/)\.\.?(/|$)|[:\\])' -or $Name.Length -gt 240 -or -not $payloadNames.Add($Name)){throw 'Invalid or duplicate runtime payload entry.'}
    $item=Get-Item -LiteralPath $Path -Force
    $script:payloadCount++;$script:payloadBytes+=$item.Length
    if($item.PSIsContainer -or $item.Length -gt 256MB -or $payloadCount -gt 20000 -or $payloadBytes -gt 1GB){throw 'Runtime payload exceeds the bounded copy limits.'}
    $entry=$payloadZip.CreateEntry($Name,[IO.Compression.CompressionLevel]::Fastest)
    $fileStream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if($fileStream.Length -ne $item.Length){throw 'Runtime input changed before packaging.'}
        $output=$entry.Open();try{$fileStream.CopyTo($output)}finally{$output.Dispose()}
        if($fileStream.Position -ne $item.Length -or $fileStream.Length -ne $item.Length){throw 'Runtime input changed during packaging.'}
    }finally{$fileStream.Dispose()}
}

$guestSource=@'
param([Parameter(Mandatory=$true)][string]$Nonce)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$result=[ordered]@{status='FAIL';nonce=$Nonce;phase='guard';runnerExitCode=$null}
$local='C:\NLTools'
$archiveReady=$false
try {
    $name=([Security.Principal.WindowsIdentity]::GetCurrent().Name -split '\\')[-1]
    if($name -ine 'WDAGUtilityAccount' -or $Nonce -cnotmatch '^[a-f0-9]{32}$'){throw 'Real Sandbox token and nonce are required.'}
    $inputPath='C:\NLInput\input.json'
    if((Get-Item -LiteralPath $inputPath).Length -gt 4096){throw 'Oversized input marker.'}
    $config=Get-Content -LiteralPath $inputPath -Raw|ConvertFrom-Json
    if($config.nonce -cne $Nonce -or $config.purpose -cne 'netlights-native-ui' -or $config.mode -cne 'windows-sandbox'){throw 'Sandbox input ownership differs.'}
    if((Get-FileHash -LiteralPath 'C:\NLInput\payload.zip' -Algorithm SHA256).Hash -ine $config.payload_sha256 -or
       (Get-FileHash -LiteralPath 'C:\NLInput\command.cs' -Algorithm SHA256).Hash -ine $config.command_sha256){throw 'Prepared payload/source hashes differ.'}
    if(Test-Path -LiteralPath $local){throw 'Local guest workspace already exists; no existing profile will be reused.'}
    [void][IO.Directory]::CreateDirectory($local)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead('C:\NLInput\payload.zip')
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total=[long]0
    try {
        if($zip.Entries.Count -gt 20000){throw 'Payload entry limit exceeded.'}
        foreach($entry in $zip.Entries) {
            $path=$entry.FullName
            $total+=$entry.Length
            if($path -cmatch '(^/|(^|/)\.\.?(/|$)|[:\\])' -or $path.Length -gt 240 -or -not $seen.Add($path) -or $entry.Length -gt 256MB -or $total -gt 1GB){throw 'Unsafe or excessive payload entry.'}
            $target=[IO.Path]::GetFullPath((Join-Path $local $path.Replace('/','\')))
            if(-not $target.StartsWith($local+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Payload escaped the owned workspace.'}
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            $source=$entry.Open();$destination=[IO.File]::Open($target,[IO.FileMode]::CreateNew)
            try{$source.CopyTo($destination);if($destination.Length -ne $entry.Length){throw 'Incomplete payload extraction.'}}finally{$source.Dispose();$destination.Dispose()}
        }
    }finally{$zip.Dispose()}
    $result.phase='local-runtime'
    if((Get-FileHash -LiteralPath "$local\publish\NetLights.exe" -Algorithm SHA256).Hash -ine $config.app_sha256 -or
       (Get-FileHash -LiteralPath "$local\scripts\native-ui-smoke.py" -Algorithm SHA256).Hash -ine $config.runner_sha256 -or
       (Get-FileHash -LiteralPath "$local\python\mfc140u.dll" -Algorithm SHA256).Hash -ine $config.mfc_sha256){throw 'Extracted app/source/runtime identity differs.'}
    # The Python guard checks all ancestors. Keep its marker on the local disk,
    # even if Windows implements a mapped read-only input using a reparse point.
    [IO.File]::Copy($inputPath,"$local\input.json",$false)
    if((Get-FileHash -LiteralPath $inputPath).Hash -ne (Get-FileHash -LiteralPath "$local\input.json").Hash){throw 'Local marker copy differs.'}
    Add-Type -TypeDefinition ([IO.File]::ReadAllText('C:\NLInput\command.cs'))
    $arguments=[Collections.Generic.List[string]]::new()
    foreach($value in @('-B',"$local\bootstrap.py",'--exe',"$local\publish\NetLights.exe",'--output',"$local\TestResults\ui",'--expected-sha256',$config.app_sha256,'--sandbox-input',"$local\input.json",'--sandbox-nonce',$Nonce,'--measure-seconds',[string]$config.measure_seconds)){$arguments.Add($value)}
    if($config.functional_ui){$arguments.Add('--functional-evidence')}
    if($config.coverage_instrumented){$arguments.Add('--coverage-instrumented')}
    if($config.maximum_history){$arguments.Add('--maximum-history')}
    if($config.enable_country){$arguments.Add('--enable-country-for-measurement')}
    [void][IO.Directory]::CreateDirectory('C:\NLRaw')
    $environment=[Collections.Generic.Dictionary[string,string]]::new()
    $environment.Add('PYTHONHOME',"$local\python");$environment.Add('PYTHONPATH',$null);$environment.Add('PYTHONOPTIMIZE',$null)
    $environment.Add('PYTHONDONTWRITEBYTECODE','1');$environment.Add('PYTHONNOUSERSITE','1')
    $environment.Add('LLVM_PROFILE_FILE',('C:\NLRaw\netlights-%p-%m-'+$Nonce+'.profraw'))
    $environment.Add('LLVM_PROFILE_FILE_NAME',('netlights-%p-%m-'+$Nonce+'.profraw'))
    $result.phase='native-ui'
    $clientDeadline=[DateTimeOffset]::UtcNow.AddSeconds(75)
    while(-not(Test-Path -LiteralPath 'C:\NLResult\client-ready.json')) {
        if([DateTimeOffset]::UtcNow -ge $clientDeadline){throw 'Host did not confirm a visible nonminimized owned Sandbox client.'}
        Start-Sleep -Milliseconds 200
    }
    if((Get-Item -LiteralPath 'C:\NLResult\client-ready.json').Length -gt 4096){throw 'Oversized client readiness marker.'}
    $client=Get-Content -LiteralPath 'C:\NLResult\client-ready.json' -Raw|ConvertFrom-Json
    if($client.nonce -cne $Nonce -or $client.visible -cne $true -or $client.iconic -cne $false -or $client.foregroundUnchanged -cne $true){throw 'Owned Sandbox client readiness marker differs.'}
    $result.runnerExitCode=[NativeUiCommand]::Run("$local\python\python.exe",$arguments.ToArray(),$local,$environment,"$local\runner.stdout.log","$local\runner.stderr.log",([int]$config.timeout_seconds*1000),$true)
    if($result.runnerExitCode -ne 0){throw 'Native UI runner failed; retained bounded guest logs and results contain the evidence.'}
    $result.status='PASS'
} catch {
    $result.error=[string]$_.Exception.Message
    if($result.error.Length -gt 2048){$result.error=$result.error.Substring(0,2048)}
} finally {
    # Archive local evidence before destroying this VM. No host profile is mapped.
    try {
        if(Test-Path -LiteralPath $local) {
            Add-Type -AssemblyName System.IO.Compression
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $archive=[IO.Compression.ZipFile]::Open('C:\NLResult\evidence.zip',[IO.Compression.ZipArchiveMode]::Create)
            $count=0;$bytes=[long]0
            try {
                foreach($pair in @(@("$local\TestResults\ui",'ui'),@('C:\NLRaw','raw'))) {
                    if(-not(Test-Path -LiteralPath $pair[0])){continue}
                    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($pair[0])
                    while($pending.Count) {
                        foreach($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Guest evidence contains a reparse point.'}
                            if($item.PSIsContainer){$pending.Push($item.FullName);continue}
                            $count++;$bytes+=$item.Length
                            if($count -gt 5000 -or $bytes -gt 1GB -or $item.Length -gt 64MB){throw 'Guest evidence exceeds its archive limits.'}
                            $relative=$item.FullName.Substring($pair[0].Length+1).Replace('\','/')
                            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$item.FullName,($pair[1]+'/'+$relative),[IO.Compression.CompressionLevel]::Fastest)|Out-Null
                        }
                    }
                }
                foreach($leaf in @('runner.stdout.log','runner.stderr.log')) {
                    if(Test-Path -LiteralPath "$local\$leaf"){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,"$local\$leaf",('logs/'+$leaf),[IO.Compression.CompressionLevel]::Fastest)|Out-Null}
                }
            }finally{$archive.Dispose()}
            $result.archiveSha256=(Get-FileHash -LiteralPath 'C:\NLResult\evidence.zip' -Algorithm SHA256).Hash
            $archiveReady=$true
        }
    }catch{$result.status='FAIL';$result.archiveError='Guest evidence could not be archived within its limits.'}
    $result.archiveReady=$archiveReady
    $json=ConvertTo-Json -InputObject $result -Depth 8
    [IO.File]::WriteAllText('C:\NLResult\guest-result.tmp',$json,[Text.UTF8Encoding]::new($false))
    [IO.File]::Move('C:\NLResult\guest-result.tmp','C:\NLResult\guest-result.json')
}
'@

try {
    $uv=(Get-Command uv.exe -CommandType Application -ErrorAction Stop).Source
    # A clean CI image may bootstrap the pinned PEP dependencies into this
    # task cache. The guest receives the finished runtime and stays offline.
    $uvPrefix=@('--cache-dir',(Join-Path $repository 'artifacts/uv-cache'),'run','--script',$runner)
    $runtime=(Invoke-CommandBounded 'python-runtime' $uv ($uvPrefix+@('--runtime-info')) 120 -Environment @{PYTHONOPTIMIZE=$null})|ConvertFrom-Json
    if($runtime.dependencies.pywinauto -cne '0.6.9' -or $runtime.dependencies.Pillow -cne '13.0.0' -or -not $runtime.base_executable -or -not $runtime.base_prefix -or [IO.Path]::GetFullPath($runtime.base_executable) -ine [IO.Path]::GetFullPath((Join-Path $runtime.base_prefix 'python.exe'))){throw 'Unexpected Python runtime/dependency metadata.'}
    $runtimeSource=Resolve-RuntimeRoot $runtime.base_prefix $runtime.base_executable $runtime.version
    $receipt.pythonExecutableSha256=$runtimeSource.ExecutableSha256
    $timeout=1200+$MeasureSeconds
    if($hosted) {
        $environment=@{PYTHONOPTIMIZE=$null;PYTHONDONTWRITEBYTECODE='1'}
        if(-not $rawTarget){$rawTarget=Join-Path $results 'raw'}
        [void][IO.Directory]::CreateDirectory($rawTarget)
        $environment.LLVM_PROFILE_FILE=Join-Path $rawTarget ('netlights-%p-%m-'+$nonce+'.profraw')
        $environment.LLVM_PROFILE_FILE_NAME='netlights-%p-%m-'+$nonce+'.profraw'
        $ui=Join-Path $results 'ui'
        $arguments=$uvPrefix+@('--exe',$app,'--output',$ui,'--expected-sha256',$ExpectedSha256,'--isolated-ci-input','--measure-seconds',[string]$MeasureSeconds)
        if($FunctionalUI){$arguments+='--functional-evidence'}
        if($CoverageInstrumented){$arguments+='--coverage-instrumented'}
        if($MaximumHistory){$arguments+='--maximum-history'}
        if($EnableCountryForMeasurement){$arguments+='--enable-country-for-measurement'}
        Invoke-CommandBounded 'isolated-ci-ui' $uv $arguments $timeout -Environment $environment|Out-Null
        $evidence=Get-Content -LiteralPath (Join-Path $ui 'results.json') -Raw|ConvertFrom-Json
        Assert-UiEvidence $evidence 'GitHub-hosted Windows VM' $nonce $ExpectedSha256 $runnerHash ([bool]$CoverageInstrumented)
        $receipt.uiResults=Join-Path $ui 'results.json'
    }else {
        if(@(Sandbox-Ids).Count){throw 'Another Sandbox exists; no environment will be reused, reconnected or stopped.'}
        $inputDirectory=Join-Path $results 'sandbox-input';$outputDirectory=Join-Path $results 'sandbox-output'
        [void][IO.Directory]::CreateDirectory($inputDirectory);[void][IO.Directory]::CreateDirectory($outputDirectory)
        [IO.File]::WriteAllText((Join-Path $inputDirectory 'run.ps1'),$guestSource,[Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $inputDirectory 'command.cs'),$captureSource,[Text.UTF8Encoding]::new($false))
        $payloadPath=Join-Path $inputDirectory 'payload.zip'
        $payloadZip=[IO.Compression.ZipFile]::Open($payloadPath,[IO.Compression.ZipArchiveMode]::Create)
        $payloadNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $payloadCount=0;$payloadBytes=[long]0
        $packagingDeadline=[DateTimeOffset]::UtcNow.AddSeconds(120)
        try {
            Add-PayloadFile $app 'publish/NetLights.exe'
            $agent=Join-Path ([IO.Path]::GetDirectoryName($app)) 'NetLights.UpdateAgent.exe'
            if(Test-Path -LiteralPath $agent -PathType Leaf){Add-PayloadFile $agent 'publish/NetLights.UpdateAgent.exe'}
            Add-PayloadFile $runner 'scripts/native-ui-smoke.py'
            Add-PayloadFile (Join-Path $repository 'native/core/src/locale.rs') 'native/core/src/locale.rs'
            Add-PayloadTree $runtimeSource.Root 'python'
            # win32ui.pyd imports this MFC library, absent on a clean Sandbox.
            # Its remaining imports are Windows/UCRT and the copied Python CRT.
            $mfcPath=Join-Path ([Environment]::SystemDirectory) 'mfc140u.dll'
            Assert-Amd64Pe $mfcPath
            $mfcLock=[IO.File]::Open($mfcPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try {
                $signature=Get-AuthenticodeSignature -LiteralPath $mfcPath
                if($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)'){throw 'MFC runtime must have a valid Microsoft signature.'}
                $mfcHash=(Get-FileHash -LiteralPath $mfcPath -Algorithm SHA256).Hash
                Add-PayloadFile $mfcPath 'python/mfc140u.dll'
                $receipt.mfcRuntime=@{name='mfc140u.dll';sha256=$mfcHash;machine='AMD64';publisher=$signature.SignerCertificate.Subject;source='WindowsSystemDirectory'}
            }finally{$mfcLock.Dispose()}
            $siteCount=0
            foreach($siteDirectory in @($runtime.site_packages)) {Add-PayloadTree $siteDirectory ('sites/'+$siteCount);$siteCount++}
            if($siteCount -lt 1 -or $siteCount -gt 4){throw 'Unexpected number of isolated site-package directories.'}
            $bootstrap=Join-Path $inputDirectory 'bootstrap.py'
            $pythonSites=(@(0..($siteCount-1)|ForEach-Object {'r"C:\NLTools\sites\'+$_+'"'}) -join ',')
            $bootstrapSource="import os, runpy, site, sys`ndirectories = [$pythonSites]`nfor directory in directories: site.addsitedir(directory)`nos.environ['PYTHONPATH'] = os.pathsep.join(directories)`nsys.argv = [r'C:\NLTools\scripts\native-ui-smoke.py'] + sys.argv[1:]`nrunpy.run_path(sys.argv[0], run_name='__main__')`n"
            [IO.File]::WriteAllText($bootstrap,$bootstrapSource,[Text.UTF8Encoding]::new($false));Add-PayloadFile $bootstrap 'bootstrap.py'
        }finally{$payloadZip.Dispose()}
        if((Get-Item -LiteralPath $payloadPath).Length -gt 512MB -or (Get-FileHash -LiteralPath $runner).Hash -ine $runnerHash -or (Get-FileHash -LiteralPath $app).Hash -ine $ExpectedSha256){throw 'Payload changed or exceeds its bound.'}
        $config=[ordered]@{nonce=$nonce;purpose='netlights-native-ui';mode='windows-sandbox';app_sha256=$ExpectedSha256;runner_sha256=$runnerHash;
            payload_sha256=(Get-FileHash -LiteralPath $payloadPath).Hash;command_sha256=(Get-FileHash -LiteralPath (Join-Path $inputDirectory 'command.cs')).Hash;mfc_sha256=$mfcHash;
            functional_ui=[bool]$FunctionalUI;coverage_instrumented=[bool]$CoverageInstrumented;maximum_history=[bool]$MaximumHistory;enable_country=[bool]$EnableCountryForMeasurement;measure_seconds=$MeasureSeconds;timeout_seconds=$timeout}
        Write-NewJson (Join-Path $inputDirectory 'input.json') $config
        if((Get-Item -LiteralPath (Join-Path $inputDirectory 'input.json')).Length -gt 4096){throw 'Input marker exceeds the Python guard limit.'}
        $escapedInput=[Security.SecurityElement]::Escape($inputDirectory);$escapedOutput=[Security.SecurityElement]::Escape($outputDirectory)
        $network=if($EnableCountryForMeasurement){'Enable'}else{'Disable'}
        $configuration="<Configuration><Networking>$network</Networking><ClipboardRedirection>Disable</ClipboardRedirection><MappedFolders><MappedFolder><HostFolder>$escapedInput</HostFolder><SandboxFolder>C:\NLInput</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder><MappedFolder><HostFolder>$escapedOutput</HostFolder><SandboxFolder>C:\NLResult</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder></MappedFolders><LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\NLInput\run.ps1 -Nonce $nonce</Command></LogonCommand></Configuration>"
        [IO.File]::WriteAllText((Join-Path $results 'isolated-ui.wsb'),$configuration,[Text.UTF8Encoding]::new($false))
        if(@(Sandbox-Ids).Count){throw 'A Sandbox appeared while preparing; refusing to adopt it.'}
        $ownedId=[Guid]::NewGuid().ToString('D');$started=$true
        Invoke-Sandbox @('start','--id',$ownedId,'--config',$configuration,'--raw') 120|Out-Null
        if(@(Sandbox-Ids) -notcontains $ownedId){throw 'The requested Sandbox identity was not created.'}
        $connection=Start-Process -FilePath $wsb -ArgumentList @('connect','--id',$ownedId,'--raw') -WindowStyle Hidden -PassThru
        Confirm-OwnedClient
        $guestResultPath=Join-Path $outputDirectory 'guest-result.json'
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds($timeout+180)
        while(-not(Test-Path -LiteralPath $guestResultPath)) {
            if([DateTimeOffset]::UtcNow -ge $deadline){throw 'Owned Sandbox did not finish within its bounded deadline.'}
            Start-Sleep -Seconds 2
        }
        if((Get-Item -LiteralPath $guestResultPath).Length -gt 16384){throw 'Oversized guest receipt.'}
        $guest=Get-Content -LiteralPath $guestResultPath -Raw|ConvertFrom-Json
        if($guest.nonce -cne $nonce){throw 'Guest result belongs to another run.'}
        $evidenceArchive=Join-Path $outputDirectory 'evidence.zip'
        if(-not $guest.archiveReady -or (Get-FileHash -LiteralPath $evidenceArchive).Hash -ine $guest.archiveSha256){throw 'Guest evidence archive is incomplete or has a different hash.'}
        $archive=[IO.Compression.ZipFile]::OpenRead($evidenceArchive)
        $entryNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $extractedBytes=[long]0;$rawCount=0;$rawBytes=[long]0
        $extractDeadline=[DateTimeOffset]::UtcNow.AddSeconds(120)
        if(-not $rawTarget){$rawTarget=Join-Path $results 'raw'}
        try {
            if($archive.Entries.Count -gt 5002){throw 'Guest evidence archive has too many entries.'}
            foreach($entry in $archive.Entries) {
                if([DateTimeOffset]::UtcNow -ge $extractDeadline){throw 'Guest evidence extraction exceeded its deadline.'}
                $name=$entry.FullName;$extractedBytes+=$entry.Length
                if($name -cmatch '(^/|(^|/)\.\.?(/|$)|[:\\])' -or $name.Length -gt 260 -or -not $entryNames.Add($name) -or $entry.Length -gt 64MB -or $extractedBytes -gt 1GB){throw 'Unsafe or excessive guest evidence entry.'}
                if($name.StartsWith('ui/',[StringComparison]::Ordinal)){$destination=Join-Path $results ('ui/'+$name.Substring(3))}
                elseif($name.StartsWith('logs/',[StringComparison]::Ordinal)){$destination=Join-Path $results ('guest-logs/'+$name.Substring(5))}
                elseif($name -cmatch '^raw/(netlights-[1-9][0-9]*-[A-Za-z0-9_-]+\.profraw)$') {
                    $rawCount++;$rawBytes+=$entry.Length
                    if($rawCount -gt 512 -or $rawBytes -gt 512MB -or -not $Matches[1].Contains($nonce)){throw 'Raw profile count/bytes/nonce is outside the run bounds.'}
                    $destination=Join-Path $rawTarget $Matches[1]
                }else {throw 'Unexpected guest evidence namespace.'}
                Assert-Unlinked $destination
                [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
                $source=$entry.Open();$target=[IO.File]::Open($destination,[IO.FileMode]::CreateNew)
                try{$source.CopyTo($target);if($target.Length -ne $entry.Length){throw 'Guest evidence extraction was incomplete.'}}finally{$source.Dispose();$target.Dispose()}
                [IO.File]::SetLastWriteTimeUtc($destination,$entry.LastWriteTime.UtcDateTime)
            }
        }finally{$archive.Dispose()}
        $receipt.rawProfilesCopied=$rawCount
        $receipt.guestStatus=$guest.status
        if($guest.status -cne 'PASS' -or $guest.runnerExitCode -ne 0){throw 'Owned guest native UI checks failed; inspect retained guest logs and UI results.'}
        $ui=Join-Path $results 'ui'
        $evidence=Get-Content -LiteralPath (Join-Path $ui 'results.json') -Raw|ConvertFrom-Json
        Assert-UiEvidence $evidence 'Windows Sandbox' $nonce $ExpectedSha256 $runnerHash ([bool]$CoverageInstrumented)
        $receipt.uiResults=Join-Path $ui 'results.json'
    }
    if($RawProfileDirectory) {
        foreach($actualPid in $evidence.metadata.pids) {
            if(-not @(Get-ChildItem -LiteralPath $rawTarget -File -Filter ('netlights-'+$actualPid+'-*.profraw')|Where-Object {$_.Length -gt 0 -and $_.Name.Contains($nonce)}).Count){throw 'An actual isolated UI process did not produce its own fresh raw profile.'}
        }
    }
    $receipt.status='PASS'
} catch {
    $receipt.error=[string]$_.Exception.Message
    throw
} finally {
    try {
        if($started -and @(Sandbox-Ids) -contains $ownedId) {
            try{Invoke-Sandbox @('stop','--id',$ownedId,'--raw') 60|Out-Null}catch{if(@(Sandbox-Ids) -contains $ownedId){throw}}
            $deadline=[DateTimeOffset]::UtcNow.AddSeconds(15)
            while(@(Sandbox-Ids) -contains $ownedId){if([DateTimeOffset]::UtcNow -ge $deadline){throw 'Owned Sandbox did not stop.'};Start-Sleep -Milliseconds 250}
        }
        $receipt.ownedSandboxStopped=$started
    }catch{$receipt.status='FAIL';$receipt.cleanupError='Owned Sandbox could not be confirmed stopped.';throw}
    finally {
        $connectionTracked=$connection -and $clientProcesses.ContainsKey($connection.Id)
        foreach($clientProcess in $clientProcesses.Values){$clientProcess.Dispose()}
        if($connection -and -not $connectionTracked){$connection.Dispose()}
        $receipt.finishedUtc=[DateTimeOffset]::UtcNow.ToString('o')
        Write-NewJson (Join-Path $results 'isolation-result.json') $receipt
    }
}
Write-Output ('RUN - PASS: isolated native UI; evidence '+$receipt.uiResults+'. Host cursor and live profile were not used.')
