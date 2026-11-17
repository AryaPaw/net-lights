param([Parameter(Mandatory)][string]$ResultsDirectory,[string]$ToolCacheDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This pinned workflow checker currently requires Windows AMD64.' }
$root=Split-Path -Parent $PSScriptRoot
$results=[IO.Path]::GetFullPath($ResultsDirectory,$root)
if (-not @('artifacts','TestResults').Where({$results.StartsWith((Join-Path $root $_)+'\',[StringComparison]::OrdinalIgnoreCase)}).Count) { throw 'Workflow evidence must stay inside project artifacts.' }
$cache=if($ToolCacheDirectory){[IO.Path]::GetFullPath($ToolCacheDirectory,$root)}else{Join-Path $root 'artifacts/tools/actionlint-1.7.12-download'}
if (-not $cache.StartsWith((Join-Path $root 'artifacts/tools')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Workflow tool cache must stay inside project artifacts/tools.' }
foreach($path in @($results,$cache)) {
    for($part=$path;$part;$part=[IO.Path]::GetDirectoryName($part)) {
        if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Workflow verification paths must not contain links.' }
    }
}
[void][IO.Directory]::CreateDirectory($cache)
[void][IO.Directory]::CreateDirectory($results)
$archive=Join-Path $cache 'actionlint_1.7.12_windows_amd64.zip'
$exe=Join-Path $cache 'actionlint.exe'
$archiveHash='6E7241B51E6817EA6A047693D8E6FED13B31819C9A0DD6C5A726E1592D22F6E9'
$exeHash='54CA21BE3DE4C7CFA26914AA8B61BD76BF573EF3CAAC5F80D110558CDF241718'
if (-not (Test-Path -LiteralPath $archive)) {
    $part=Join-Path $cache ('download-'+[Guid]::NewGuid().ToString('N')+'.zip')
    $client=[Net.Http.HttpClient]::new()
    $cancel=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))
    $response=$inputStream=$outputStream=$null
    try { try {
        $response=$client.GetAsync('https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_windows_amd64.zip',[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cancel.Token).GetAwaiter().GetResult()
        $null=$response.EnsureSuccessStatusCode()
        if ($response.Content.Headers.ContentLength -gt 10MB) { throw 'Workflow checker download exceeds its bound.' }
        $inputStream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $outputStream=[IO.File]::Open($part,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        $buffer=[byte[]]::new(65536)
        $total=0
        while (($count=$inputStream.ReadAsync($buffer,0,$buffer.Length,$cancel.Token).GetAwaiter().GetResult()) -gt 0) {
            $total+=$count
            if ($total -gt 10MB) { throw 'Workflow checker download exceeds its bound.' }
            $outputStream.Write($buffer,0,$count)
        }
    } finally {
        if ($outputStream) { $outputStream.Dispose() }; if ($inputStream) { $inputStream.Dispose() }
        if ($response) { $response.Dispose() }; $cancel.Dispose(); $client.Dispose()
    }
    if ((Get-Item -LiteralPath $part).Length -gt 10MB -or (Get-FileHash -LiteralPath $part).Hash -cne $archiveHash) { throw 'Pinned actionlint archive hash differs.' }
    [IO.File]::Move($part,$archive,$false)
    } catch {
        if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }
        throw
    }
}
if ((Get-Item -LiteralPath $archive).Length -gt 10MB -or (Get-FileHash -LiteralPath $archive).Hash -cne $archiveHash) { throw 'Cached actionlint archive hash differs.' }
if (-not (Test-Path -LiteralPath $exe)) {
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry=$zip.GetEntry('actionlint.exe')
        if (-not $entry -or $entry.Length -gt 20MB) { throw 'Pinned actionlint executable is invalid.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$exe,$false)
    } finally { $zip.Dispose() }
}
if ((Get-FileHash -LiteralPath $exe).Hash -cne $exeHash) { throw 'Cached actionlint executable hash differs.' }

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public static class BoundedWorkflowRead {
    public static async Task<string> Read(Stream input, CancellationToken token) {
        byte[] buffer=new byte[4096];
        using(var output=new MemoryStream(65536)) {
            int count;
            while((count=await input.ReadAsync(buffer,0,buffer.Length,token))!=0) {
                if(output.Length+count>65536) throw new InvalidDataException("Workflow checker output exceeded its bound.");
                output.Write(buffer,0,count);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }
}
'@
function Invoke-BoundedWorkflowCommand([string]$File,[string[]]$Arguments,[int]$TimeoutMs=30000) {
    $start=[Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.WorkingDirectory=$root
    foreach($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new()
    $process.StartInfo=$start
    $started=$false
    $cancel=[Threading.CancellationTokenSource]::new()
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $exitAt=$null
    try {
        if (-not $process.Start()) { throw 'Workflow checker did not start.' }
        $started=$true
        $stdout=[BoundedWorkflowRead]::Read($process.StandardOutput.BaseStream,$cancel.Token)
        $stderr=[BoundedWorkflowRead]::Read($process.StandardError.BaseStream,$cancel.Token)
        while ($true) {
            if ($stdout.IsFaulted -or $stdout.IsCanceled) { $null=$stdout.GetAwaiter().GetResult() }
            if ($stderr.IsFaulted -or $stderr.IsCanceled) { $null=$stderr.GetAwaiter().GetResult() }
            if ($process.HasExited) {
                if ($stdout.IsCompleted -and $stderr.IsCompleted) { break }
                if ($null -eq $exitAt) { $exitAt=$timer.ElapsedMilliseconds }
                if ($timer.ElapsedMilliseconds-$exitAt -ge 2000) { throw 'Workflow checker output did not close after exit.' }
            }
            if ($timer.ElapsedMilliseconds -ge $TimeoutMs) { throw 'Workflow checker timed out.' }
            Start-Sleep -Milliseconds 10
        }
        $text=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
        return @{Code=$process.ExitCode;Text=$text}
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); $null=$process.WaitForExit(5000) }
        $cancel.Cancel()
        if ($started) { $process.StandardOutput.Dispose(); $process.StandardError.Dispose() }
        $process.Dispose()
        $cancel.Dispose()
    }
}
function Invoke-WorkflowLint([string[]]$Paths) {
    # This gate checks YAML and GitHub expressions. Don't invoke unpinned PATH tools.
    return Invoke-BoundedWorkflowCommand $exe (@('-no-color','-shellcheck=','-pyflakes=')+$Paths)
}

$pwsh=[Environment]::ProcessPath
if ([IO.Path]::GetFileName($pwsh) -ine 'pwsh.exe') { throw 'Workflow controls require their exact current PowerShell host.' }
$overflowRejected=$false
try { $null=Invoke-BoundedWorkflowCommand $pwsh @('-NoProfile','-NonInteractive','-Command','[Console]::Out.Write([string]::new([char]120,150000))') 5000 }
catch { if ($_.Exception.ToString().Contains('output exceeded its bound')) { $overflowRejected=$true } else { throw } }
if (-not $overflowRejected) { throw 'Workflow reader accepted output beyond its bound.' }
$timeoutRejected=$false
try { $null=Invoke-BoundedWorkflowCommand $pwsh @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 10') 300 }
catch { if ($_.Exception.Message -ceq 'Workflow checker timed out.') { $timeoutRejected=$true } else { throw } }
if (-not $timeoutRejected) { throw 'Workflow runner accepted a hung process.' }

$positive=@'
name: control
on: workflow_dispatch
jobs:
  test:
    runs-on: windows-2022
    steps:
      - run: Write-Output ok
'@
$valid=Join-Path $results 'valid.yml'
[IO.File]::WriteAllText($valid,$positive,[Text.UTF8Encoding]::new($false))
if ((Invoke-WorkflowLint @($valid)).Code -ne 0) { throw 'Workflow checker rejected its positive control.' }
foreach($kind in @('expression','syntax')) {
    $invalid=Join-Path $results ($kind+'.yml')
    $text=if($kind -ceq 'expression'){$positive.Replace('Write-Output ok','Write-Output ${{ needs.unknown.outputs.value }}')}else{$positive.Replace('jobs:','jobs: [')}
    [IO.File]::WriteAllText($invalid,$text,[Text.UTF8Encoding]::new($false))
    $result=Invoke-WorkflowLint @($invalid)
    $result.Text | Set-Content -LiteralPath (Join-Path $results ($kind+'.log')) -Encoding utf8
    if ($result.Code -ne 1) { throw 'Workflow checker failed to detect an intentional invalid workflow.' }
}
$files=@(Get-ChildItem -LiteralPath (Join-Path $root '.github/workflows') -File | Where-Object Extension -In @('.yml','.yaml'))
if (-not $files.Count -or $files.Count -gt 64 -or @($files|Where-Object Length -GT 2MB).Count) { throw 'Workflow input inventory exceeds its bounds.' }
$result=Invoke-WorkflowLint @($files.FullName)
$result.Text | Set-Content -LiteralPath (Join-Path $results 'workflows.log') -Encoding utf8
if ($result.Code -ne 0) { Write-Output $result.Text; throw 'Workflow syntax or expression validation FAIL.' }
Write-Host 'RUN - PASS: pinned actionlint, valid/invalid controls and all repository workflows.'
