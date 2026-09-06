using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class KeyboardInputOwnershipTests
{
    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    public void Held_user_modifiers_refuse_input_without_sending_any_key_including_releases(int held)
    {
        var sent = false;
        Assert.Throws<InvalidOperationException>(() => BrowserTrustedInput.WithInputOwnership(
            (IntPtr)17, () => (IntPtr)17, key => key == held, () => sent = true));
        Assert.False(sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void User_switching_foreground_before_dispatch_never_redirects_the_action(bool changeDuringCheck)
    {
        var sent = false;
        var reads = 0;
        Assert.Throws<InvalidOperationException>(() => BrowserTrustedInput.WithInputOwnership(
            (IntPtr)17, () => ++reads == 1 && changeDuringCheck ? (IntPtr)17 : (IntPtr)18,
            _ => false, () => sent = true));
        Assert.False(sent);
    }

    [Fact]
    public void Neutral_owned_foreground_dispatches_once()
    {
        var sent = 0;
        BrowserTrustedInput.WithInputOwnership((IntPtr)17, () => (IntPtr)17, _ => false, () => sent++);
        Assert.Equal(1, sent);
    }

    [Fact]
    public void Cancellation_before_keyboard_ownership_never_observes_or_sends_input()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var observed = false;
        var sent = false;
        Assert.ThrowsAny<OperationCanceledException>(() => BrowserTrustedInput.WithInputOwnership(
            (IntPtr)17, () => { observed = true; return (IntPtr)17; }, _ => false,
            () => sent = true, cancellation.Token));
        Assert.False(observed);
        Assert.False(sent);
    }

    [Fact]
    public void Cancellation_during_modifier_validation_never_sends_input()
    {
        using var cancellation = new CancellationTokenSource();
        var sent = false;
        Assert.ThrowsAny<OperationCanceledException>(() => BrowserTrustedInput.WithInputOwnership(
            (IntPtr)17, () => (IntPtr)17,
            _ => { cancellation.Cancel(); return false; },
            () => sent = true, cancellation.Token));
        Assert.False(sent);
    }

    [Theory]
    [InlineData("new tab")]
    [InlineData("close tab")]
    [InlineData("reopen tab")]
    [InlineData("refresh")]
    [InlineData("back")]
    [InlineData("forward")]
    [InlineData("focus address")]
    [InlineData("find")]
    [InlineData("downloads")]
    [InlineData("history")]
    public void Every_supported_browser_shortcut_has_a_bounded_native_chord_without_windows_keys(string action)
    {
        var keys = JarvisAutomationService.NativeShortcutKeys(JarvisAutomationService.BrowserShortcutForAction(action));
        Assert.InRange(keys.Length, 1, 3);
        Assert.DoesNotContain((ushort)0x5B, keys);
        Assert.DoesNotContain((ushort)0x5C, keys);
    }

    [Fact]
    public void Arbitrary_text_is_not_a_keyboard_program()
    {
        Assert.Throws<InvalidOperationException>(() => JarvisAutomationService.NativeShortcutKeys("file explorer"));
        Assert.Throws<InvalidOperationException>(() => JarvisAutomationService.NativeShortcutKeys("^+a"));
    }
}
