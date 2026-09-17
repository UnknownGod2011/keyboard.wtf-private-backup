using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class JarvisTranscriptOwnershipTests : IDisposable
{
    [Theory]
    [InlineData("Navigate to the How to Use Jarvis section.")]
    [InlineData("Please open the hotkeys section.")]
    public async Task Semantic_internal_settings_tool_retains_local_ownership(string text)
    {
        using var bridge = new BrowserHelperBridge();
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(BrowserHelperBridge).GetMethod("ProcessMessage", flags)!.Invoke(bridge,
        [JsonSerializer.Serialize(new { type = "hello", helper = new {
            extensionId = "gojdebdikepkpapmnggeljneamjdigbh",
            serviceWorkerVersion = "2.1.21", contentScriptVersion = "2.1.21",
            activeTabFresh = true, commandChannelReady = true, commandAuthoritySupported = true,
            activeUrl = "https://example.org/", sessionId = "test-session",
        } })]);
        var settings = new SettingsService(Path.Combine(_directory, "local-only.json"));
        settings.Current.BrowserAutomationEnabled = true;
        settings.Current.BrowserAutomationConsentVersion = SettingsService.CurrentBrowserAutomationConsentVersion;
        using var planner = new BrowserAutomationPlanner();
        using var coordinator = new BrowserAutomationCoordinator(settings, new NotificationService(null), bridge, planner);
        var commands = new CommandRegistry(null, null, null, settings, new NotificationService(null), browserAutomation: coordinator);
        commands.ObserveJarvisTranscript(text, CancellationToken.None);
        // Deliberately invalid section prevents a real Settings window opening
        // in the unit test. Even a local validation failure cannot authorize BA.
        var result = await commands.ExecuteJarvisToolAsync("open_keyboard_wtf_settings",
            JsonSerializer.SerializeToElement(new { section = "nonexistent-test-section" }), CancellationToken.None);
        Assert.False(coordinator.Snapshot().CanCancel);
        Assert.Contains("section does not exist", result["message"].ToString());
        var alternate = await commands.ExecuteJarvisToolAsync("browser_automation",
            JsonSerializer.SerializeToElement(new { request = text }), CancellationToken.None);
        Assert.Equal(true, alternate["duplicate_suppressed"]);
        Assert.False(coordinator.Snapshot().CanCancel);
    }

    [Theory]
    [InlineData("open_url", "Separately, open https://example.org in a new tab. Leave the form task paused.")]
    [InlineData("open_url", "Could you also open https://example.org? Keep the form task on hold.")]
    [InlineData("browser_automation_status", "The project reference is https://example.org; what is the form status?")]
    public async Task A_selected_non_guidance_tool_cannot_inject_the_turn_into_a_paused_browser_task(string tool, string text)
    {
        using var bridge = new BrowserHelperBridge();
        var settings = new SettingsService(Path.Combine(_directory, "unused-settings.json"));
        using var planner = new BrowserAutomationPlanner();
        using var coordinator = new BrowserAutomationCoordinator(settings, new NotificationService(null), bridge, planner);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var runtimeType = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", flags)!;
        var runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
        runtimeType.GetProperty("State")!.SetValue(runtime, BrowserTaskState.NeedsUserAction);
        runtimeType.GetProperty("Intervention")!.SetValue(runtime, new BrowserUserIntervention
        {
            Missing = "Provide the project reference for the notes field.", CanSkip = true,
        });
        runtimeType.GetProperty("Page")!.SetValue(runtime, new BrowserPageSnapshot
        {
            TabId = 12, Url = "https://example.test/form", Controls =
            [
                new BrowserControl { Role = "link", AccessibleName = "HTML Form Instructions", Visible = true, Enabled = true },
                new BrowserControl { Id = "notes", FieldId = "notes", FieldLabel = "Project reference", Role = "textbox",
                    Visible = true, Enabled = true, FieldRequired = false },
            ],
        });
        typeof(BrowserAutomationCoordinator).GetField("_active", flags)!.SetValue(coordinator, runtime);
        var commands = new CommandRegistry(null, null, null, settings, new NotificationService(null), browserAutomation: coordinator);
        commands.ObserveJarvisTranscript(text, CancellationToken.None);
        await commands.ExecuteJarvisToolAsync(tool, JsonSerializer.SerializeToElement(new { url = "https://example.org" }), CancellationToken.None);
        Assert.Equal(BrowserTaskState.NeedsUserAction, coordinator.Snapshot().State);
        Assert.Equal(0, runtimeType.GetField("GuidanceRevision")!.GetValue(runtime));
        Assert.NotNull(coordinator.Snapshot().Intervention);
        // No automation service is supplied: the direct tool cannot cause any
        // native effect in this ownership test. Real Helper execution is replayed separately.
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
    private readonly LearnedMappingService _mappings;
    private readonly JarvisActionHistoryService _history;
    private readonly JarvisAutomationService _automation;
    private readonly CommandRegistry _commands;
    private const string Request = "Always open https://example.org/workspace when I say open my workspace";

    public JarvisTranscriptOwnershipTests()
    {
        _mappings = new LearnedMappingService(Path.Combine(_directory, "mappings.json"));
        _history = new JarvisActionHistoryService(Path.Combine(_directory, "history.json"));
        var settings = new SettingsService();
        _automation = new JarvisAutomationService(null, settings, _history, _mappings, () => { });
        _commands = new CommandRegistry(null, null, null, settings, null, automation: _automation, history: _history);
    }

    [Fact]
    public void Observing_a_durable_request_does_not_save_or_act_before_a_model_tool_call()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        Assert.Empty(_mappings.Snapshot());
        Assert.False(File.Exists(_mappings.MappingPath));
        Assert.False(File.Exists(Path.Combine(_directory, "history.json")));
    }

    [Fact]
    public async Task The_authorized_tool_returns_the_single_verified_local_mapping_result()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        var first = await _commands.ExecuteJarvisToolAsync("remember_link_alias",
            JsonSerializer.SerializeToElement(new { alias = "open my workspace" }), CancellationToken.None);
        Assert.Equal(true, first["persistence_verified"]);
        Assert.Single(_mappings.Snapshot());
        Assert.Equal("https://example.org/workspace", _mappings.Snapshot()[0].Target);
        var repeated = await _commands.ExecuteJarvisToolAsync("remember_link_alias",
            JsonSerializer.SerializeToElement(new { alias = "open my workspace" }), CancellationToken.None);
        Assert.Equal(true, repeated["duplicate_suppressed"]);
        Assert.Equal("", repeated["message"]);
    }

    [Fact]
    public async Task Cancelled_tool_cannot_execute_the_observed_durable_request()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _commands.ExecuteJarvisToolAsync(
            "remember_link_alias", JsonSerializer.SerializeToElement(new { alias = "open my workspace" }), new CancellationToken(true)));
        Assert.Empty(_mappings.Snapshot());
    }

    [Fact]
    public async Task A_new_conversational_turn_cannot_execute_an_older_observed_request()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        _commands.ObserveJarvisTranscript("What mappings have I saved?", CancellationToken.None);
        await _commands.ExecuteJarvisToolAsync("list_learned_mappings", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Empty(_mappings.Snapshot());
    }

    [Fact]
    public async Task A_completed_non_action_response_cannot_be_executed_by_a_later_tool_without_input()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        _commands.CompleteJarvisUserTurn();
        await _commands.ExecuteJarvisToolAsync("list_learned_mappings", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Empty(_mappings.Snapshot());
    }

    [Theory]
    [InlineData("copy_text")]
    [InlineData("replace_selected_text")]
    public async Task Current_selected_context_rejects_an_instruction_echo_before_output_execution(string toolName)
    {
        const string request =
            "I've selected some text. I need you to polish the English and copy the revised version to my clipboard, keeping the original meaning intact.";
        const string selection =
            "Yesterday the engineering team review the incident and they decides that the cache policy need clearer ownership. The change reduce duplicate work while keeping every customer-visible behavior the same.";
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);

        typeof(CommandRegistry).GetMethod(
                "RecordCapturedBrowserContext",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_commands,
            [
                "get_selected_text",
                new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["text"] = selection,
                    ["fresh"] = true,
                },
            ]);

        var result = await _commands.ExecuteJarvisToolAsync(
            toolName,
            JsonSerializer.SerializeToElement(new { text = request }),
            CancellationToken.None);

        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["clipboard_unchanged"]);
        Assert.Equal(true, result["selection_unchanged"]);
        Assert.Equal(true, result["selected_context_result_required"]);
        Assert.Contains("selected content", result["message"].ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _automation.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void Link_follow_up_context_uses_the_verified_destination_not_the_model_alias()
    {
        var result = new Dictionary<string, object> { ["ok"] = true, ["url"] = "https://example.org/actual" };
        JarvisAutomationService.AttachReferentialTarget("open_url",
            JsonSerializer.SerializeToElement(new { url = "my workspace" }), result);
        var target = Assert.IsType<Dictionary<string, object>>(result["referential_target"]);
        Assert.Equal("https://example.org/actual", target["target"]);
    }

    [Fact]
    public async Task An_owned_result_cannot_expire_and_authorize_an_alternate_tool_in_the_same_request()
    {
        _commands.ObserveJarvisTranscript(Request, CancellationToken.None);
        await _commands.ExecuteJarvisToolAsync("remember_link_alias",
            JsonSerializer.SerializeToElement(new { alias = "open my workspace" }), CancellationToken.None);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var receipt = typeof(CommandRegistry).GetField("_browserPreRouteReceipt", flags)!.GetValue(_commands)!;
        receipt.GetType().GetField("<CreatedUtc>k__BackingField", flags)!.SetValue(receipt, DateTime.UtcNow.AddMinutes(-2));
        var late = await _commands.ExecuteJarvisToolAsync("unavailable_tool", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.Equal(true, late["duplicate_suppressed"]);
        Assert.Equal("", late["message"]);
        _commands.ObserveJarvisTranscript("What mappings have I saved?", CancellationToken.None);
        var fresh = await _commands.ExecuteJarvisToolAsync("list_learned_mappings", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        Assert.False(fresh.ContainsKey("duplicate_suppressed"));
    }
}
