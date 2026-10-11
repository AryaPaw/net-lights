using System.Security.Cryptography;
using System.Text;
using NetLights.Updates;

namespace NetLights.App;

internal static class Program
{
    private const string MutexName = @"Local\NetLights.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        RuntimeJournal.Write("process-start");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            RuntimeJournal.Write("unhandled-exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            RuntimeJournal.Write("unobserved-task", e.Exception);
        string exitEventName = "Local\\NetLights.Exit." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Environment.ProcessPath ?? Application.ExecutablePath))))[..24];
        if (args.Length == 1 && args[0] == "--exit-local")
        {
            RuntimeJournal.Write("exit-local-command");
            if (EventWaitHandle.TryOpenExisting(exitEventName, out EventWaitHandle? existing))
            {
                using (existing) existing.Set();
            }
            return;
        }

        Mutex? mutex = null;
        bool created = false;
        try
        {
            mutex = new Mutex(true, MutexName, out created);
        }
        catch (AbandonedMutexException ex)
        {
            mutex = ex.Mutex;
            created = true;
        }

        if (mutex is null)
        {
            return;
        }

        using (mutex)
        {
            if (!created)
            {
                RuntimeJournal.Write("second-instance");
                return;
            }

            ApplicationConfiguration.Initialize();
            using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, exitEventName);
            UpdateHealthSignal.TryReadName(args, out string? updateHealthEvent);
            var context = new NetLightsContext(updateHealthEvent);
            if (args.Length == 1 && args[0] == "--show-local")
                context.ShowLocalWindowOnStart();
            _ = Task.Run(() =>
            {
                exitEvent.WaitOne();
                context.RequestExit();
            });
            RuntimeJournal.Write("message-loop-starting");
            try
            {
                Application.Run(context);
                RuntimeJournal.Write("message-loop-ended");
            }
            catch (Exception ex)
            {
                RuntimeJournal.Write("message-loop-failed", ex);
                throw;
            }
        }
    }
}
