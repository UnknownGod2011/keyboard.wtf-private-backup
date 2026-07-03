namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class HotkeyServiceTests
{
    [Fact]
    public void ConflictFallbacksDoNotUseReservedCursivisShortcut()
    {
        Assert.Equal("Ctrl+Alt+J", HotkeyService.JarvisDefault);
        Assert.Equal("Ctrl+Shift+J", HotkeyService.JarvisFallback);
        Assert.Equal("Ctrl+Alt+Escape", HotkeyService.CancelFallback);
        Assert.DoesNotContain("Space", HotkeyService.JarvisDefault, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Space", HotkeyService.JarvisFallback, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Space", HotkeyService.CancelFallback, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultJarvisShortcutIsCtrlAltJ()
    {
        Assert.Equal("Ctrl+Alt+J", HotkeySettings.Defaults().Jarvis);
    }

    [Fact]
    public void LegacyDefaultQIsMigratedAndPersisted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-hotkey-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(path, """{"Hotkeys":{"Jarvis":"Ctrl+Alt+Q"}}""");
            var settings = new SettingsService(path);

            settings.Load();

            Assert.Equal("Ctrl+Alt+J", settings.Current.Hotkeys.Jarvis);
            Assert.True(File.Exists(path + ".pre-v2.bak"));
            var reloaded = new SettingsService(path);
            reloaded.Load();
            Assert.Equal("Ctrl+Alt+J", reloaded.Current.Hotkeys.Jarvis);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [Fact]
    public void CurrentSchemaPreservesUserCustomizedJarvisShortcut()
    {
        var directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-hotkey-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                path,
                $"{{\"SettingsSchemaVersion\":{SettingsService.CurrentSchemaVersion},\"Hotkeys\":{{\"Jarvis\":\"Ctrl+Alt+Q\"}}}}");
            var settings = new SettingsService(path);

            settings.Load();

            Assert.Equal("Ctrl+Alt+Q", settings.Current.Hotkeys.Jarvis);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [Fact]
    public void StartupPathUsesTheExecutableThatIsActuallyRunning()
    {
        var localBuild = Path.Combine(
            Path.GetTempPath(),
            "keyboard.wtf",
            "bin",
            "Release",
            "keyboard.wtf.exe");

        var result = KeyboardWtfApp.ResolveStartupExecutablePath(localBuild);

        Assert.Equal(Path.GetFullPath(localBuild), result);
        Assert.DoesNotContain(
            Path.Combine("Programs", "keyboard.wtf"),
            result,
            StringComparison.OrdinalIgnoreCase);
    }
}
