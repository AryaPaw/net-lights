using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace NetLights.App;

internal static class AutoStartStore
{
    public const string ValueName = "NetLights";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(string executablePath)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return OwnsCommand(key?.GetValue(ValueName) as string, executablePath);
    }

    public static bool HasForeignEntry(string executablePath)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string command && !OwnsCommand(command, executablePath);
    }

    public static void Set(bool enabled, string executablePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, Quote(executablePath));
        }
        else if (OwnsCommand(key.GetValue(ValueName) as string, executablePath))
        {
            key.DeleteValue(ValueName);
        }

        TryRemoveOwnedStartupShortcut(executablePath);
    }

    private static void TryRemoveOwnedStartupShortcut(string executablePath)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Net Lights.lnk");
        if (!File.Exists(path)) return;
        object? shell = null;
        object? shortcut = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return;
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, [path]);
            string? target = shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty,
                null, shortcut, null) as string;
            if (target is not null && string.Equals(Path.GetFullPath(target), Path.GetFullPath(executablePath),
                    StringComparison.OrdinalIgnoreCase))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    public static string Quote(string path) => "\"" + path.Replace("\"", "") + "\"";

    internal static bool OwnsCommand(string? command, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        try
        {
            string full = Path.GetFullPath(executablePath);
            return string.Equals(command, Quote(full), StringComparison.OrdinalIgnoreCase)
                || string.Equals(command, full, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
