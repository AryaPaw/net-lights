namespace NetLights.App;

internal static class Program
{
    private const string MutexName = @"Local\NetLights.SingleInstance";

    [STAThread]
    private static void Main()
    {
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
            Application.Run(new NetLightsContext());
        }
    }
}
