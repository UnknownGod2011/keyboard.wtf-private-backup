using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserAutomationSettingsTests
{
    [Fact]
    public void New_install_is_disabled_with_memory_off()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"keyboard-wtf-browser-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(path);
            settings.Load();

            Assert.False(settings.Current.BrowserAutomationEnabled);
            Assert.False(settings.Current.BrowserAutomationMemoryEnabled);
            Assert.Equal(
                "gemini-3.1-flash-lite-preview",
                settings.Current.BrowserAutomationModel);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Browser_settings_persist()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"keyboard-wtf-browser-settings-{Guid.NewGuid():N}.json");
        try
        {
            var first = new SettingsService(path);
            first.Load();
            first.SaveBrowserAutomationEnabled(true);
            first.SaveBrowserAutomationModel("gemini-3-flash-preview");

            var second = new SettingsService(path);
            second.Load();
            Assert.True(second.Current.BrowserAutomationEnabled);
            Assert.Equal("gemini-3-flash-preview", second.Current.BrowserAutomationModel);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
