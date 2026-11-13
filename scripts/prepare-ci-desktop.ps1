#requires -Version 7.5
param(
    [string]$ResultsDirectory = 'TestResults/hosted-desktop',
    [switch]$FunctionalUI,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'ci-environment.ps1')

function Select-CiDesktopMode($Modes, [int]$ReservedWidth, [int]$ReservedHeight, [int]$WorkHeight = 1300, [switch]$PreferHeight) {
    # Mode dimensions are physical pixels. Preserve space currently reserved by
    # the shell; the actual resulting work area is checked again after applying.
    $minimumWidth = 1450 + [Math]::Max(0, $ReservedWidth)
    $minimumHeight = $WorkHeight + [Math]::Max(0, $ReservedHeight)
    $fitting = @($Modes | Where-Object {
        $_.Width -ge $minimumWidth -and $_.Height -ge $minimumHeight -and
        $_.Width -le 8192 -and $_.Height -le 8192 -and
        ([long]$_.Width * $_.Height) -le 32000000 -and
        $_.BitsPerPixel -eq 32 -and ($_.Flags -band 2) -eq 0
    })
    if ($PreferHeight) {
        return @($fitting | Sort-Object @{Expression='Height';Descending=$true}, @{Expression = {[long]$_.Width * $_.Height}}, Width | Select-Object -First 1)
    }
    return @($fitting | Sort-Object @{Expression = {[long]$_.Width * $_.Height}}, Width, Height | Select-Object -First 1)
}

if ($SelfTest) {
    function Mode([int]$Width, [int]$Height, [int]$Bits = 32, [int]$Flags = 0) {
        return [pscustomobject]@{Width=$Width;Height=$Height;BitsPerPixel=$Bits;Flags=$Flags}
    }
    $modes = @((Mode 1024 768), (Mode 1920 1080), (Mode 2560 1440), (Mode 3840 2160))
    $selected = @(Select-CiDesktopMode $modes 0 40)
    if ($selected.Count -ne 1 -or $selected[0].Width -ne 2560 -or $selected[0].Height -ne 1440) { throw 'The smallest supported fitting mode was not selected.' }
    if (@(Select-CiDesktopMode @((Mode 1920 1080), (Mode 1450 1300)) 0 40).Count) { throw 'A mode without sufficient actual work area was accepted.' }
    if (@(Select-CiDesktopMode @((Mode 2560 1440 16), (Mode 2560 1440 32 2)) 0 40).Count) { throw 'Low-colour or interlaced mode was accepted.' }
    if (@(Select-CiDesktopMode @((Mode 16384 16384), (Mode 8192 8192)) 0 0).Count) { throw 'An excessive capture surface was accepted.' }
    if (@(Select-CiDesktopMode @() 0 0).Count) { throw 'An absent display mode was invented.' }
    $selected = @(Select-CiDesktopMode @((Mode 1450 1300)) 0 0)
    if ($selected.Count -ne 1) { throw 'The exact physical minimum was rejected without a shell reservation.' }
    $selected = @(Select-CiDesktopMode @((Mode 1920 1080),(Mode 1600 1200)) 0 40 1000 -PreferHeight)
    if ($selected.Count -ne 1 -or $selected[0].Width -ne 1600 -or $selected[0].Height -ne 1200) { throw 'Compact functional selection did not preserve the tallest supported viewport.' }
    if (@(Select-CiDesktopMode @((Mode 1920 1080),(Mode 1600 1200)) 0 40).Count) { throw 'Compact functional modes weakened the default visual requirement.' }
    if (@(Select-CiDesktopMode @((Mode 1920 1000)) 0 40 1000 -PreferHeight).Count) { throw 'Compact functional selection ignored the shell reservation.' }
    Write-Output 'RUN - PASS: supported-mode selection and five negative/boundary controls. No display or input API was called.'
    return
}

