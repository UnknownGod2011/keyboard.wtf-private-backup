namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

internal static class BrowserTrustedInput
{
    private static readonly object DispatchLock = new();
    private static readonly int[] Modifiers = [0x10, 0x11, 0x12, 0x5B, 0x5C];
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkShift = 0x10;
    private const ushort VkC = 0x43;
    private const ushort VkV = 0x56;
    private const ushort VkBack = 0x08;
    private const ushort VkReturn = 0x0D;
    private const ushort VkSpace = 0x20;
    private const ushort VkLeft = 0x25;
    private const ushort VkRight = 0x27;
    private const ushort VkDown = 0x28;

    internal static int NativeInputSize => Marshal.SizeOf<Input>();
    internal static uint CurrentThreadId => GetCurrentThreadId();
    internal static void FocusChromeWindowForAutomation(string expectedWindowTitle = "") =>
        FocusChromeWindow(expectedWindowTitle);

    public static void PressEnter(string expectedWindowTitle = "") => Send(
        expectedWindowTitle,
        Key(VkReturn),
        Key(VkReturn, keyUp: true));

    public static void PressSpace(string expectedWindowTitle = "") => Send(
        expectedWindowTitle,
        Key(VkSpace),
        Key(VkSpace, keyUp: true));

    public static void PressAltArrowDown(string expectedWindowTitle = "") => Send(
        expectedWindowTitle,
        Key(VkMenu),
        Key(VkDown),
        Key(VkDown, keyUp: true),
        Key(VkMenu, keyUp: true));

    public static void TypeTextAndPressEnter(
        string text,
        string expectedWindowTitle = "")
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Listbox type-ahead text is required.", nameof(text));

