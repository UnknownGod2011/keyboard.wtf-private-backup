namespace KeyboardWtf.Helpers;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

[SupportedOSPlatform("windows")]
internal static class ClipboardHelper
{
    public sealed class Snapshot
    {
        internal DataObject Data { get; init; }
        internal bool WasEmpty { get; init; }
    }

    public static string GetText()
    {
        string result = null;
        var thread = new Thread(() => result = GetClipboardText());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(3000);
        return result ?? "";
    }

    public static bool SetText(string text)
    {
        bool success = false;
        var thread = new Thread(() => success = RetryClipboardOperation(
            () => SetClipboardText(text)));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(3000);
        return success;
    }

    public static Snapshot Capture()
    {
        Snapshot snapshot = null;
        var thread = new Thread(() =>
        {
            for (var attempt = 0; attempt < 5 && snapshot is null; attempt++)
            {
                try
                {
                    var source = Clipboard.GetDataObject();
                    if (source == null)
                    {
                        snapshot = new Snapshot { WasEmpty = true };
                        return;
                    }
                    var copy = new DataObject();
                    foreach (var format in source.GetFormats())
                    {
                        try
                        {
                            var value = source.GetData(format);
                            if (value != null)
                                copy.SetData(format, value);
                        }
                        catch { }
                    }
                    snapshot = new Snapshot { Data = copy, WasEmpty = false };
                }
                catch
                {
                    if (attempt < 4)
                        Thread.Sleep(20 * (attempt + 1));
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(3000);
        return snapshot;
    }

    public static bool Restore(Snapshot snapshot)
    {
        if (snapshot == null)
            return false;
        var success = false;
        var thread = new Thread(() =>
        {
            success = RetryClipboardOperation(() =>
            {
                try
                {
                    if (snapshot.WasEmpty)
                        Clipboard.Clear();
                    else if (snapshot.Data != null)
                        Clipboard.SetDataObject(snapshot.Data, true);
                    else
                        return false;
                    return true;
                }
                catch { return false; }
            });
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(3000);
        return success;
    }

    private static bool RetryClipboardOperation(Func<bool> operation)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (operation())
                    return true;
            }
            catch { }
            if (attempt < 4)
                Thread.Sleep(20 * (attempt + 1));
        }
        return false;
    }

    private static string GetClipboardText()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return "";
        try
        {
            var handle = GetClipboardData(13);
            if (handle == IntPtr.Zero)
                return "";
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return "";
            try { return Marshal.PtrToStringUni(pointer) ?? ""; }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    private static bool SetClipboardText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var hGlobal = GlobalAlloc(0x0002, (UIntPtr)bytes);
            if (hGlobal == IntPtr.Zero) return false;
            var locked = GlobalLock(hGlobal);
            if (locked == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
            Marshal.Copy(text.ToCharArray(), 0, locked, text.Length);
            Marshal.WriteInt16(locked, text.Length * 2, 0);
            GlobalUnlock(hGlobal);
            if (SetClipboardData(13, hGlobal) == IntPtr.Zero) { GlobalFree(hGlobal); return false; }
            return true;
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint f);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint f, IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint f, UIntPtr n);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr h);
}
