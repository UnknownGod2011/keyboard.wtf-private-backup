using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class DirectTabContextTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
    private readonly BrowserHelperBridge _bridge = new();
    private readonly SettingsService _settings = new();
    private readonly LearnedMappingService _mappings;
    private readonly JarvisAutomationService _automation;

    public DirectTabContextTests()
    {
        _settings.Current.PreferredBrowser = "chrome";
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        _mappings = new(Path.Combine(_directory, "mappings.json"));
        _automation = new(null, _settings, new(Path.Combine(_directory, "history.json")),
            _mappings, () => { }, null, null, _bridge);
        Process("hello");
    }

    [Fact]
    public async Task Link_resolved_through_app_or_path_keeps_actual_tab_identity_not_an_app_alias()
    {
        _mappings.Remember(LearnedMappingKind.Link, "my research", "https://example.org/research");
        foreach (var tool in new[] { "open_app", "open_path" })
        {
            var opening = Execute(tool, new { app_name = "my research", path = "my research" });
            var command = await NextCommand(opening);
            Assert.Equal("https://example.org/research", command.GetProperty("url").GetString());
            Reply(command, 11);
            var target = JsonSerializer.SerializeToElement(await opening).GetProperty("referential_target");
            Assert.Equal("browser_tab", target.GetProperty("kind").GetString());
            Assert.Equal("https://example.org/research", target.GetProperty("target").GetString());
        }
    }

    [Fact]
    public async Task Direct_search_returns_the_actual_opened_tab_without_creating_a_Browser_task()
    {
        var opening = Execute("web_search", new { query = "public documentation", engine = "google", browser = "chrome" });
        var command = await NextCommand(opening);
        Assert.Equal("open_direct_tab", command.GetProperty("type").GetString());
        Assert.Contains("public%20documentation", command.GetProperty("url").GetString());
        Reply(command, 11);
        Assert.Equal("browser_tab", JsonSerializer.SerializeToElement(await opening).GetProperty("referential_target").GetProperty("kind").GetString());
        AssertNoCommand();
    }

    [Fact]
    public async Task Multi_target_close_uses_only_receipt_handles_and_accounts_for_manual_closure()
    {
        var first = await Open(11);
        var second = await Open(12);
        var closing = Execute("browser_action", new { action = "close tabs", target_ids = new[] { first, second, first } });
        var command = await NextCommand(closing);
        Assert.Equal(11, command.GetProperty("tabId").GetInt32());
        Assert.Equal(7, command.GetProperty("windowId").GetInt32());
        Assert.Equal("test-session", command.GetProperty("expectedSessionId").GetString());
        Reply(command, 11);
        command = await NextCommand(closing);
        Assert.Equal(12, command.GetProperty("tabId").GetInt32());
        Reply(command, 12, alreadyClosed: true);
        var result = await closing;
        Assert.Equal(true, result["ok"]);
        Assert.Equal(1, result["closed_count"]);
        Assert.Equal(1, result["already_closed_count"]);
        Assert.Equal(2, result["target_count"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Mixed_known_unknown_references_reject_the_entire_set_before_any_mutation()
    {
        var known = await Open(11);
        var result = await Execute("browser_action", new { action = "close tabs", target_ids = new[] { known, "invented" } });
        Assert.Equal(false, result["ok"]);
        Assert.Equal(true, result["needs_clarification"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Wrong_close_receipt_cannot_count_as_success_or_consume_the_target()
    {
        var known = await Open(11);
        var closing = Execute("browser_action", new { action = "close tabs", target_ids = new[] { known } });
        Reply(await NextCommand(closing), 99);
        var result = await closing;
        Assert.Equal(false, result["ok"]);
        Assert.Equal(0, result["closed_count"]);
        var retry = Execute("browser_action", new { action = "close tabs", target_ids = new[] { known } });
        Reply(await NextCommand(retry), 11);
        Assert.Equal(true, (await retry)["ok"]);
    }

    [Fact]
    public async Task Conversation_reset_invalidates_handles_without_targeting_another_tab()
    {
        var known = await Open(11);
        _automation.ResetConversationTransientContext();
        Assert.Equal(false, (await Execute("browser_action", new { action = "close tabs", target_ids = new[] { known } }))["ok"]);
        AssertNoCommand();
    }

    [Fact]
    public async Task Reset_during_open_does_not_repopulate_new_conversation_context()
    {
        var opening = Execute("open_url", new { url = "https://example.org/one", browser = "chrome" });
        var command = await NextCommand(opening);
        _automation.ResetConversationTransientContext();
        Reply(command, 11);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        AssertNoCommand();
    }

    [Fact]
    public async Task Cancellation_revokes_close_waiter_and_never_dispatches_the_rest_of_the_set()
    {
        var first = await Open(11);
        var second = await Open(12);
        using var cancel = new CancellationTokenSource();
        var closing = Execute("browser_action", new { action = "close tabs", target_ids = new[] { first, second } }, cancel.Token);
        var command = await NextCommand(closing);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closing);
        Assert.False(Process("command_check", command).GetProperty("commandAuthorized").GetBoolean());
        Reply(command, 11);
        AssertNoCommand();
    }

    [Fact]
    public async Task Workflow_keeps_successful_handles_and_partial_failures_in_one_result()
    {
        _settings.Current.JarvisWorkflows = new() { new()
        {
            Name = "test workspace", Actions = new()
            {
                new() { Kind = "url", Target = "https://example.org/one" },
                new() { Kind = "url", Target = "https://example.org/two" },
                new() { Kind = "url", Target = "https://example.org/three" },
            },
        } };
        var workflow = Execute("run_workflow", new { name = "test workspace" });
        Reply(await NextCommand(workflow), 11);
        var failed = await NextCommand(workflow);
        Process("command_result", failed, new { ok = false, error = "No second tab was created." });
        Reply(await NextCommand(workflow), 13);
        var result = JsonSerializer.SerializeToElement(await workflow);
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal(3, result.GetProperty("action_results").GetArrayLength());
        var targets = result.GetProperty("referential_targets");
        Assert.Equal(2, targets.GetArrayLength());
        Assert.Equal("https://example.org/one", targets[0].GetProperty("target").GetString());
        Assert.Equal("https://example.org/three", targets[1].GetProperty("target").GetString());
        Assert.NotEmpty(targets[0].GetProperty("target_id").GetString());
        AssertNoCommand();
    }

    [Theory]
    [InlineData("close tabs", "[]")]
    [InlineData("close tabs", "[null]")]
    [InlineData("close tabs", "[11]")]
    [InlineData("first tab", "[\"invented\"]")]
    public async Task Invalid_target_command_fails_closed(string action, string ids)
    {
        using var document = JsonDocument.Parse(ids);
        var result = await Execute("browser_action", new { action, target_ids = document.RootElement });
        Assert.Equal(false, result["ok"]);
        AssertNoCommand();
    }

    [Theory]
    [InlineData("open_app")]
    [InlineData("open_path")]
    [InlineData("open_url")]
    public void Shell_link_result_never_misrepresents_an_alias_as_a_native_app_or_exact_tab(string tool)
    {
        var result = new Dictionary<string, object> { ["ok"] = true, ["url"] = "https://example.org/actual" };
        JarvisAutomationService.AttachReferentialTarget(tool, JsonSerializer.SerializeToElement(new { app_name = "my research", path = "my research", url = "my research" }), result);
        var target = JsonSerializer.SerializeToElement(result).GetProperty("referential_target");
        Assert.Equal("link", target.GetProperty("kind").GetString());
        Assert.Equal("https://example.org/actual", target.GetProperty("target").GetString());
        Assert.False(target.TryGetProperty("target_id", out _));
        Assert.Contains("no exact tab handle", target.GetProperty("instruction").GetString());
    }

    [Theory]
    [InlineData("ChromeHTML", true)]
    [InlineData("ChromeHTML.random", true)]
    [InlineData("MSEdgeHTM", false)]
    [InlineData("FirefoxURL", false)]
    [InlineData(null, false)]
    public void Only_actual_Chrome_association_allows_Helper_substitution(string progId, bool expected) =>
        Assert.Equal(expected, BrowserLauncherService.IsChromeAssociation(progId));

    private Task<Dictionary<string, object>> Execute(string tool, object args, CancellationToken token = default) =>
        _automation.ExecuteAsync(tool, JsonSerializer.SerializeToElement(args), token);

    private async Task<string> Open(int tab)
    {
        var opening = Execute("open_url", new { url = $"https://example.org/{tab}", browser = "chrome" });
        var command = await NextCommand(opening);
        Assert.Equal("open_direct_tab", command.GetProperty("type").GetString());
        Reply(command, tab);
        var result = JsonSerializer.SerializeToElement(await opening);
        Assert.Equal("browser_tab", result.GetProperty("referential_target").GetProperty("kind").GetString());
        return result.GetProperty("referential_target").GetProperty("target_id").GetString()!;
    }

    private async Task<JsonElement> NextCommand(Task operation)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var command = Process("poll").GetProperty("command");
            if (command.ValueKind != JsonValueKind.Null) return command;
            Assert.False(operation.IsCompleted, "Operation ended before the expected Helper command.");
            await Task.Delay(10);
        }
        throw new TimeoutException("Expected Helper command did not arrive.");
    }

    private void Reply(JsonElement command, int tab, bool alreadyClosed = false) =>
        Process("command_result", command, new { ok = true, tabId = tab, windowId = 7, sessionId = "test-session", alreadyClosed });

    private void AssertNoCommand() => Assert.Equal(JsonValueKind.Null, Process("poll").GetProperty("command").ValueKind);

    private JsonElement Process(string type, JsonElement command = default, object result = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            type, result, taskId = command.ValueKind == JsonValueKind.Object ? command.GetProperty("taskId").GetString() : "",
            commandId = command.ValueKind == JsonValueKind.Object ? command.GetProperty("commandId").GetString() : "",
            helper = new
            {
                extensionId = "gojdebdikepkpapmnggeljneamjdigbh", serviceWorkerVersion = "2.1.21", contentScriptVersion = "2.1.21",
                activeTabFresh = true, commandChannelReady = true, commandAuthoritySupported = true,
                activeUrl = "https://example.org/", sessionId = "test-session",
            },
        });
        var method = typeof(BrowserHelperBridge).GetMethod("ProcessMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return JsonDocument.Parse((string)method.Invoke(_bridge, new object[] { json })!).RootElement.Clone();
    }

    public void Dispose()
    {
        _automation.Dispose();
        _bridge.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