        var inputs = new List<Input>(text.Length * 2 + 2);
        foreach (var character in text)
        {
            inputs.Add(UnicodeKey(character));
            inputs.Add(UnicodeKey(character, keyUp: true));
        }
        inputs.Add(Key(VkReturn));
        inputs.Add(Key(VkReturn, keyUp: true));
        Send(expectedWindowTitle, inputs.ToArray());
    }

    public static void Paste(string expectedWindowTitle = "") => Send(
        expectedWindowTitle,
        Key(VkControl),
        Key(VkV),
        Key(VkV, keyUp: true),
        Key(VkControl, keyUp: true));

    public static void SelectPreviousText(
        int textElementCount,
        string expectedWindowTitle = "")
    {
        if (textElementCount is <= 0 or > 4096)
            throw new ArgumentOutOfRangeException(
                nameof(textElementCount),
                "Editor read-back supports between 1 and 4096 text elements.");

        var inputs = new List<Input>((textElementCount * 2) + 2)
        {
            Key(VkShift),
        };
        for (var index = 0; index < textElementCount; index++)
        {
            inputs.Add(Key(VkLeft));
            inputs.Add(Key(VkLeft, keyUp: true));
        }
        inputs.Add(Key(VkShift, keyUp: true));
        Send(expectedWindowTitle, inputs.ToArray());
    }

    public static void CopySelection(string expectedWindowTitle = "") => Send(
        expectedWindowTitle,
        Key(VkControl),
        Key(VkC),
        Key(VkC, keyUp: true),
        Key(VkControl, keyUp: true));

    public static void DeleteSelection(string expectedWindowTitle = "") => Send(
        expectedWindowTitle,
        Key(VkBack),
        Key(VkBack, keyUp: true));

    public static void CollapseSelectionToEnd(string expectedWindowTitle = "") => Send(
        expectedWindowTitle,
        Key(VkRight),
        Key(VkRight, keyUp: true));

    private static void Send(string expectedWindowTitle, params Input[] inputs)
    {
        if (inputs.Length == 0)
            return;
        var target = FocusChromeWindow(expectedWindowTitle);
        DispatchToWindow(target, inputs);
    }

    internal static void PressChordInWindow(IntPtr expectedWindow, params ushort[] keys) =>
        DispatchToWindow(expectedWindow, keys.Select(key => Key(key))
            .Concat(keys.Reverse().Select(key => Key(key, keyUp: true))).ToArray());

    // All direct and trusted browser keyboard dispatch shares one boundary.
    // Do not release physically held user modifiers: refuse the action instead.
    internal static void WithInputOwnership(IntPtr expectedWindow, Func<IntPtr> foreground, Func<int, bool> isDown, Action send)
    {
        lock (DispatchLock)
        {
            if (expectedWindow == IntPtr.Zero || foreground() != expectedWindow)
                throw new InvalidOperationException("The target window changed. No keyboard input was sent.");
            if (Modifiers.Any(isDown))
                throw new InvalidOperationException("A keyboard modifier is held. Release the keys and repeat the request; no keyboard input was sent.");
            if (foreground() != expectedWindow)
                throw new InvalidOperationException("The target window changed. No keyboard input was sent.");
            send();
        }
    }

    private static void DispatchToWindow(IntPtr target, Input[] inputs) => WithInputOwnership(
        target, GetForegroundWindow, key => (GetAsyncKeyState(key) & 0x8000) != 0, () =>
        {
            // One SendInput batch prevents our down/up events being interleaved
            // with each other or with user input. A partial dispatch releases only
            // keys whose downs this batch actually inserted, never global modifiers.
            var sent = SendInput((uint)inputs.Length, inputs, NativeInputSize);
            if (sent == inputs.Length)
                return;
            var error = Marshal.GetLastWin32Error();
            var held = new Dictionary<(ushort Key, ushort Scan), Input>();
            foreach (var input in inputs.Take((int)sent))
            {
                var keyboard = input.Union.Keyboard;
                var id = (keyboard.VirtualKey, keyboard.ScanCode);
                if ((keyboard.Flags & KeyeventfKeyup) != 0)
                    held.Remove(id);
                else
                    held[id] = input;
            }
            var releases = held.Values.Reverse().Select(input =>
            {
                input.Union.Keyboard.Flags |= KeyeventfKeyup;
                return input;
            }).ToArray();
            if (releases.Length > 0)
                _ = SendInput((uint)releases.Length, releases, NativeInputSize);
            throw new Win32Exception(error, "Windows did not dispatch the complete keyboard action. It was not retried.");
        });

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private static IntPtr FocusChromeWindow(string expectedWindowTitle)
    {
        var foreground = GetForegroundWindow();
        var target = FindChromeWindow(expectedWindowTitle);
        if (target == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "The controlled Chrome window could not be identified safely for trusted browser input.");
        }
        if (foreground == target)
            return target;

        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        var targetThread = GetWindowThreadProcessId(target, out _);
        var attachedForeground = foregroundThread != 0
            && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != 0
            && targetThread != currentThread
            && targetThread != foregroundThread
            && AttachThreadInput(currentThread, targetThread, true);
        try
        {
            if (IsIconic(target))
                ShowWindow(target, 9);
            BringWindowToTop(target);
            SetForegroundWindow(target);
            SetFocus(target);
        }
        finally
        {
            if (attachedTarget)
                AttachThreadInput(currentThread, targetThread, false);
            if (attachedForeground)
                AttachThreadInput(currentThread, foregroundThread, false);
        }

        Thread.Sleep(120);
        if (GetForegroundWindow() != target)
        {
            throw new InvalidOperationException(
                "Windows did not allow Jarvis to focus the controlled Chrome window.");
        }
        return target;
    }

    private static IntPtr FindChromeWindow(string expectedWindowTitle)
    {
        var candidates = new List<(IntPtr Handle, string Title)>();
        EnumWindows((handle, parameter) =>
        {
            if (!IsWindowVisible(handle))
                return true;
            _ = GetWindowThreadProcessId(handle, out var processId);
            try
            {
                if (!string.Equals(
                    Process.GetProcessById((int)processId).ProcessName,
                    "chrome",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                return true;
            }

            var title = WindowTitle(handle);
            if (!string.IsNullOrWhiteSpace(title))
                candidates.Add((handle, title));
            return true;
        }, IntPtr.Zero);

        var expected = (expectedWindowTitle ?? "").Trim();
        if (expected.Length > 0)
        {
            var titleMatch = candidates
                .Where(candidate => candidate.Title.Contains(
                    expected,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (titleMatch.Length == 1)
                return titleMatch[0].Handle;
        }
        return candidates.Count == 1 ? candidates[0].Handle : IntPtr.Zero;
    }

    private static string WindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
            return "";
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static Input Key(ushort virtualKey, bool keyUp = false) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyeventfKeyup : 0,
            },
        },
    };

    private static Input UnicodeKey(char character, bool keyUp = false) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                ScanCode = character,
                Flags = KeyeventfUnicode | (keyUp ? KeyeventfKeyup : 0),
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        // INPUT is a union whose native size is determined by MOUSEINPUT,
        // even when SendInput is only dispatching keyboard events. Keeping all
        // union members here produces the required 40-byte x64 / 28-byte x86
        // structure instead of an undersized keyboard-only INPUT.
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public UIntPtr Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(
        uint attachThread,
        uint attachToThread,
        bool attach);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr window,
        StringBuilder text,
        int maximumCount);
}
