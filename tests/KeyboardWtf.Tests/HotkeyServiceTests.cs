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
    [InlineData("Ctrl+Shift+F12", true)]
    [InlineData("Ctrl+Shift+F13", false)]
    [InlineData("Ctrl+Shift+F24", false)]
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

    [Fact]
    public void Legacy_inaccessible_function_hotkeys_are_migrated_to_human_defaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-hotkey-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """
                {
                  "Hotkeys": {
                    "PushToTalk": "Ctrl+Shift+F13",
                    "Dictation": "Ctrl+Shift+F14",
                    "Jarvis": "Ctrl+Shift+F15",
                    "Cancel": "Ctrl+Shift+F16",
                    "OpenSettings": "Ctrl+Shift+F17"
                  }
                }
                """);

            var service = new SettingsService(path);
            service.Load();

            var defaults = HotkeySettings.Defaults();
            Assert.Equal(defaults.PushToTalk, service.Current.Hotkeys.PushToTalk);
            Assert.Equal(defaults.Dictation, service.Current.Hotkeys.Dictation);
            Assert.Equal(defaults.Jarvis, service.Current.Hotkeys.Jarvis);
            Assert.Equal(defaults.Cancel, service.Current.Hotkeys.Cancel);
            Assert.Equal(defaults.OpenSettings, service.Current.Hotkeys.OpenSettings);
            Assert.DoesNotContain("F13", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Hotkey_change_registers_new_before_removing_old()
    {
        var platform = new FakeHotkeyPlatform();
        using var service = new HotkeyService(platform.Register, platform.Unregister);
        var defaults = HotkeySettings.Defaults().ToDictionary();
        Assert.True(service.TryReplaceForTests(defaults, null, true, out var initialError), initialError);

        var changed = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase)
        {
            ["Jarvis"] = "Ctrl+Shift+J",
        };
        Assert.True(service.TryReplaceForTests(changed, defaults, false, out var error), error);

        Assert.Equal("Ctrl+Shift+J", service.ActiveRegistrations["Jarvis"]);
        Assert.DoesNotContain("Ctrl+Alt+Q", service.ActiveRegistrations.Values, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(5, platform.Active.Count);
    }

    [Fact]
    public void Failed_multi_hotkey_change_rolls_back_staged_registrations()
    {
        var platform = new FakeHotkeyPlatform();
        using var service = new HotkeyService(platform.Register, platform.Unregister);
        var defaults = HotkeySettings.Defaults().ToDictionary();
        Assert.True(service.TryReplaceForTests(defaults, null, true, out var initialError), initialError);
        platform.Blocked.Add(FakeHotkeyPlatform.Parse("Ctrl+Shift+E"));

        var changed = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase)
        {
            ["PushToTalk"] = "Ctrl+Shift+W",
            ["Dictation"] = "Ctrl+Shift+E",
        };
        Assert.False(service.TryReplaceForTests(changed, defaults, false, out var error));

        Assert.Contains("already in use", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(defaults["PushToTalk"], service.ActiveRegistrations["SmartWriting"]);
        Assert.Equal(defaults["Dictation"], service.ActiveRegistrations["Dictation"]);
        Assert.DoesNotContain("Ctrl+Shift+W", service.ActiveRegistrations.Values, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(5, platform.Active.Count);
    }

    [Fact]
    public void Changing_one_binding_does_not_retry_an_unchanged_fallback_or_unavailable_default()
    {
        var platform = new FakeHotkeyPlatform();
        using var service = new HotkeyService(platform.Register, platform.Unregister);
        var configured = HotkeySettings.Defaults().ToDictionary();
        var active = new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase)
        {
            ["Jarvis"] = HotkeyService.JarvisFallback,
            ["Cancel"] = HotkeyService.CancelFallback,
        };
        Assert.True(service.TryReplaceForTests(active, null, true, out var initialError), initialError);
        platform.Blocked.Add(FakeHotkeyPlatform.Parse(configured["Jarvis"]));
        platform.Blocked.Add(FakeHotkeyPlatform.Parse(configured["Cancel"]));

        var changed = new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase)
        {
            ["Dictation"] = "Ctrl+Shift+E",
        };
        Assert.True(service.TryReplaceForTests(changed, configured, false, out var error), error);

        Assert.Equal("Ctrl+Shift+E", service.ActiveRegistrations["Dictation"]);
        Assert.Equal(HotkeyService.JarvisFallback, service.ActiveRegistrations["Jarvis"]);
        Assert.Equal(HotkeyService.CancelFallback, service.ActiveRegistrations["Cancel"]);
        Assert.Equal(5, platform.Active.Count);
    }

    [Fact]
    public void Strict_reset_keeps_existing_bindings_when_a_default_is_occupied()
    {
        var platform = new FakeHotkeyPlatform();
        using var service = new HotkeyService(platform.Register, platform.Unregister);
        var defaults = HotkeySettings.Defaults().ToDictionary();
        var active = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase)
        {
            ["Jarvis"] = "Ctrl+Shift+J",
        };
        Assert.True(service.TryReplaceForTests(active, null, true, out var initialError), initialError);
        platform.Blocked.Add(FakeHotkeyPlatform.Parse(defaults["Jarvis"]));

        Assert.False(service.TryReplaceForTests(defaults, active, true, out var error));
        Assert.Contains("already in use", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Ctrl+Shift+J", service.ActiveRegistrations["Jarvis"]);
        Assert.DoesNotContain(defaults["Jarvis"], service.ActiveRegistrations.Values, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(5, platform.Active.Count);
    }

    private sealed class FakeHotkeyPlatform
    {
        public Dictionary<int, (uint Modifiers, uint Key)> Active { get; } = [];
        public HashSet<(uint Modifiers, uint Key)> Blocked { get; } = [];

        public bool Register(int id, uint modifiers, uint key)
        {
            var combination = (modifiers, key);
            if (Blocked.Contains(combination) || Active.Values.Contains(combination))
                return false;
            Active[id] = combination;
            return true;
        }

        public bool Unregister(int id) => Active.Remove(id);

        public static (uint Modifiers, uint Key) Parse(string value)
        {
            Assert.True(HotkeyService.TryParse(value, out var modifiers, out var key));
            return (modifiers, (uint)key);
        }
    }
}
