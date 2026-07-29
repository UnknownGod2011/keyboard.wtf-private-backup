namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class JarvisShortcutTests
{
    [Theory]
    [InlineData("new tab", "^t")]
    [InlineData("close this website", "^w")]
    [InlineData("go to the next tab", "^{TAB}")]
    [InlineData("switch to Chrome's fourth tab using Ctrl+4", "^4")]
    [InlineData("go to the 7th tab", "^7")]
    [InlineData("go to the previous tab", "^+{TAB}")]
    [InlineData("reopen closed tab", "^+t")]
    [InlineData("refresh", "^r")]
    [InlineData("back", "%{LEFT}")]
    [InlineData("forward", "%{RIGHT}")]
    [InlineData("downloads", "^j")]
    [InlineData("history", "^h")]
    [InlineData("find on page", "^f")]
    public void BrowserActionsMapOnlyToAllowlistedShortcuts(string action, string expected)
    {
        Assert.Equal(expected, JarvisAutomationService.BrowserShortcutForAction(action));
    }

    [Theory]
    [InlineData("create a new desktop", 0x44)]
    [InlineData("switch to next desktop", 0x27)]
    [InlineData("switch to previous desktop", 0x25)]
    [InlineData("close current desktop", 0x73)]
    [InlineData("move window to desktop", 0)]
    public void VirtualDesktopActionsMapOnlyToWindowsShortcuts(string action, int expected)
    {
        Assert.Equal((byte)expected, JarvisAutomationService.VirtualDesktopKeyForAction(action));
    }

    [Theory]
    [InlineData("start recording", 3, 0x12)]
    [InlineData("stop recording", 3, 0x12)]
    [InlineData("open game bar", 2, 0x47)]
    [InlineData("record screen region", 3, 0x10)]
    public void RecordingActionsMapOnlyToAllowlistedWindowsShortcuts(
        string action,
        int expectedLength,
        int expectedModifier)
    {
        var keys = JarvisAutomationService.WindowsRecordingShortcutForAction(action);

        Assert.NotNull(keys);
        Assert.Equal(expectedLength, keys.Length);
        Assert.Equal(0x5B, keys[0]);
        Assert.Equal(expectedModifier, keys[1]);
    }

    [Fact]
    public void UnknownRecordingActionHasNoShortcut()
    {
        Assert.Null(JarvisAutomationService.WindowsRecordingShortcutForAction("upload recording"));
    }
}
