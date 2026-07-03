namespace KeyboardWtf.Automation.Input;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
public sealed class WindowsInputController : IInputController
{
    private readonly InputSafetyPolicy _safety;

    public WindowsInputController(InputSafetyPolicy safety = null)
    {
        _safety = safety ?? new InputSafetyPolicy();
    }

    public Task<InputExecutionResult> PressKeyAsync(InputKey key, InputSafetyContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var action = InputAction.Press(key);
        var decision = _safety.Evaluate(action, context);
        if (decision.Risk == InputRisk.Blocked)
            return Task.FromResult(InputExecutionResult.Block(decision.Reason, "windows-sendinput"));
        if (decision.Risk == InputRisk.NeedsConfirmation)
            return Task.FromResult(InputExecutionResult.NeedsConfirmation(decision.Reason, "windows-sendinput"));

        if (!TryVk(key, out var vk))
            return Task.FromResult(InputExecutionResult.Failed($"Unsupported key: {key}.", "windows-sendinput"));
        var result = SendSequence([KeyboardInput(vk, 0), KeyboardInput(vk, KeyeventfKeyup)]);
        return Task.FromResult(result.Ok
            ? InputExecutionResult.Success($"Pressed {key}.", "windows-sendinput", 1)
            : InputExecutionResult.Failed($"SendInput failed for {key}. Win32 error: {result.Win32Error}.", "windows-sendinput"));
    }

    public Task<InputExecutionResult> PressChordAsync(InputChord chord, InputSafetyContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var action = InputAction.ChordAction(chord);
        var decision = _safety.Evaluate(action, context);
        if (decision.Risk == InputRisk.Blocked)
            return Task.FromResult(InputExecutionResult.Block(decision.Reason, "windows-sendinput"));
        if (decision.Risk == InputRisk.NeedsConfirmation)
            return Task.FromResult(InputExecutionResult.NeedsConfirmation(decision.Reason, "windows-sendinput"));
        if (chord?.Keys == null || chord.Keys.Count == 0)
            return Task.FromResult(InputExecutionResult.Failed("Shortcut is missing.", "windows-sendinput"));

        var vks = new List<ushort>();
        foreach (var key in chord.Keys)
        {
            if (!TryVk(key, out var vk))
                return Task.FromResult(InputExecutionResult.Failed($"Unsupported key in shortcut: {key}.", "windows-sendinput"));
            vks.Add(vk);
        }

        var inputs = new List<INPUT>();
        inputs.AddRange(vks.Select(vk => KeyboardInput(vk, 0)));
        for (var i = vks.Count - 1; i >= 0; i--)
            inputs.Add(KeyboardInput(vks[i], KeyeventfKeyup));

        var result = SendSequence(inputs.ToArray());
        return Task.FromResult(result.Ok
            ? InputExecutionResult.Success($"Pressed {chord.Display}.", "windows-sendinput", vks.Count)
            : InputExecutionResult.Failed($"SendInput failed for {chord.Display}. Win32 error: {result.Win32Error}.", "windows-sendinput"));
    }

    private static SendInputResult SendSequence(INPUT[] inputs)
    {
        if (inputs.Length == 0)
            return new SendInputResult(false, 0);
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return new SendInputResult(sent == inputs.Length, Marshal.GetLastWin32Error());
    }

    private static bool TryVk(InputKey key, out ushort vk)
    {
        vk = key switch
        {
            InputKey.Enter => 0x0D,
            InputKey.Space => 0x20,
            InputKey.Tab => 0x09,
            InputKey.Escape => 0x1B,
            InputKey.ArrowUp => 0x26,
            InputKey.ArrowDown => 0x28,
            InputKey.ArrowLeft => 0x25,
            InputKey.ArrowRight => 0x27,
            InputKey.Home => 0x24,
            InputKey.End => 0x23,
            InputKey.PageUp => 0x21,
            InputKey.PageDown => 0x22,
            InputKey.Backspace => 0x08,
            InputKey.Delete => 0x2E,
            InputKey.Control => 0x11,
            InputKey.Alt => 0x12,
            InputKey.Shift => 0x10,
            InputKey.Windows => 0x5B,
            InputKey.A => 0x41,
            InputKey.C => 0x43,
            InputKey.F => 0x46,
            InputKey.L => 0x4C,
            InputKey.R => 0x52,
            InputKey.S => 0x53,
            InputKey.T => 0x54,
            InputKey.V => 0x56,
            InputKey.W => 0x57,
            _ => 0,
        };
        return vk != 0;
    }

    private static INPUT KeyboardInput(ushort key, uint flags) =>
        new()
        {
            type = InputKeyboard,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = key,
                    dwFlags = flags,
                    dwExtraInfo = GetMessageExtraInfo()
                }
            }
        };

    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private readonly record struct SendInputResult(bool Ok, int Win32Error);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();
}
