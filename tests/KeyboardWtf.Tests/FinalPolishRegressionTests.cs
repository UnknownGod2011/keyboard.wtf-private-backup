namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class FinalPolishRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-final-polish-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("browser-automation", "browser-automation")]
    [InlineData("#how-to-use-jarvis", "how-to-use")]
    [InlineData("keyboard.wtf text to speech settings", "tts")]
    [InlineData("what's next", "roadmap")]
    [InlineData("account", "account-billing")]
    public void Settings_routes_use_one_canonical_section_registry(
        string requested,
        string expected) =>
        Assert.Equal(expected, SettingsSectionRegistry.Resolve(requested));

    [Fact]
    public void Every_canonical_settings_route_exists_in_the_packaged_page()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));
        foreach (var id in SettingsSectionRegistry.CanonicalIds)
            Assert.Contains($"<section id=\"{id}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Initial_settings_deep_link_remains_authoritative_while_account_layout_loads()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));

        Assert.Contains("let initialRoute = resolveRoute(location.hash);", html, StringComparison.Ordinal);
        Assert.Contains("if (initialRoute)", html, StringComparison.Ordinal);
        Assert.Contains("window.finishInitialSettingsRoute", html, StringComparison.Ordinal);
        Assert.Contains("sectionById.forEach(section => layoutObserver.observe(section));", html,
            StringComparison.Ordinal);
        Assert.Contains("loadSettings().finally(() => window.finishInitialSettingsRoute?.());", html,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("settings", true, false)]
    [InlineData("Windows Settings", true, false)]
    [InlineData("keyboard.wtf settings", false, true)]
    [InlineData("Jarvis settings", false, true)]
    [InlineData("your settings", false, true)]
    public void Settings_intent_keeps_windows_and_keyboard_wtf_targets_distinct(
        string request,
        bool windows,
        bool keyboardWtf)
    {
        Assert.Equal(windows, JarvisAutomationService.IsWindowsSettingsTarget(request));
        Assert.Equal(keyboardWtf, JarvisAutomationService.IsKeyboardWtfSettingsTarget(request));
    }

    [Theory]
    [InlineData("native", true)]
    [InlineData("app", true)]
    [InlineData("application", true)]
    [InlineData("auto", false)]
    [InlineData("website", false)]
    public void Explicit_app_intent_can_require_a_real_native_application(
        string targetKind,
        bool expected) =>
        Assert.Equal(expected, JarvisAutomationService.IsExplicitNativeTarget(targetKind));

    [Theory]
    [InlineData("ChatGPT app", "ChatGPT")]
    [InlineData("launch Visual Studio Code application", "Visual Studio Code")]
    [InlineData("start Discord native app", "Discord")]
    [InlineData("Calculator", "Calculator")]
    public void Explicit_native_app_wording_is_removed_before_installed_app_resolution(
        string request,
        string expected) =>
        Assert.Equal(expected, JarvisAutomationService.NormalizeNativeAppQuery(request));

    [Theory]
    [InlineData("close tab", false)]
    [InlineData("close Chrome", false)]
    [InlineData("thanks", false)]
    [InlineData("thank you", false)]
    [InlineData("goodbye", true)]
    [InlineData("stop Jarvis", true)]
    [InlineData("end chat", true)]
    [InlineData("fuck off", true)]
    public void Only_explicit_conversation_dismissal_ends_Jarvis(
        string transcript,
        bool expected) =>
        Assert.Equal(expected, GeminiLiveConversationService.ShouldAutoEnd(transcript));

    [Fact]
    public void Live_prompt_routes_natural_current_screen_questions_to_fresh_vision()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "GeminiLiveConversationService.cs"));

        Assert.Contains("what the user is looking at", source, StringComparison.Ordinal);
        Assert.Contains("must call inspect_screen", source, StringComparison.Ordinal);
        Assert.Contains("Every new explicit current-screen request captures a fresh image", source,
            StringComparison.Ordinal);
        Assert.Contains("the foreground app name alone is not screen analysis", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Deleted_workflow_tombstone_survives_restart_and_is_cleared_by_explicit_resave()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        var settings = new SettingsService(path);
        settings.Load();
        settings.SaveWorkflow("initialize my setup", "Notepad", "", "");

        Assert.True(settings.DeleteWorkflow("initialize my setup"));
        Assert.True(settings.IsWorkflowDeleted("initialize my setup"));

        var reloaded = new SettingsService(path);
        reloaded.Load();
        Assert.True(reloaded.IsWorkflowDeleted("initialize my setup"));
        Assert.Empty(reloaded.Current.JarvisWorkflows);

        reloaded.SaveWorkflow("initialize my setup", "Calculator", "", "");
        Assert.False(reloaded.IsWorkflowDeleted("initialize my setup"));
        Assert.Equal("Calculator", Assert.Single(reloaded.Current.JarvisWorkflows).Apps);
    }

    [Fact]
    public void Deleting_a_workflow_removes_aliases_and_targets_from_learned_mappings()
    {
        Directory.CreateDirectory(_root);
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(
            LearnedMappingKind.Workflow,
            "start my day",
            "initialize my setup",
            "Initialize my setup");

        Assert.Equal(1, mappings.ForgetWorkflowReferences("initialize my setup"));
        Assert.Empty(mappings.Snapshot());
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(91, false)]
    public void Cached_selection_is_only_safe_in_the_same_recent_window(int ageSeconds, bool expected)
    {
        var context = new SelectionContext((IntPtr)10, "notepad", "Draft - Notepad");
        var snapshot = new SelectionSnapshot(
            "current selection",
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(ageSeconds),
            context.Handle,
            context.ProcessName,
            context.WindowTitle);

        Assert.Equal(expected, JarvisAutomationService.CanUseCachedSelection(
            snapshot,
            context,
            DateTimeOffset.UtcNow));
        Assert.False(JarvisAutomationService.CanUseCachedSelection(
            snapshot,
            new SelectionContext((IntPtr)11, "chrome", "Unrelated tab"),
            DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch { }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
