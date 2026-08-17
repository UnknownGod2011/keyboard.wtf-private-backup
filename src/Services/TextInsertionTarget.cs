namespace KeyboardWtf.Services;

using System.Runtime.InteropServices;
using System.Text;

internal sealed record TextInsertionTarget(
    IntPtr WindowHandle,
    uint ProcessId,
    string WindowTitle)
{
    public static TextInsertionTarget Capture()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
            return null;
        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0)
            return null;
        return new TextInsertionTarget(handle, processId, ReadWindowTitle(handle));
    }

    public bool IsCurrent() => Matches(this, Capture());

    internal static bool Matches(TextInsertionTarget expected, TextInsertionTarget current)
    {
        if (expected is null || current is null
            || expected.WindowHandle == IntPtr.Zero
            || expected.ProcessId == 0)
            return false;
        if (expected.WindowHandle != current.WindowHandle
            || expected.ProcessId != current.ProcessId)
            return false;
        return string.IsNullOrWhiteSpace(expected.WindowTitle)
            || string.Equals(expected.WindowTitle, current.WindowTitle, StringComparison.Ordinal);
    }

    private static string ReadWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
            return string.Empty;
        var title = new StringBuilder(length + 1);
        _ = GetWindowText(handle, title, title.Capacity);
        return title.ToString();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
}
