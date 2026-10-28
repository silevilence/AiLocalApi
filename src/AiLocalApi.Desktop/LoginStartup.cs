using Microsoft.Win32;
namespace AiLocalApi.Desktop;
internal static class LoginStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled { get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("AiLocalApi") is string; } }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("AiLocalApi", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("AiLocalApi", false);
    }
}
