namespace NetLights.Updates;

public static class UpdateHealthSignal
{
    public const string ArgumentName = "--update-health-event";
    private const string Prefix = "Local\\NetLights.UpdateHealth.";

    public static string CreateEventName() => Prefix + Guid.NewGuid().ToString("N");

    public static bool TryReadName(IReadOnlyList<string> args, out string? eventName)
    {
        eventName = null;
        for (int index = 0; index + 1 < args.Count; index++)
        {
            if (!string.Equals(args[index], ArgumentName, StringComparison.Ordinal)) continue;
            string candidate = args[index + 1];
            if (!IsValidName(candidate)) return false;
            eventName = candidate;
            return true;
        }

        return false;
    }

    public static bool Signal(string? eventName)
    {
        if (!OperatingSystem.IsWindows() || !IsValidName(eventName)) return false;
        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(eventName!);
            return handle.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsValidName(string? value)
        => value is not null
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(value[Prefix.Length..], "N", out _);
}
