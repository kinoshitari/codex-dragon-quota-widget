using Microsoft.Win32;
using System.IO;

namespace DragonQuotaWidget;

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexDragonQuotaWidget";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                var executable = ResolveStableExecutable();
                if (!string.IsNullOrWhiteSpace(executable)) key.SetValue(ValueName, $"\"{executable}\" --watch-codex");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch
        {
            // Startup registration is best-effort; manual launch remains available.
        }
    }

    private static string? ResolveStableExecutable()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var personalPluginExecutable = Path.Combine(
            profile,
            "plugins",
            "codex-dragon-quota-widget",
            "bin",
            "win-x64",
            "CodexDragonQuotaWidget.exe");
        return File.Exists(personalPluginExecutable) ? personalPluginExecutable : Environment.ProcessPath;
    }
}
