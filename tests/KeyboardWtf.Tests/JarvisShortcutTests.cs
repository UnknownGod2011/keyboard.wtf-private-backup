namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

public sealed class JarvisShortcutTests
{
    [Fact]
    public void Successful_open_receipt_exposes_one_exact_referential_target()
    {
        var path = Path.Combine(Path.GetTempPath(), "Example Folder");
        var args = JsonSerializer.SerializeToElement(new { folder = "Example Folder" });
        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["path"] = path,
            ["message"] = "Opened Example Folder.",
        };

        JarvisAutomationService.AttachReferentialTarget("open_folder", args, result);

        var reference = Assert.IsType<Dictionary<string, object>>(result["referential_target"]);
        Assert.Equal("folder", reference["kind"]);
        Assert.Equal(path, reference["target"]);
        Assert.DoesNotContain("native_window_title", reference.Keys);
        Assert.Equal("one", reference["cardinality"]);
    }

    [Fact]
    public void Opened_file_receipt_uses_the_file_name_as_its_native_title()
    {
        var path = Path.Combine(Path.GetTempPath(), "Release Notes.txt");
        var args = JsonSerializer.SerializeToElement(new { path });
        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["path"] = path,
        };

        JarvisAutomationService.AttachReferentialTarget("open_path", args, result);

        var reference = Assert.IsType<Dictionary<string, object>>(result["referential_target"]);
        Assert.Equal("path", reference["kind"]);
        Assert.Equal(path, reference["target"]);
        Assert.Equal("Release Notes.txt", reference["native_window_title"]);
    }

    [Fact]
    public void Failed_or_non_open_receipt_cannot_become_a_referential_target()
    {
        var args = JsonSerializer.SerializeToElement(new { folder = "Missing" });
        var failed = new Dictionary<string, object> { ["ok"] = false };
        var unrelated = new Dictionary<string, object> { ["ok"] = true };

        JarvisAutomationService.AttachReferentialTarget("open_folder", args, failed);
        JarvisAutomationService.AttachReferentialTarget("system_control", args, unrelated);

        Assert.DoesNotContain("referential_target", failed.Keys);
        Assert.DoesNotContain("referential_target", unrelated.Keys);
    }

    [Fact]
    public void Open_path_folder_receipt_keeps_resolved_path_and_does_not_advertise_a_shared_title()
    {
        var path = Path.Combine(Path.GetTempPath(), "A", "notes");
        var result = new Dictionary<string, object> { ["ok"] = true, ["path"] = path, ["target_kind"] = "folder" };
        JarvisAutomationService.AttachReferentialTarget("open_path", JsonSerializer.SerializeToElement(new { path = "notes" }), result);
        var target = Assert.IsType<Dictionary<string, object>>(result["referential_target"]);
        Assert.Equal("folder", target["kind"]);
        Assert.Equal(path, target["target"]);
        Assert.DoesNotContain("native_window_title", target.Keys);
    }

    [Theory]
    [InlineData("ChatGPT", "ChatGPT", "https://chatgpt.com/", true)]
    [InlineData("Gmail", "Inbox - Gmail", "https://mail.google.com/", true)]
    [InlineData("ChatGPT", "Inbox - Gmail", "https://mail.google.com/", false)]
    [InlineData("https://example.com/portal", "Portal", "https://example.com/portal/home", true)]
    public void Named_browser_close_requires_a_verified_matching_tab(
        string target,
        string title,
        string url,
        bool expected) =>
        Assert.Equal(expected, JarvisAutomationService.BrowserTargetMatches(target, title, url));

    [Theory]
    [InlineData("Chrome", true)]
    [InlineData("the browser", true)]
    [InlineData("ChatGPT", false)]
    [InlineData("Discord", false)]
    public void Only_explicit_browser_names_authorize_closing_the_browser_window(
        string target,
        bool expected) =>
        Assert.Equal(expected, JarvisAutomationService.IsExplicitBrowserApplication(target));

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
    [InlineData("Go to the last Chrome tab.", "from_end:1")]
    [InlineData("go to the second last tab", "from_end:2")]
    [InlineData("go to the second last browser tab", "from_end:2")]
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
