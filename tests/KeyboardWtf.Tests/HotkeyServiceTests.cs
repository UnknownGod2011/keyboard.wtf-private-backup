namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class HotkeyServiceTests
{
    [Fact]
    public void ConflictFallbacksDoNotUseReservedCursivisShortcut()
    {
        Assert.Equal("Ctrl+Alt+J", HotkeyService.JarvisFallback);
        Assert.Equal("Ctrl+Alt+Escape", HotkeyService.CancelFallback);
        Assert.DoesNotContain("Space", HotkeyService.JarvisFallback, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Space", HotkeyService.CancelFallback, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Ctrl+Alt+K", true)]
    [InlineData("Ctrl+Alt+,", true)]
    [InlineData("Ctrl+Alt", false)]
    [InlineData("Ctrl+Alt+K+L", false)]
    [InlineData("K", false)]
    [InlineData("Alt+F4", false)]
    [InlineData("Win+L", false)]
    [InlineData("Ctrl+Shift+Escape", false)]
    [InlineData("Ctrl+Alt+NotAKey", false)]
    public void Hotkey_parser_rejects_ambiguous_or_reserved_combinations(string value, bool expected)
    {
        Assert.Equal(expected, HotkeyService.TryParse(value, out _, out _));
    }

    [Fact]
    public void Duplicate_hotkeys_are_rejected_before_registration()
    {
        var settings = HotkeySettings.Defaults();
        settings.Cancel = settings.Jarvis;
        Assert.True(HotkeyService.HasDuplicates(settings, out var duplicate));
        Assert.Equal(settings.Jarvis, duplicate, ignoreCase: true);
    }
}
