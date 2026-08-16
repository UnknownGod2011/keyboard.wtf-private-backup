namespace KeyboardWtf.Services;

using Microsoft.Win32;

public static class WindowsStartupService
{
#if KEYBOARD_WTF_PAID_PRODUCTION_CANDIDATE
    public const string AppName = "keyboard.wtf Paid Production Candidate";
#else
    public const string AppName = "keyboard.wtf";
#endif
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Sync(bool enabled, string executablePath)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null)
                return;

            if (!enabled)
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
                return;
            }

            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                AppLog.Warning("Startup registration skipped: executable path was not found");
                return;
            }

            key.SetValue(AppName, Quote(executablePath), RegistryValueKind.String);
            AppLog.Info($"Startup registration enabled: {executablePath}");
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Startup registration update failed");
        }
    }

    public static string RegisteredCommand()
    {
        if (!OperatingSystem.IsWindows())
            return "";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(AppName)?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static bool IsRegisteredFor(string executablePath)
    {
        var command = RegisteredCommand().Trim();
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(executablePath))
            return false;

        return string.Equals(Unquote(command), executablePath, StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string path) => $"\"{path.Trim().Trim('"')}\"";

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            return trimmed[1..^1];
        return trimmed;
    }
}
