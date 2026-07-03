namespace KeyboardWtf.Services;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using KeyboardWtf.Helpers;

public enum TaskContextSource
{
    None,
    SelectedText,
    Clipboard,
    ActiveWindow,
    ControlledBrowserPage,
}

public sealed class TaskContextRequest
{
    public bool UserExplicitlyRequested { get; init; }
    public bool UseSelectedText { get; init; }
    public bool UseClipboard { get; init; }
    public bool IncludeActiveWindow { get; init; } = true;
}

public sealed class TaskContextResult
{
    public bool Captured { get; init; }
    public TaskContextSource Source { get; init; }
    public string Text { get; init; } = "";
    public string Summary { get; init; } = "";
    public IReadOnlyDictionary<string, string> StructuredFields { get; init; } =
        new Dictionary<string, string>();
    public string ActiveWindowTitle { get; init; } = "";
    public string ActiveProcess { get; init; } = "";
    public bool Truncated { get; init; }
    public string Error { get; init; } = "";
}

public sealed class TaskContextCaptureService
{
    public const int MaxContextChars = 12000;
    private readonly Func<CancellationToken, Task<string>> _selectedTextProvider;
    private readonly Func<string> _clipboardProvider;
    private readonly Func<(string Title, string Process)> _windowProvider;

    public TaskContextCaptureService(
        Func<CancellationToken, Task<string>> selectedTextProvider = null,
        Func<string> clipboardProvider = null,
        Func<(string Title, string Process)> windowProvider = null)
    {
        _selectedTextProvider = selectedTextProvider ?? CaptureSelectedTextAsync;
        _clipboardProvider = clipboardProvider ?? ClipboardHelper.GetText;
        _windowProvider = windowProvider ?? GetActiveWindow;
    }

    public async Task<TaskContextResult> CaptureAsync(
        TaskContextRequest request,
        CancellationToken token)
    {
        request ??= new TaskContextRequest();
        var window = request.IncludeActiveWindow
            ? _windowProvider()
            : (Title: "", Process: "");
        if (!request.UserExplicitlyRequested)
        {
            return new TaskContextResult
            {
                Captured = false,
                ActiveWindowTitle = window.Item1,
                ActiveProcess = window.Item2,
                Error = "Private context was not captured because the user did not explicitly request it.",
            };
        }

        string text = "";
        var source = TaskContextSource.None;
        var selectedFailed = false;
        if (request.UseSelectedText)
        {
            try
            {
                text = await _selectedTextProvider(token)
                    .WaitAsync(TimeSpan.FromSeconds(4), token);
            }
            catch (TimeoutException)
            {
                selectedFailed = true;
            }
            if (!string.IsNullOrWhiteSpace(text))
                source = TaskContextSource.SelectedText;
            else
                selectedFailed = true;
        }
        if (source == TaskContextSource.None && request.UseClipboard)
        {
            text = _clipboardProvider();
            if (!string.IsNullOrWhiteSpace(text))
                source = TaskContextSource.Clipboard;
        }
        if (source == TaskContextSource.None && request.IncludeActiveWindow
            && (!string.IsNullOrWhiteSpace(window.Title) || !string.IsNullOrWhiteSpace(window.Process)))
        {
            source = TaskContextSource.ActiveWindow;
        }

        var compacted = Compact(text);
        return new TaskContextResult
        {
            Captured = source != TaskContextSource.None,
            Source = source,
            Text = compacted.Text,
            Summary = compacted.Summary,
            StructuredFields = compacted.Fields,
            ActiveWindowTitle = window.Title,
            ActiveProcess = window.Process,
            Truncated = compacted.Truncated,
            Error = source == TaskContextSource.None
                ? selectedFailed
                    ? "No selected text was detected and no requested fallback context was available."
                    : "No requested context was available."
                : "",
        };
    }

    public static (string Text, string Summary, IReadOnlyDictionary<string, string> Fields, bool Truncated) Compact(
        string value)
    {
        var clean = Regex.Replace((value ?? "").ReplaceLineEndings("\n"), @"[ \t]+", " ").Trim();
        var truncated = clean.Length > MaxContextChars;
        var text = truncated ? clean[..MaxContextChars].Trim() : clean;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator is < 1 or > 80)
                continue;
            var key = line[..separator].Trim();
            var fieldValue = line[(separator + 1)..].Trim();
            if (key.Length == 0 || fieldValue.Length == 0)
                continue;
            fields[key] = fieldValue.Length <= 500 ? fieldValue : fieldValue[..500].Trim();
            if (fields.Count >= 40)
                break;
        }
        var summary = text.Length <= 1400
            ? text
            : text[..1200].Trim() + $" ... [{text.Length} characters captured]";
        return (text, summary, fields, truncated);
    }

    private static async Task<string> CaptureSelectedTextAsync(CancellationToken token)
    {
        var previous = ClipboardHelper.Capture();
        var marker = $"keyboard-wtf-context-{Guid.NewGuid():N}";
        try
        {
            ClipboardHelper.SetText(marker);
            SendCopyShortcut();
            await Task.Delay(240, token);
            var selected = ClipboardHelper.GetText();
            return selected == marker ? "" : selected;
        }
        finally
        {
            ClipboardHelper.Restore(previous);
        }
    }

    private static void SendCopyShortcut()
    {
        const byte control = 0x11;
        const byte c = 0x43;
        const uint keyUp = 0x0002;
        keybd_event(control, 0, 0, UIntPtr.Zero);
        keybd_event(c, 0, 0, UIntPtr.Zero);
        keybd_event(c, 0, keyUp, UIntPtr.Zero);
        keybd_event(control, 0, keyUp, UIntPtr.Zero);
    }

    private static (string Title, string Process) GetActiveWindow()
    {
        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
                return ("", "");
            var length = GetWindowTextLength(handle);
            var buffer = new char[Math.Max(1, length + 1)];
            GetWindowText(handle, buffer, buffer.Length);
            GetWindowThreadProcessId(handle, out var processId);
            var process = processId == 0 ? "" : Process.GetProcessById((int)processId).ProcessName;
            return (new string(buffer).TrimEnd('\0'), process);
        }
        catch
        {
            return ("", "");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, char[] text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
