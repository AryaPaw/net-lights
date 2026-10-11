param(
    [ValidateRange(2, 3600)][int]$Seconds = 30,
    [ValidateRange(1, 30)][int]$IntervalSeconds = 1,
    [string]$OutputPath = 'artifacts/evidence/netlights-resources.csv'
)

$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NetLightsResourcesNative {
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
}
'@

$expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts\publish\NetLights.exe'))
$process = Get-Process NetLights -ErrorAction Stop | Where-Object {
    $_.Path -and [string]::Equals($_.Path, $expected, [StringComparison]::OrdinalIgnoreCase)
} | Select-Object -First 1
if (-not $process) { throw "Local Net Lights process not found at $expected" }
$nativeHandle = [NetLightsResourcesNative]::OpenProcess(0x1000, $false, [uint32]$process.Id)
if ($nativeHandle -eq [IntPtr]::Zero) { throw "Unable to open process $($process.Id) for resource counters" }

$writer = $null
try {
    $resolved = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $resolved) | Out-Null
    if (Test-Path -LiteralPath $resolved) { throw "Refusing to overwrite existing resource log: $resolved" }
    $writer = [IO.StreamWriter]::new($resolved, $false, [Text.UTF8Encoding]::new($true))
    $writer.WriteLine('utc,pid,privateBytes,workingSetBytes,handles,gdi,userObjects,cpuPercent')
    $writer.Flush()

    $sampleCount = 0
    $previousCpu = $null
    $previousTime = $null
    for ($elapsed = 0; $elapsed -le $Seconds; $elapsed += $IntervalSeconds) {
        $process.Refresh()
        if ($process.HasExited) { throw "Net Lights exited during sampling: PID $($process.Id)" }
        $now = [DateTimeOffset]::UtcNow
        $cpu = $process.TotalProcessorTime.TotalSeconds
        $cpuPercent = if ($null -eq $previousCpu) { $null } else {
            [math]::Round(100 * ($cpu - $previousCpu) / (($now - $previousTime).TotalSeconds * [Environment]::ProcessorCount), 2)
        }
        $invariant = [Globalization.CultureInfo]::InvariantCulture
        $cpuText = if ($null -eq $cpuPercent) { '' } else { $cpuPercent.ToString('F2', $invariant) }
        $fields = @(
            $now.ToString('o'),
            $process.Id.ToString($invariant),
            $process.PrivateMemorySize64.ToString($invariant),
            $process.WorkingSet64.ToString($invariant),
            $process.HandleCount.ToString($invariant),
            [NetLightsResourcesNative]::GetGuiResources($nativeHandle, 0).ToString($invariant),
            [NetLightsResourcesNative]::GetGuiResources($nativeHandle, 1).ToString($invariant),
            $cpuText
        )
        $writer.WriteLine([string]::Join(',', $fields))
        $writer.Flush()
        $sampleCount++
        $previousCpu = $cpu
        $previousTime = $now
        if ($elapsed + $IntervalSeconds -le $Seconds) { Start-Sleep -Seconds $IntervalSeconds }
    }

    Write-Output "$resolved ($sampleCount samples)"
}
finally {
    if ($writer) { $writer.Dispose() }
    [void][NetLightsResourcesNative]::CloseHandle($nativeHandle)
}
