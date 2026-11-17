using Microsoft.Win32;

namespace Lilt.App.Settings;

internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("Lilt") is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (!enabled) { key.DeleteValue("Lilt", throwOnMissingValue: false); return; }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Could not find Lilt.exe.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the published app to enable startup.");
        key.SetValue("Lilt", $"\"{executable}\"");
    }
}
