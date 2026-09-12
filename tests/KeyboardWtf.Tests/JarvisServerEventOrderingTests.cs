using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class JarvisServerEventOrderingTests
{
    [Theory]
    [InlineData("goodbye")]
    [InlineData("go away")]
    [InlineData("fuck off")]
    [InlineData("end the conversation")]
    [InlineData("bye Jarvis")]
    [InlineData("goodbye Jarvis")]
    [InlineData("that's all, goodbye")]
    [InlineData("thanks, goodbye")]
    public async Task Recognized_terminal_input_is_not_queued_behind_an_inflight_tool(string farewell)
    {
        await using var run = new MessageRun();
        var reader = run.ReadAsync(
            ToolCall("action-1"),
            JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text = farewell } } }));

        try
        {
            await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await reader.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(run.LocalTerminationRequested() || run.Ended.Task.IsCompletedSuccessfully);
            await run.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(run.Session.IsCancellationRequested);
        }
        finally
        {
            run.Release();
            await IgnoreCancelledAsync(reader);
        }
    }

    [Theory]
    [InlineData("inputTranscription")]
    [InlineData("interimInputTranscription")]
    public async Task Terminal_input_owns_a_combined_frame_before_tools_or_context_observers(string transcriptionKey)
    {
        await using var run = new MessageRun();
        run.Push(JsonSerializer.Serialize(new
        {
            serverContent = new Dictionary<string, object>
            {
                [transcriptionKey] = new { text = "End this conversation." },
                ["outputTranscription"] = new { text = "This response must not win." },
            },
            toolCall = new { functionCalls = new[] { new { id = "late-action", name = "test_pending_action", args = new { } } } },
        }));

        await run.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(run.Session.IsCancellationRequested);
        Assert.False(run.ToolEntered.Task.IsCompleted);
        Assert.False(run.UserObserved.Task.IsCompleted);
        Assert.Equal(0, run.PendingToolCount());
    }

    [Theory]
    [InlineData("Can you hear me?")]
    [InlineData("What can you help me with?")]
    [InlineData("Explain this selected text.")]
    public async Task Conversational_input_alone_is_observed_without_invoking_an_action(string text)
    {
        await using var run = new MessageRun();
        run.Push(JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text } } }));
        await run.UserObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(run.ToolEntered.Task.IsCompleted);
        Assert.False(run.Session.IsCancellationRequested);
    }

    [Fact]
    public async Task Explicit_browser_cancel_reaches_its_owner_before_an_unrelated_tool_finishes()
    {
        var coordinator = new BrowserAutomationCoordinator(null, null, null, null);
        var runtimeType = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", BindingFlags.NonPublic)!;
        var runtime = Activator.CreateInstance(runtimeType, nonPublic: true)!;
        runtimeType.GetProperty("State")!.SetValue(runtime, BrowserTaskState.Executing);
        typeof(BrowserAutomationCoordinator).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(coordinator, runtime);
        var commands = new CommandRegistry(null, null, null, null, null, browserAutomation: coordinator);
        await using var run = new MessageRun();
        run.ObserveControls(commands.ObserveImmediateJarvisControl);
        run.Push(ToolCall("blocked-unrelated-action"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        run.Push("""{"serverContent":{"inputTranscription":{"text":"Okay stop, cancel browser automation."}}}""");
        Assert.Equal(BrowserTaskState.Cancelled, coordinator.Snapshot().State);
        Assert.False(run.ToolToken.IsCancellationRequested); // Do not cancel the unrelated request.
        Assert.False(run.Session.IsCancellationRequested);
        Assert.False(run.UserObserved.Task.IsCompleted); // Its receipt is still ordered after the tool.
        Assert.Null(commands.ObserveImmediateJarvisControl("cancel browser automation", run.Session.Token));

        run.FinishTool();
        await run.DrainAsync();
        var field = typeof(CommandRegistry).GetField("_browserPreRouteReceipt", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var receipt = Assert.IsType<BrowserPreRouteReceipt>(field.GetValue(commands));
        Assert.True(receipt.Ok);
        Assert.Contains("cancelled", receipt.Message);
        Assert.True(receipt.TryAcknowledgeOnce());
        Assert.False(receipt.TryAcknowledgeOnce());
        commands.ObserveJarvisTranscript("Okay stop, cancel browser automation.", run.Session.Token);
        Assert.Same(receipt, field.GetValue(commands));
        Assert.False(receipt.TryAcknowledgeOnce());
        ((CancellationTokenSource)runtimeType.GetProperty("Cancellation")!.GetValue(runtime)!).Dispose();
    }

    [Fact]
    public async Task Shutdown_drops_a_cancel_receipt_waiting_behind_a_tool()
    {
        await using var run = new MessageRun();
        var committed = false;
        run.ObserveControls((_, _) => () => committed = true);
        run.Push(ToolCall("before-shutdown"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.Push("""{"serverContent":{"inputTranscription":{"text":"cancel browser automation"}}}""");
        run.Release();
        await run.DrainAsync();
        Assert.False(committed);
    }

    [Fact]
    public async Task Combined_frame_keeps_authoritative_input_before_its_tool_execution()
    {
        await using var run = new MessageRun();
        run.Push(JsonSerializer.Serialize(new
        {
            serverContent = new { inputTranscription = new { text = "Open the requested folder." } },
            toolCall = new { functionCalls = new[] { new { id = "combined", name = "test_pending_action", args = new { } } } },
        }));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(run.UserObservedBeforeTool);
    }

    [Fact]
    public async Task Provider_cancellation_reaches_the_matching_inflight_tool()
    {
        await using var run = new MessageRun();
        var reader = run.ReadAsync(
            ToolCall("action-2"),
            """{"toolCallCancellation":{"ids":["action-2"]}}""");

        try
        {
            await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // ReadAsync and this assertion both resume from ToolEntered. First
            // prove that the cancellation frame was delivered; otherwise a
            // loaded test runner can race its 500ms timer against its own sender.
            await reader.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(run.ToolToken.IsCancellationRequested);
            await run.ToolCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(run.Session.IsCancellationRequested);
        }
        finally
        {
            run.Release();
            await IgnoreCancelledAsync(reader);
        }
    }

    [Fact]
    public async Task End_tool_waits_for_the_authoritative_transcript_instead_of_rejecting_early()
    {
        await using var run = new MessageRun();
        run.Push(EndCall("end-before-transcript"));

        await Task.Delay(100);
        Assert.Equal(1, run.PendingToolCount());

        run.Push("""{"serverContent":{"inputTranscription":{"text":"That's all, goodbye."}}}""");
        Assert.True(run.LocalTerminationRequested() || run.Ended.Task.IsCompletedSuccessfully);
        await run.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(run.Session.IsCancellationRequested);
    }

    [Fact]
    public async Task Dedicated_end_tool_is_a_safe_fallback_when_no_input_transcription_arrives()
    {
        await using var run = new MessageRun();
        run.Push(EndCall("end-without-transcript"));

        await run.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(run.Session.IsCancellationRequested);
    }

    [Fact]
    public async Task Dedicated_end_tool_cannot_override_contradictory_user_transcription()
    {
        await using var run = new MessageRun();
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Close the current browser tab."}}}""");
        await run.UserObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.Push(EndCall("incorrect-end"));

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert.False(run.LocalTerminationRequested());
        Assert.False(run.Session.IsCancellationRequested);
    }

    private static string ToolCall(string id) => JsonSerializer.Serialize(new
    {
        toolCall = new { functionCalls = new[] { new { id, name = "test_pending_action", args = new { } } } },
    });

    private static string EndCall(string id) => JsonSerializer.Serialize(new
    {
        toolCall = new
        {
            functionCalls = new[]
            {
                new { id, name = "end_conversation", args = new { reason = "User ended the conversation." } },
            },
        },
    });

    private static async Task IgnoreCancelledAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Completed_turn_releases_authority_after_its_tools_before_the_next_observation()
    {
        await using var run = new MessageRun();
        var events = new List<string>();
        run.ObserveTurnOrder(events);
        run.Push("""{"serverContent":{"inputTranscription":{"text":"First request."}}}""");
        run.Push(ToolCall("slow-first-action"));
        await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        run.Push("""{"serverContent":{"turnComplete":true}}""");
        run.Push("""{"serverContent":{"inputTranscription":{"text":"Next request."}}}""");
        Assert.Equal(new[] { "First request." }, events);
        run.FinishTool();
        await run.DrainAsync();
        Assert.Equal(new[] { "First request.", "completed", "Next request." }, events);
    }

    private sealed class MessageRun : IAsyncDisposable
    {
        private readonly GeminiLiveConversationService _service = new(null, null, null);
        private readonly MethodInfo _handle = typeof(GeminiLiveConversationService).GetMethod(
            "HandleServerMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly TaskCompletionSource<bool> _release = NewSignal();
        private readonly object _diagnostics;
        public CancellationTokenSource Session { get; } = new();
        public TaskCompletionSource<bool> ToolEntered { get; } = NewSignal();
        public TaskCompletionSource<bool> ToolCancelled { get; } = NewSignal();
        public CancellationToken ToolToken { get; private set; }
        public bool UserObservedBeforeTool { get; private set; }
        public TaskCompletionSource<bool> Ended { get; } = NewSignal();
        public TaskCompletionSource<bool> UserObserved { get; } = NewSignal();

        public MessageRun()
        {
            typeof(GeminiLiveConversationService).GetField(
                "_sessionCts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_service, Session);
            var diagnosticsType = typeof(GeminiLiveConversationService).GetNestedType(
                "ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
            _diagnostics = Activator.CreateInstance(diagnosticsType,
                "ordering-test", 1, DateTimeOffset.UtcNow, 0, false)!;
            _service.SetSessionEndingObserver(() =>
            {
                Ended.TrySetResult(true);
                return Task.CompletedTask;
            });
            _service.SetUserTranscriptObserver((_, _) => UserObserved.TrySetResult(true));
            _service.SetToolExecutor(async (_, _, token) =>
            {
                UserObservedBeforeTool = UserObserved.Task.IsCompletedSuccessfully;
                ToolToken = token;
                ToolEntered.TrySetResult(true);
                try { await _release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    ToolCancelled.TrySetResult(true);
                    throw;
                }
                token.ThrowIfCancellationRequested();
                return new Dictionary<string, object> { ["ok"] = true };
            });
        }

        public async Task ReadAsync(params string[] messages)
        {
            // Same sequential message dispatch as the production receive loop.
            for (var index = 0; index < messages.Length; index++)
            {
                _handle.Invoke(_service, [messages[index], NewSignal(), _diagnostics, Session.Token]);
                if (index == 0)
                    await ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }

        public void Push(string message) =>
            _handle.Invoke(_service, [message, NewSignal(), _diagnostics, Session.Token]);

        public void ObserveControls(Func<string, CancellationToken, Action> observer) =>
            _service.SetUserTranscriptObserver((_, _) => UserObserved.TrySetResult(true), observer);

        public void ObserveTurnOrder(List<string> events) =>
            _service.SetUserTranscriptObserver((text, _) => events.Add(text), userTurnCompletedObserver: () => events.Add("completed"));

        public void FinishTool() => _release.TrySetResult(true);

        public async Task DrainAsync()
        {
            var dispatcher = (LiveToolCallDispatcher)_diagnostics.GetType().GetProperty("ToolCalls")!.GetValue(_diagnostics);
            if (dispatcher != null)
                await dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        }

        public int PendingToolCount()
        {
            var dispatcher = _diagnostics.GetType().GetProperty(
                "ToolCalls", BindingFlags.Instance | BindingFlags.Public)!.GetValue(_diagnostics);
            return dispatcher == null
                ? 0
                : (int)dispatcher.GetType().GetProperty(
                    "PendingCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dispatcher)!;
        }

        public bool LocalTerminationRequested() => (int)typeof(GeminiLiveConversationService).GetField(
            "_autoEnding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)! == 1;

        public void Release()
        {
            Session.Cancel();
            _release.TrySetResult(true);
        }

        public async ValueTask DisposeAsync()
        {
            Release();
            await _service.StopAsync(showDone: false);
            _service.Dispose();
        }

        private static TaskCompletionSource<bool> NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

// These exercise real queued receive/tool handlers and the process-wide UI
// publisher. Run without concurrent native overlay/hotkey integration tests;
// otherwise their global UI activity and blocking waits contaminate this timing.
[CollectionDefinition("Live event integration", DisableParallelization = true)]
public sealed class LiveEventIntegrationCollection { }
