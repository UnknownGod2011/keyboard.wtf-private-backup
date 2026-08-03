namespace KeyboardWtf;

using System.Diagnostics;
using KeyboardWtf.Services;

internal static class AppLog
{
    private static readonly object Lock = new();
    private static string _logFile;

    public static void Init()
    {
        var dir = Path.Combine(SettingsService.AppDataDir, "logs");
        Directory.CreateDirectory(dir);
        _logFile = Path.Combine(dir, "keyboard.wtf.log");
        Info("keyboard.wtf logging started");
    }

    public static void Verbose(string text) => Write("VERBOSE", text);
    public static void Verbose(Exception ex, string text) => Write("VERBOSE", $"{text}: {ex.Message}");
    public static void Info(string text) => Write("INFO", text);
    public static void Info(Exception ex, string text) => Write("INFO", $"{text}: {ex.Message}");
    public static void Warning(string text) => Write("WARN", text);
    public static void Warning(Exception ex, string text) => Write("WARN", $"{text}: {ex.Message}");
    public static void Error(string text) => Write("ERROR", text);
    public static void Error(Exception ex, string text) => Write("ERROR", $"{text}: {ex.Message}");

    private static void Write(string level, string text)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {text}";
        Debug.WriteLine(line);

        if (string.IsNullOrEmpty(_logFile))
            return;

        try
        {
            lock (Lock)
                File.AppendAllText(_logFile, line + Environment.NewLine);
        }
        catch
        {
            // Logging must never interrupt a voice command.
        }
    }
}
