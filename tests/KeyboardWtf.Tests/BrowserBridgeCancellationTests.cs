using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserBridgeCancellationTests
{
    [Fact]
    public async Task Legacy_helper_fails_before_queuing_a_mutation_with_an_actionable_update_message()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello", supportsAuthority: false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.ExecuteAsync(
            new BrowserActionRequest { TaskId = "task", Type = "click" }, CancellationToken.None));
        Assert.Contains("Update the Chrome Helper", error.Message);
        Assert.Equal(JsonValueKind.Null, Process(bridge, "poll", supportsAuthority: false).GetProperty("command").ValueKind);
    }
    [Fact]
    public async Task Cancelled_queued_action_is_never_returned_to_the_helper_after_it_recovers()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        using var cancellation = new CancellationTokenSource();
        var action = bridge.ExecuteAsync(new BrowserActionRequest
        {
            TaskId = "old-task", Type = "click", ControlId = "old-field",
        }, cancellation.Token);
        Assert.False(action.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action);
        Assert.Equal(JsonValueKind.Null, Process(bridge, "poll").GetProperty("command").ValueKind);
    }

    [Fact]
    public async Task Dispatched_command_loses_mutation_authority_when_cancelled()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        using var cancellation = new CancellationTokenSource();
        var action = new BrowserActionRequest { TaskId = "task", Type = "click", ControlId = "field" };
        var pending = bridge.ExecuteAsync(action, cancellation.Token);
        var returned = Process(bridge, "poll").GetProperty("command");
        Assert.True(returned.GetProperty("requiresAuthorityCheck").GetBoolean());
        Assert.True(Process(bridge, "command_check", action).GetProperty("commandAuthorized").GetBoolean());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(Process(bridge, "command_check", action).GetProperty("commandAuthorized").GetBoolean());
    }

    [Fact]
    public async Task Completed_command_cannot_regain_authority_and_heartbeat_does_not_take_next_action()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        var action = new BrowserActionRequest { TaskId = "task", Type = "observe" };
        var pending = bridge.ExecuteAsync(action, CancellationToken.None);
        Assert.Equal(JsonValueKind.Null, Process(bridge, "heartbeat").GetProperty("command").ValueKind);
        Assert.Equal(action.CommandId, Process(bridge, "poll").GetProperty("command").GetProperty("commandId").GetString());
        Process(bridge, "command_result", action, new { ok = true });
        await pending;
        Assert.False(Process(bridge, "command_check", action).GetProperty("commandAuthorized").GetBoolean());
    }

    [Fact]
    public async Task Discarding_old_work_keeps_the_next_live_request_and_its_result()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        using var cancellation = new CancellationTokenSource();
        var old = bridge.ExecuteAsync(new BrowserActionRequest { TaskId = "old", Type = "click" }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        var fresh = new BrowserActionRequest { TaskId = "new", Type = "observe" };
        var result = bridge.ExecuteAsync(fresh, CancellationToken.None);
        var returned = Process(bridge, "poll").GetProperty("command");
        Assert.Equal(fresh.CommandId, returned.GetProperty("commandId").GetString());
        Process(bridge, "command_result", fresh, new { ok = true });
        Assert.True((await result).Ok);
        Assert.Equal(JsonValueKind.Null, Process(bridge, "poll").GetProperty("command").ValueKind);
    }

    private static JsonElement Process(BrowserHelperBridge bridge, string type,
        BrowserActionRequest action = null, object result = null, bool supportsAuthority = true)
    {
        var json = JsonSerializer.Serialize(new
        {
            type, taskId = action?.TaskId, commandId = action?.CommandId, result,
            helper = new
            {
                extensionId = "gojdebdikepkpapmnggeljneamjdigbh",
                serviceWorkerVersion = "2.1.21", contentScriptVersion = "2.1.21",
                activeTabFresh = true, commandChannelReady = true,
                commandAuthoritySupported = supportsAuthority,
                activeUrl = "https://example.org/", sessionId = "test-session",
            },
        });
        var method = typeof(BrowserHelperBridge).GetMethod("ProcessMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return JsonDocument.Parse((string)method.Invoke(bridge, new object[] { json })!).RootElement.Clone();
    }
}
