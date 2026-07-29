namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class JarvisShortcutTests
{
    [Theory]
    [InlineData("new tab", "^t")]
    [InlineData("close this website", "^w")]
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
    [InlineData("switch to Chrome's fourth tab using Ctrl+4", "index:4")]
    [InlineData("go to the 7th tab", "index:7")]
    [InlineData("switch to last tab", "from_end:1")]
    [InlineData("Switch to the last tab.", "from_end:1")]
    [InlineData("go to the second last tab", "from_end:2")]
    [InlineData("switch to second-to-last tab", "from_end:2")]
    [InlineData("go to the next tab", "next")]
    [InlineData("go to the previous tab", "previous")]
    public void Tab_selection_uses_exact_helper_tab_ids_instead_of_OS_keys(
        string request,
        string expectedSelection)
    {
        Assert.True(
            JarvisAutomationService.TryGetBrowserTabSelection(
                request,
                out var selection,
                out _));
        Assert.Equal(expectedSelection, selection);
        Assert.Null(JarvisAutomationService.BrowserShortcutForAction(request));
    }

    [Theory]
    [InlineData("Switch to the second-to-last tab.", true)]
    [InlineData("Please refresh.", true)]
    [InlineData("Write this text and then switch to the second tab.", false)]
    [InlineData("Open YouTube and use the third tab.", false)]
    public void Only_a_whole_direct_browser_command_uses_the_shortcut_pre_route(
        string request,
        bool expected)
    {
        Assert.Equal(
            expected,
            JarvisAutomationService.IsDirectBrowserShortcutRequest(request));
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
