using System.Security.Cryptography;
using System.Text;

namespace NetLights.App;

internal static class Program
{
    private const string MutexName = @"Local\NetLights.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        string exitEventName = "Local\\NetLights.Exit." + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Environment.ProcessPath ?? Application.ExecutablePath))))[..24];
        if (args.Length == 1 && args[0] == "--exit-local")
        {
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
                return;
            }

            ApplicationConfiguration.Initialize();
            using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, exitEventName);
            var context = new NetLightsContext();
            _ = Task.Run(() =>
            {
                exitEvent.WaitOne();
                context.RequestExit();
            });
            Application.Run(context);
        }
    }
}
