namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

public sealed class FinalPolishRegressionTests : IDisposable
{
    [Fact]
    public void Account_usable_now_excludes_reserved_credits_and_discloses_the_hold()
    {
        var html = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Resources", "web", "settings.html"));
        var usage = html[html.IndexOf("document.getElementById('paidUsage').textContent", StringComparison.Ordinal)..];
        usage = usage[..usage.IndexOf("const signInMessage", StringComparison.Ordinal)];
        Assert.Contains("formatPaidCredits(available)", usage);
        Assert.DoesNotContain("formatPaidCredits(totalUsable)", usage);
        Assert.Contains("reserved pending settlement", usage);
    }

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
    public void Settings_sidebar_uses_the_same_canonical_order_as_rendered_content()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));
        var nav = System.Text.RegularExpressions.Regex.Match(
            html,
            "(?s)<nav id=\"sidebar-nav\">(.*?)</nav>").Groups[1].Value;
        var navIds = System.Text.RegularExpressions.Regex.Matches(nav, "href=\"#([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        var sectionIds = System.Text.RegularExpressions.Regex.Matches(html, "<section id=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(sectionIds, navIds);
        Assert.True(Array.IndexOf(navIds, "voice-capture") < Array.IndexOf(navIds, "account-billing"));
        Assert.Contains("const sectionOrder = new Map(Array.from(sectionById.keys())", html, StringComparison.Ordinal);
        Assert.Contains("navLinks.forEach(link => sidebarNav.appendChild(link));", html, StringComparison.Ordinal);
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

    [Fact]
    public void Refresh_account_preserves_the_actual_settings_scroll_container()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));

        Assert.Contains("id=\"main-scroll\"", html, StringComparison.Ordinal);
        Assert.Contains("document.getElementById('main-scroll')", html, StringComparison.Ordinal);
        Assert.Contains("settingsScroller.scrollTop = refreshScrollTop;", html, StringComparison.Ordinal);
        Assert.Contains("window.refreshSettingsScrollSpy?.();", html, StringComparison.Ordinal);
        Assert.DoesNotContain("document.querySelector('main')", html, StringComparison.Ordinal);
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
    [InlineData("Open Account & Billing in keyboard.wtf Settings.", "account-billing")]
    [InlineData("Show me your hotkey settings.", "hotkeys")]
    [InlineData("Open Jarvis assistant settings.", "assistant")]
    [InlineData("Open keyboard.wtf settings.", "")]
    [InlineData("Open keyboard.wtf Settings, Account and Billing. Do not use Browser Automation.", "account-billing")]
    [InlineData("Show me your hotkey settings; leave text to speech unchanged.", "hotkeys")]
    [InlineData("Open keyboard.wtf voice notes settings.", "voice-notes")]
    public void Explicit_keyboard_wtf_settings_requests_resolve_to_one_local_route(
        string request,
        string expectedSection)
    {
        Assert.True(SettingsSectionRegistry.TryResolveKeyboardWtfNavigationRequest(
            request,
            out var section));
        Assert.Equal(expectedSection, section);
    }

    [Theory]
    [InlineData("Open settings.")]
    [InlineData("Open Windows Settings.")]
    [InlineData("What do you think about the keyboard.wtf settings design?")]
    public void Non_local_or_conversational_settings_mentions_do_not_claim_the_privileged_route(
        string request) =>
        Assert.False(SettingsSectionRegistry.TryResolveKeyboardWtfNavigationRequest(
            request,
            out _));

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
    [InlineData("bye", true)]
    [InlineData("good bye", true)]
    [InlineData("Good-bye\u2026", true)]
    [InlineData("go away", true)]
    [InlineData("stop Jarvis", true)]
    [InlineData("end chat", true)]
    [InlineData("fuck off", true)]
    public void Only_explicit_conversation_dismissal_ends_Jarvis(
        string transcript,
        bool expected) =>
        Assert.Equal(expected, GeminiLiveConversationService.ShouldAutoEnd(transcript));

    [Fact]
    public void Fresh_goodbye_fragment_is_detected_even_if_the_accumulated_turn_is_saturated()
    {
        var saturatedTurn = new string('x', 4096);

        Assert.True(GeminiLiveConversationService.ShouldAutoEnd("goodbye", saturatedTurn));
        Assert.True(GeminiLiveConversationService.ShouldAutoEnd("go away", saturatedTurn));
        Assert.False(GeminiLiveConversationService.ShouldAutoEnd("close tab", saturatedTurn));
    }

    [Theory]
    [InlineData("interimInputTranscription")]
    [InlineData("inputTranscription")]
    public void Farewell_is_recognized_from_the_earliest_available_input_transcript(
        string transcriptionProperty)
    {
        using var document = JsonDocument.Parse(
            "{\"" + transcriptionProperty + "\":{\"text\":\"Okay, goodbye.\"}}");

        Assert.True(GeminiLiveConversationService.TryGetFarewellTranscription(
            document.RootElement,
            out var kind));
        Assert.Equal(
            transcriptionProperty == "interimInputTranscription" ? "interim" : "final",
            kind);
    }

    [Fact]
    public void Normal_browser_close_is_not_a_farewell_in_an_interim_transcript()
    {
        using var document = JsonDocument.Parse(
            """{"interimInputTranscription":{"text":"close the Chrome tab"}}""");

        Assert.False(GeminiLiveConversationService.TryGetFarewellTranscription(
            document.RootElement,
            out _));
    }

    [Fact]
    public void Farewell_path_is_local_and_has_no_deferred_provider_turn_delay()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "GeminiLiveConversationService.cs"));

        var farewell = source.IndexOf(
            "if (ShouldAutoEnd(text, accumulatedUserTurn))",
            StringComparison.Ordinal);
        var observer = source.IndexOf(
            "GetToolDispatcher(diagnostics, token).Observe(_userTranscriptObserver, accumulatedUserTurn);",
            StringComparison.Ordinal);
        Assert.True(farewell >= 0);
        Assert.True(observer > farewell);
        Assert.Contains("StopMicrophone();", source, StringComparison.Ordinal);
        Assert.Contains("StopPlayback();", source, StringComparison.Ordinal);
        Assert.Contains("await StopAsync(showDone: false);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await Task.Delay(850);", source, StringComparison.Ordinal);
    }

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
    public void Live_prompt_keeps_transient_actions_separate_from_explicit_persistent_memory()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Services",
            "GeminiLiveConversationService.cs"));

        Assert.Contains("Ordinary one-time commands", source, StringComparison.Ordinal);
        Assert.Contains("Never call remember_routine", source, StringComparison.Ordinal);
        Assert.Contains("explicitly asks to remember or save a reusable trigger", source,
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
