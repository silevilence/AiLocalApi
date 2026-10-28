using Microsoft.Win32;
namespace AiLocalApi.Desktop;
internal static class LoginStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Name => "AiLocalApi-" + Distribution.StartupId;
    public static bool Enabled { get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(Name) is string; } }
    public static void RepairIfEnabled() { if (Enabled) Set(true); }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(Name, $"\"{Distribution.StartupExecutable}\" --tray");
        else key.DeleteValue(Name, false);
    }
}
