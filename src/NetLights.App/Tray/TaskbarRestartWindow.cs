using System.Runtime.InteropServices;

namespace NetLights.App;

// Explorer can broadcast TaskbarCreated before its notification area accepts icons.
// All attempts run on the owning UI thread and stop after a bounded recovery window.
internal sealed class TaskbarRestartWindow : NativeWindow, IDisposable
{
    private readonly Action _restore;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly uint _taskbarCreated;
    private readonly int _attemptCount;
    private int _remaining;
    private bool _disposed;

    internal TaskbarRestartWindow(Action restore, int intervalMilliseconds = 1000, int attemptCount = 12)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptCount, 1);
        _restore = restore;
        _attemptCount = attemptCount;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreated == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _timer = new System.Windows.Forms.Timer { Interval = intervalMilliseconds };
        _timer.Tick += OnTick;
        CreateHandle(new CreateParams());
    }

    internal void RequestRecovery()
    {
        if (_disposed) return;
        _remaining = _attemptCount;
        _timer.Stop();
        RuntimeJournal.Write("tray-recovery-requested");
        // Defer shell calls until after the broadcast returns; don't run them in WndProc.
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try { _restore(); }
        catch (Exception ex) { RuntimeJournal.Write("tray-recovery-attempt-failed", ex); }
        if (_disposed) return;
        if (--_remaining <= 0)
        {
            _timer.Stop();
            RuntimeJournal.Write("tray-recovery-window-ended");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == _taskbarCreated) RequestRecovery();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        DestroyHandle();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);
}
