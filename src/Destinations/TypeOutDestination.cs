namespace KeyboardWtf.Destinations;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;
using KeyboardWtf.Services;

[SupportedOSPlatform("windows")]
public class TypeOutDestination : IDestination
{
    public string Name => "Type Out";
    public string Description => "Type voice directly into the active window";
    public bool IsAvailable => true;
    public DestinationCategory Category => DestinationCategory.Raw;
    public string AiPrompt => null;

    private const int PreTypeDelayMs = 120;
    private const int ClipboardRestoreDelayMs = 140;
    private readonly Func<TextInsertionTarget> _captureTarget;
    private readonly Func<ClipboardHelper.Snapshot> _captureClipboard;
    private readonly Func<string, bool> _setClipboard;
    private readonly Func<string> _getClipboard;
    private readonly Func<ClipboardHelper.Snapshot, bool> _restoreClipboard;
    private readonly Func<bool> _sendPaste;
    private readonly Func<int, CancellationToken, Task> _delay;

    public TypeOutDestination()
        : this(
            TextInsertionTarget.Capture,
            ClipboardHelper.Capture,
            ClipboardHelper.SetText,
            ClipboardHelper.GetText,
            ClipboardHelper.Restore,
            SendPasteShortcut,
            (milliseconds, token) => Task.Delay(milliseconds, token))
    {
    }

    internal TypeOutDestination(
        Func<TextInsertionTarget> captureTarget,
        Func<ClipboardHelper.Snapshot> captureClipboard,
        Func<string, bool> setClipboard,
        Func<string> getClipboard,
        Func<ClipboardHelper.Snapshot, bool> restoreClipboard,
        Func<bool> sendPaste,
        Func<int, CancellationToken, Task> delay)
    {
        _captureTarget = captureTarget;
        _captureClipboard = captureClipboard;
        _setClipboard = setClipboard;
        _getClipboard = getClipboard;
        _restoreClipboard = restoreClipboard;
        _sendPaste = sendPaste;
        _delay = delay;
    }

    public Task<bool> SendAsync(string text) =>
        SendAsync(text, _captureTarget(), CancellationToken.None);

    internal async Task<bool> SendAsync(
        string text,
        TextInsertionTarget expectedTarget,
        CancellationToken cancellationToken)
    {
        ClipboardHelper.Snapshot clipboard = null;
        var clipboardContainsDelivery = false;
        try
        {
            if (!TextInsertionTarget.Matches(expectedTarget, _captureTarget()))
            {
                AppLog.Warning("TypeOut blocked because the intended window is no longer active");
                return false;
            }

            clipboard = _captureClipboard();
            if (clipboard is null)
            {
                AppLog.Warning("TypeOut could not preserve the current clipboard");
                return false;
            }
            if (!_setClipboard(text))
                return false;
            clipboardContainsDelivery = true;
            await _delay(PreTypeDelayMs, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!TextInsertionTarget.Matches(expectedTarget, _captureTarget()))
            {
                AppLog.Warning("TypeOut blocked because focus changed before paste");
                return false;
            }

            var pasted = _sendPaste();
            if (pasted)
                await _delay(ClipboardRestoreDelayMs, CancellationToken.None);
            AppLog.Info(pasted
                ? $"Pasted {text.Length} chars into active window"
                : "TypeOut paste shortcut failed; text remains on clipboard");
            return pasted;
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("TypeOut cancelled before paste");
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"TypeOut failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (clipboardContainsDelivery && clipboard is not null
                && string.Equals(_getClipboard(), text, StringComparison.Ordinal)
                && !_restoreClipboard(clipboard))
                AppLog.Warning("TypeOut could not restore the previous clipboard contents");
        }
    }

    private static bool SendPasteShortcut()
    {
        var inputs = new[]
        {
            KeyboardInput(VkControl, 0),
            KeyboardInput((ushort)Keys.V, 0),
            KeyboardInput((ushort)Keys.V, KeyeventfKeyup),
            KeyboardInput(VkControl, KeyeventfKeyup),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            AppLog.Warning($"TypeOut: sent {sent}/{inputs.Length} paste input events");
            return false;
        }

        return true;
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

    // --- Win32 P/Invoke ---

    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const ushort VkControl = 0x11;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetMessageExtraInfo();
}
