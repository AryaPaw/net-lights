using System.Diagnostics;
using System.Runtime.InteropServices;
using NetLights.App;
using Xunit;

namespace NetLights.IntegrationTests;

[Collection("SettingsFiles")]
public sealed class TaskbarRecoveryTests
{
    [Fact]
    public void ShellBroadcast_RetriesAfterFailure_AndStopsAtLimit()
    {
        RunSta(() =>
        {
            int calls = 0;
            using var window = new TaskbarRestartWindow(() =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("Shell not ready");
            }, 20, 3);
            SendMessage(window.Handle, RegisterWindowMessage("TaskbarCreated"), 0, 0);
            Assert.Equal(0, calls);
            PumpUntil(() => calls == 3);
            PumpFor(150);
            Assert.Equal(3, calls);
        });
    }

    [Fact]
    public void RepeatedBroadcast_ReusesRecovery_AndDisposeCancelsPendingAttempts()
    {
        RunSta(() =>
        {
            int calls = 0;
            using var window = new TaskbarRestartWindow(() => calls++, 20, 3);
            uint message = RegisterWindowMessage("TaskbarCreated");
            for (int i = 0; i < 20; i++) SendMessage(window.Handle, message, 0, 0);
            PumpUntil(() => calls == 3);
            PumpFor(150);
            Assert.Equal(3, calls);
            window.RequestRecovery();
            window.Dispose();
            window.RequestRecovery();
            PumpFor(150);
            Assert.Equal(3, calls);
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)), "UI test timed out");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var clock = Stopwatch.StartNew();
        while (!complete() && clock.ElapsedMilliseconds < 3000)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Assert.True(complete(), "Recovery timer did not complete");
    }

    private static void PumpFor(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessage(nint hwnd, uint message, nint wparam, nint lparam);
}
