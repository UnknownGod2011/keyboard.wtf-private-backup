using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class JarvisServerEventOrderingTests
{
    [Fact]
    public async Task Recognized_terminal_input_is_not_queued_behind_an_inflight_tool()
    {
        await using var run = new MessageRun();
        var reader = run.ReadAsync(
            ToolCall("action-1"),
            """{"serverContent":{"inputTranscription":{"text":"end this conversation"}}}""");

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

    private static string ToolCall(string id) => JsonSerializer.Serialize(new
    {
        toolCall = new { functionCalls = new[] { new { id, name = "test_pending_action", args = new { } } } },
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
