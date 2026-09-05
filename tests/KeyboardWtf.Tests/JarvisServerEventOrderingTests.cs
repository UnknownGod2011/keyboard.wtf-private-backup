using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

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
            var shutdown = await Task.WhenAny(run.Ended.Task, Task.Delay(500));
            Assert.Same(run.Ended.Task, shutdown);
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
    public async Task Provider_cancellation_reaches_the_matching_inflight_tool()
    {
        await using var run = new MessageRun();
        var reader = run.ReadAsync(
            ToolCall("action-2"),
            """{"toolCallCancellation":{"ids":["action-2"]}}""");

        try
        {
            await run.ToolEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var cancelled = await Task.WhenAny(run.ToolCancelled.Task, Task.Delay(500));
            Assert.Same(run.ToolCancelled.Task, cancelled);
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
        var shutdown = await Task.WhenAny(run.Ended.Task, Task.Delay(500));

        Assert.Same(run.Ended.Task, shutdown);
        Assert.True(run.Session.IsCancellationRequested);
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
            _service.SetUserTranscriptObserver(_ => UserObserved.TrySetResult(true));
            _service.SetToolExecutor(async (_, _, token) =>
            {
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

        public int PendingToolCount()
        {
            var dispatcher = _diagnostics.GetType().GetProperty(
                "ToolCalls", BindingFlags.Instance | BindingFlags.Public)!.GetValue(_diagnostics);
            return dispatcher == null
                ? 0
                : (int)dispatcher.GetType().GetProperty(
                    "PendingCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dispatcher)!;
        }

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
