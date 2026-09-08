namespace NetLights.App;

internal static class Program
{
    private const string MutexName = @"Local\NetLights.SingleInstance";

    [STAThread]
    private static void Main()
    {
        Mutex? mutex = null;
        bool created;
        try
        {
            mutex = new Mutex(true, MutexName, out created);
        }
        catch (AbandonedMutexException)
        {
            mutex = new Mutex(true, MutexName, out created);
            created = true;
        }

        using (mutex)
        {
            if (!created)
            {
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new NetLightsContext());
        }
    }
}