# This check must precede every native desktop call and child process. A local
# interactive desktop or self-hosted runner must never be reconfigured here.
Assert-HostedRunner
$results = [IO.Path]::GetFullPath($ResultsDirectory, $repository)
$allowed = @(@('TestResults','artifacts') | Where-Object {
    $prefix = [IO.Path]::GetFullPath((Join-Path $repository $_)) + [IO.Path]::DirectorySeparatorChar
    $results.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
})
if (-not $allowed.Count) { throw 'Desktop evidence must use a fresh subdirectory of TestResults or artifacts.' }
for ($part=$results; $part; $part=[IO.Path]::GetDirectoryName($part)) {
    if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Desktop evidence cannot pass through a reparse point.' }
}
if (Test-Path -LiteralPath $results) { throw 'Desktop evidence directory must be fresh.' }
[void][IO.Directory]::CreateDirectory($results)
$receipt = [ordered]@{status='FAIL';startedUtc=[DateTimeOffset]::UtcNow.ToString('o');minimumWorkArea=@{width=1450;height=1300};displayChanged=$false;pointerInputPerformed=$false}

try {
    # DEVMODEW layout and EnumDisplaySettingsW physical-pixel semantics:
    # https://learn.microsoft.com/windows/win32/api/wingdi/ns-wingdi-devmodew
    # https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-enumdisplaysettingsw
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class NetLightsCiDisplay {
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct MonitorInfo {
        public uint Size; public Rect Monitor,Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct DevMode {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string DeviceName;
        public ushort SpecVersion,DriverVersion,Size,DriverExtra; public uint Fields;
        public int PositionX,PositionY; public uint Orientation,FixedOutput;
        public short Color,Duplex,YResolution,TTOption,Collate;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string FormName;
        public ushort LogPixels; public uint BitsPerPixel,Width,Height,Flags,Frequency;
        public uint IcmMethod,IcmIntent,MediaType,DitherType,Reserved1,Reserved2,PanningWidth,PanningHeight;
    }
    public sealed class Mode { public uint Width,Height,BitsPerPixel,Flags,Frequency; }
    public sealed class Rectangle { public int X,Y,Width,Height; }
    public sealed class State { public string Device; public Rectangle Bounds,WorkArea; public Mode Current; }
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] private static extern bool GetMonitorInfoW(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern bool EnumDisplaySettingsW(string device,uint index,ref DevMode mode);
    private static DevMode Empty() { return new DevMode{Size=(ushort)Marshal.SizeOf(typeof(DevMode))}; }
    private static Mode Copy(DevMode m) { return new Mode{Width=m.Width,Height=m.Height,BitsPerPixel=m.BitsPerPixel,Flags=m.Flags,Frequency=m.Frequency}; }
    private static Rectangle Copy(Rect r) { return new Rectangle{X=r.Left,Y=r.Top,Width=r.Right-r.Left,Height=r.Bottom-r.Top}; }
    public static State Read() {
        var previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        if(previous==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"DPI context could not be established.");
        try {
            var monitor=MonitorFromPoint(new Point(),1);
            var info=new MonitorInfo{Size=(uint)Marshal.SizeOf(typeof(MonitorInfo))};
            if(monitor==IntPtr.Zero || !GetMonitorInfoW(monitor,ref info)) throw new Win32Exception(Marshal.GetLastWin32Error(),"Primary monitor could not be read.");
            var mode=Empty();
            if(!EnumDisplaySettingsW(info.Device,UInt32.MaxValue,ref mode)) throw new InvalidOperationException("The current primary display mode could not be read.");
            return new State{Device=info.Device,Bounds=Copy(info.Monitor),WorkArea=Copy(info.Work),Current=Copy(mode)};
        } finally { if(SetThreadDpiAwarenessContext(previous)==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(),"DPI context restoration failed."); }
    }
    public static Mode[] Supported(string device) {
        var modes=new List<Mode>();
        for(uint index=0;index<4096;index++) {
            var mode=Empty(); if(!EnumDisplaySettingsW(device,index,ref mode)) return modes.ToArray();
            modes.Add(Copy(mode));
        }
        throw new InvalidOperationException("The display mode enumeration exceeded its bounded limit.");
    }
}
'@
    $before = [NetLightsCiDisplay]::Read()
    $modes = @([NetLightsCiDisplay]::Supported($before.Device))
    $receipt.before = $before
    $receipt.supportedModes = $modes
    $requiredHeight=1300
    $selected = @(Select-CiDesktopMode $modes ($before.Bounds.Width-$before.WorkArea.Width) ($before.Bounds.Height-$before.WorkArea.Height))
    if ($FunctionalUI -and -not $selected.Count -and ($before.WorkArea.Width -lt 1450 -or $before.WorkArea.Height -lt 1300)) {
        $requiredHeight=1000
        $selected = @(Select-CiDesktopMode $modes ($before.Bounds.Width-$before.WorkArea.Width) ($before.Bounds.Height-$before.WorkArea.Height) 1000 -PreferHeight)
    }
    $receipt.evidenceLevel=$(if($FunctionalUI){'functional'}else{'visual-desktop-prerequisite'})
    $receipt.compactFunctional=$FunctionalUI -and $requiredHeight -eq 1000
    $receipt.minimumWorkArea.height=$requiredHeight
    $alreadyFits=$before.WorkArea.Width -ge 1450 -and $before.WorkArea.Height -ge $requiredHeight
    $preferSelected=$receipt.compactFunctional -and $selected.Count -eq 1 -and $selected[0].Height -gt $before.Bounds.Height
    if (-not $alreadyFits -or $preferSelected) {
        if ($selected.Count -ne 1) { throw 'The primary virtual display has no supported mode that fits the required UI matrix.' }
        $receipt.selected = $selected[0]
        $receipt.displayChangeAttempted=$true
        Assert-HostedRunner
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell/v1.0/powershell.exe'))
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($argument in @('-NoProfile','-NonInteractive','-Command',("Set-DisplayResolution -Width {0} -Height {1} -Force -ErrorAction Stop" -f $selected[0].Width,$selected[0].Height))) { [void]$start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new(); $process.StartInfo=$start
        try {
            if (-not $process.Start()) { throw 'The owned display configuration process could not start.' }
            $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(30000)) { $process.Kill($true);[void]$process.WaitForExit(5000);throw 'The owned display configuration process timed out.' }
            if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr),5000)) { throw 'Display configuration output did not close.' }
            [IO.File]::WriteAllText((Join-Path $results 'set-display.stdout.log'),$stdout.Result.Substring(0,[Math]::Min($stdout.Result.Length,65536)))
            [IO.File]::WriteAllText((Join-Path $results 'set-display.stderr.log'),$stderr.Result.Substring(0,[Math]::Min($stderr.Result.Length,65536)))
            $receipt.configurationExitCode=$process.ExitCode
            if ($process.ExitCode -ne 0) { throw 'The supported display mode could not be applied; see bounded logs.' }
            $receipt.displayChanged=$true
        } finally { $process.Dispose() }
    }
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $after=[NetLightsCiDisplay]::Read()
        if ($after.Device -ceq $before.Device -and $after.WorkArea.Width -ge 1450 -and $after.WorkArea.Height -ge $requiredHeight) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    $receipt.after=$after
    if ($after.Device -cne $before.Device -or $after.WorkArea.Width -lt 1450 -or $after.WorkArea.Height -lt $requiredHeight) { throw 'The actual primary work area still cannot fit the required UI matrix.' }
    $receipt.status='PASS'
    Write-Output "RUN - PASS: hosted primary work area $($after.WorkArea.Width)x$($after.WorkArea.Height); no pointer input."
} catch {
    $receipt.error=$_.Exception.Message
    throw
} finally {
    $receipt.completedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    $json=$receipt|ConvertTo-Json -Depth 8
    $stream=[IO.File]::Open((Join-Path $results 'result.json'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {$bytes=[Text.UTF8Encoding]::new($false).GetBytes($json);$stream.Write($bytes)} finally {$stream.Dispose()}
}
