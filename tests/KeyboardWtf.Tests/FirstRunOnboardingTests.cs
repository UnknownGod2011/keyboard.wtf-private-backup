using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class FirstRunOnboardingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"keyboard-wtf-first-run-{Guid.NewGuid():N}");

    [Fact]
    public void Repeated_startup_hotkey_failure_notifies_only_once()
    {
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(KeyboardWtfApp.MarkHotkeyFailureNotified(
            reported,
            "OpenSettings",
            "Ctrl+Alt+, is already in use by Windows or another app."));
        Assert.False(KeyboardWtfApp.MarkHotkeyFailureNotified(
            reported,
            "OpenSettings",
            "Ctrl+Alt+, is already in use by Windows or another app."));
        Assert.True(KeyboardWtfApp.MarkHotkeyFailureNotified(
            reported,
            "Jarvis",
            "Ctrl+Alt+Q is already in use by Windows or another app."));
    }

    [Fact]
    public void Fresh_settings_are_first_run_but_saved_settings_are_not()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        var first = new SettingsService(path);
        first.Load();

        Assert.True(first.IsFirstRun);
        Assert.True(File.Exists(path));

        var restarted = new SettingsService(path);
        restarted.Load();

        Assert.False(restarted.IsFirstRun);
    }

    [Theory]
    [InlineData("getting-started")]
    [InlineData("getting started")]
    [InlineData("onboarding")]
    public void Getting_started_route_is_canonical(string route) =>
        Assert.Equal("getting-started", SettingsSectionRegistry.Resolve(route));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
