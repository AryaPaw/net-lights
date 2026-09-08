namespace NetLights.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\NetLights.SingleInstance", out bool created);
        if (!created)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new NetLightsContext());
        GC.KeepAlive(mutex);
    }
}
