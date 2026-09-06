namespace KeyboardWtf.Services;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// Native window requests never search browser tabs or inject foreground keys.
// Snapshot identity is retained through dispatch and verification; a window
// opened after the request is not part of its target set.
internal static class NativeWindowCloser
{
    internal sealed record Window(IntPtr Handle, uint ProcessId, uint ThreadId, string ClassName, string ProcessName, string Title)
    {
        public string Id => Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture);
        public bool IsExplorer => ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            && ClassName is "CabinetWClass" or "ExploreWClass";
        public bool IsBrowser => ProcessName.ToLowerInvariant() is
            "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi" or "arc";
    }

    internal sealed record Outcome(Window[] Targets, Window[] Remaining, int Requested)
    {
        public bool Succeeded => Targets.Length > 0 && Remaining.Length == 0;
    }

    internal static Window[] Enumerate()
    {
        var windows = new List<Window>();
        EnumWindows((handle, _) =>
        {
            if (IsWindowVisible(handle) && GetWindow(handle, 4) == IntPtr.Zero)
            {
                var window = Read(handle);
                if (window != null && window.Title.Length > 0
                    && (window.ProcessName != "explorer" || window.IsExplorer))
                    windows.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }

    internal static Window[] Select(Window[] windows, string target, bool all, string windowId, IntPtr foreground)
    {
        var name = (target ?? "").Trim().ToLowerInvariant();
        Window[] ById(IEnumerable<Window> candidates) => candidates
            .Where(w => string.IsNullOrWhiteSpace(windowId) || w.Id.Equals(windowId.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (name.Length == 0)
            return !string.IsNullOrWhiteSpace(windowId) ? ById(windows)
                : all ? [] : windows.Where(w => w.Handle == foreground).ToArray();

        // These are OS app identities, not natural-language command examples.
        var process = name switch
        {
            "file explorer" or "windows explorer" or "explorer.exe" => "explorer",
            "google chrome" => "chrome",
            "microsoft edge" or "edge" => "msedge",
            _ => name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name,
        };
        if (process == "explorer")
            return ById(windows.Where(w => w.IsExplorer));

        var byProcess = windows.Where(w => w.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byProcess.Length > 0)
            return ById(byProcess);

        // A website title is not authority to close its entire browser window.
        // Resolve native titles only. Ambiguous matches are returned to the caller.
        return ById(windows.Where(w => !w.IsBrowser && w.Title.Equals(target?.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    internal static async Task<Outcome> CloseAsync(
        Window[] targets,
        CancellationToken token,
        Func<IntPtr, Window> read = null,
        Func<IntPtr, bool> requestClose = null,
        TimeSpan? timeout = null)
    {
        read ??= Read;
        requestClose ??= handle => PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
        targets = targets.DistinctBy(w => w.Handle).ToArray();
        var requested = 0;
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            if (!SameIdentity(target, read(target.Handle)))
                continue; // Already closed, or handle recycled. Never touch its replacement.
            token.ThrowIfCancellationRequested();
            if (requestClose(target.Handle))
                requested++;
        }
        var watch = Stopwatch.StartNew();
        Window[] remaining;
        do
        {
            token.ThrowIfCancellationRequested();
            remaining = targets.Where(w => SameIdentity(w, read(w.Handle))).ToArray();
            if (remaining.Length == 0 || watch.Elapsed >= (timeout ?? TimeSpan.FromSeconds(2)))
                break;
            await Task.Delay(40, token).ConfigureAwait(false);
        } while (true);
        return new Outcome(targets, remaining, requested);
    }

    private static bool SameIdentity(Window expected, Window current) => current != null
        && expected.Handle == current.Handle && expected.ProcessId == current.ProcessId
        && expected.ThreadId == current.ThreadId && expected.ClassName == current.ClassName;

    internal static Window Read(IntPtr handle)
    {
        if (!IsWindow(handle))
            return null;
        var thread = GetWindowThreadProcessId(handle, out var pid);
        var className = new StringBuilder(256);
        GetClassName(handle, className, className.Capacity);
        var title = new StringBuilder(GetWindowTextLength(handle) + 1);
        GetWindowText(handle, title, title.Capacity);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return new Window(handle, pid, thread, className.ToString(), process.ProcessName, title.ToString());
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder value, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder value, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
