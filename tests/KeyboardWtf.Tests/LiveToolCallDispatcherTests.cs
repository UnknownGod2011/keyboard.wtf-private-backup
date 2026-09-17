using System.Collections.Concurrent;
using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class LiveToolCallDispatcherTests
{
    [Fact]
    public void Multi_target_batch_marks_a_later_singular_reference_ambiguous()
    {
        var first = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["referential_target"] = new { kind = "folder", target = "one" },
        };
        var second = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["referential_target"] = new { kind = "folder", target = "two" },
        };

        LiveToolCallDispatcher.AnnotateReferentialGroup([first, second]);

        foreach (var response in new[] { first, second })
        {
            var group = Assert.IsType<Dictionary<string, object>>(response["referential_group"]);
            Assert.Equal("multiple", group["cardinality"]);
            Assert.Equal(2, group["target_count"]);
            Assert.False(group.ContainsKey("targets")); // Keep payload growth linear.
        }
    }

    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Workflow_targets_and_separate_action_targets_share_one_complete_group()
    {
        var workflow = new Dictionary<string, object>
        {
            ["ok"] = false, // A failed action does not erase the successful subset.
            ["referential_targets"] = new object[] { new { target = "one" }, new { target = "two" } },
        };
        var separate = new Dictionary<string, object> { ["ok"] = true, ["referential_target"] = new { target = "three" } };
        LiveToolCallDispatcher.AnnotateReferentialGroup([workflow, separate]);
        var group = Assert.IsType<Dictionary<string, object>>(workflow["referential_group"]);
        Assert.Equal(3, group["target_count"]);
        Assert.Equal(2, Assert.IsType<object[]>(workflow["referential_targets"]).Length);
        Assert.True(separate.ContainsKey("referential_target"));
    }

    [Fact]
    public async Task Multiple_actions_in_one_provider_batch_return_one_combined_response()
    {
        var entered = Signal();
        var release = Signal();
        var responses = new ConcurrentQueue<string>();
        using var queue = Queue(async (_, args, token) =>
        {
            if (args.GetProperty("value").GetString() == "second")
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
            }
            return Ok();
        }, responses);
        queue.Enqueue(JsonSerializer.SerializeToElement(new
        {
            functionCalls = new[]
            {
                new { id = "one", name = "test_action", args = new { value = "first" } },
                new { id = "two", name = "test_action", args = new { value = "second" } },
            },
        }), new());
        await entered.Task.WaitAsync(AsyncTestTimeout);
        var earlyResponses = responses.Count;
        release.TrySetResult(true);
        await queue.Completion.WaitAsync(AsyncTestTimeout);
        Assert.Equal(0, earlyResponses);
        var batch = JsonDocument.Parse(Assert.Single(responses)).RootElement
            .GetProperty("toolResponse").GetProperty("functionResponses");
        Assert.Equal(new[] { "one", "two" }, batch.EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Cancellation_during_a_batch_suppresses_an_already_finished_calls_pending_result()
    {
        var entered = Signal();
        var release = Signal();
        var responses = new ConcurrentQueue<string>();
        using var queue = Queue(async (_, args, token) =>
        {
            if (args.GetProperty("value").GetString() == "second")
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
            }
            return Ok();
        }, responses);
        queue.Enqueue(JsonSerializer.SerializeToElement(new
        {
            functionCalls = new[]
            {
                new { id = "one", name = "test_action", args = new { value = "first" } },
                new { id = "two", name = "test_action", args = new { value = "second" } },
            },
        }), new());
        await entered.Task.WaitAsync(AsyncTestTimeout);
        queue.Cancel(JsonSerializer.SerializeToElement(new[] { "one" }));
        release.TrySetResult(true);
        await queue.Completion.WaitAsync(AsyncTestTimeout);
        var batch = JsonDocument.Parse(Assert.Single(responses)).RootElement
            .GetProperty("toolResponse").GetProperty("functionResponses");
        Assert.Equal("two", Assert.Single(batch.EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Later_transcript_cannot_replace_the_context_of_an_inflight_action()
    {
        var entered = Signal();
        var release = Signal();
        var context = "";
        var observedDuringAction = "";
        var responses = new ConcurrentQueue<string>();
        using var queue = Queue(async (_, _, token) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(token);
            observedDuringAction = context;
            return Ok();
        }, responses);
        queue.Observe((text, _) => context = text, "first request");
        queue.Enqueue(Call("first", "value"), new());
        await entered.Task.WaitAsync(AsyncTestTimeout);
        queue.Observe((text, _) => context = text, "different request");
        Assert.Equal("first request", context);
        release.TrySetResult(true);
        await queue.Completion.WaitAsync(AsyncTestTimeout);
        Assert.Equal("first request", observedDuringAction);
        Assert.Equal("different request", context);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Shutdown_discards_queued_observers_instead_of_executing_stale_preroutes()
    {
        var entered = Signal();
        var observed = false;
        using var queue = Queue(async (_, _, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok();
        }, new());
        queue.Enqueue(Call("first", "value"), new());
        await entered.Task.WaitAsync(AsyncTestTimeout);
        queue.Observe((_, _) => observed = true, "new request");
        queue.Stop();
        await queue.Completion.WaitAsync(AsyncTestTimeout);
        Assert.False(observed);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Actions_remain_serial_and_duplicate_provider_ids_are_not_replayed()
    {
        var entered = Signal();
        var release = Signal();
        var actions = new ConcurrentQueue<string>();
        var responses = new ConcurrentQueue<string>();
        using var queue = Queue(async (_, args, token) =>
        {
            var value = args.GetProperty("value").GetString();
            actions.Enqueue(value);
            if (value == "first")
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
            }
            return Ok();
        }, responses);

        queue.Enqueue(Call("one", "first"), new());
        await entered.Task.WaitAsync(AsyncTestTimeout);
        queue.Enqueue(Call("two", "second"), new());
        queue.Enqueue(Call("two", "second"), new());
        Assert.Equal(new[] { "first" }, actions.ToArray());
        Assert.Equal(2, queue.PendingCount);
        release.TrySetResult(true);
        await queue.Completion.WaitAsync(AsyncTestTimeout);
        queue.Enqueue(Call("one", "first"), new());
        await queue.Completion;
        Assert.Equal(new[] { "first", "second" }, actions.ToArray());
        Assert.Equal(2, responses.Count);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Semantic_duplicate_with_a_new_provider_id_is_acknowledged_silently_once()
    {
        var actions = 0;
        var responses = new ConcurrentQueue<string>();
        var guard = new ToolAttemptGuard();
        using var queue = Queue((_, _, _) =>
        {
            Interlocked.Increment(ref actions);
            return Task.FromResult(Ok());
        }, responses);

        queue.Enqueue(JsonSerializer.SerializeToElement(new
        {
            functionCalls = new[]
            {
                new { id = "first", name = "test_action", args = new { value = "same", scope = "all" } },
            },
        }), guard);
        await queue.Completion.WaitAsync(AsyncTestTimeout);
        queue.Enqueue(JsonSerializer.SerializeToElement(new
        {
            functionCalls = new[]
            {
                new { id = "retry", name = "test_action", args = new { scope = "all", value = "same" } },
            },
        }), guard);
        await queue.Completion.WaitAsync(AsyncTestTimeout);

        Assert.Equal(1, actions);
        Assert.Equal(2, responses.Count);
        var function = JsonDocument.Parse(responses.Last()).RootElement
            .GetProperty("toolResponse").GetProperty("functionResponses")[0];
        var duplicate = function.GetProperty("response");
        Assert.True(duplicate.GetProperty("duplicate_suppressed").GetBoolean());
        Assert.False(function.TryGetProperty("scheduling", out _));
        Assert.False(duplicate.TryGetProperty("scheduling", out _));
        Assert.Equal("", duplicate.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synchronous_live_results_never_emit_async_scheduling_metadata(bool acknowledged)
    {
        var responses = new ConcurrentQueue<string>();
        using var queue = Queue((_, _, _) => Task.FromResult(new Dictionary<string, object>
        {
            ["ok"] = true,
            ["duplicate_suppressed"] = acknowledged,
            ["message"] = acknowledged ? "" : "Done.",
        }), responses);
        queue.Enqueue(Call("one", "value"), new());
        await queue.Completion.WaitAsync(AsyncTestTimeout);
        var function = JsonDocument.Parse(Assert.Single(responses)).RootElement
            .GetProperty("toolResponse").GetProperty("functionResponses")[0];
        Assert.False(function.TryGetProperty("scheduling", out _));
        Assert.False(function.GetProperty("response").TryGetProperty("scheduling", out _));
    }

    [Fact]
    public async Task Cancelled_queued_action_never_executes_but_an_unrelated_action_still_does()
    {
        var entered = Signal();
        var release = Signal();
        var actions = new ConcurrentQueue<string>();
        var responses = new ConcurrentQueue<string>();
        using var queue = Queue(async (_, args, token) =>
        {
            var value = args.GetProperty("value").GetString();
            actions.Enqueue(value);
            if (value == "first")
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(token);
            }
            return Ok();
        }, responses);
        queue.Enqueue(Call("one", "first"), new());
        await entered.Task.WaitAsync(AsyncTestTimeout);
        queue.Enqueue(Call("two", "cancelled"), new());
        queue.Enqueue(Call("three", "third"), new());
        queue.Cancel(JsonSerializer.SerializeToElement(new[] { "two", "unknown-id" }));
        release.TrySetResult(true);
        await queue.Completion.WaitAsync(AsyncTestTimeout);
        Assert.Equal(new[] { "first", "third" }, actions.ToArray());
        Assert.Equal(2, responses.Count);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Late_success_from_a_cancelled_action_cannot_publish_a_response_or_poison_the_guard()
    {
        var entered = Signal();
        var release = Signal();
        var responses = new ConcurrentQueue<string>();
        var guard = new ToolAttemptGuard();
        var call = Call("stale", "old");
        using var queue = Queue(async (_, _, _) =>
        {
            entered.TrySetResult(true);
            await release.Task; // Simulates a dependency that ignores cancellation.
            return Ok();
        }, responses);
        queue.Enqueue(call, guard);
        await entered.Task.WaitAsync(AsyncTestTimeout);
        queue.Cancel(JsonSerializer.SerializeToElement(new[] { "stale" }));
        release.TrySetResult(true);
        await queue.Completion.WaitAsync(AsyncTestTimeout);
        Assert.Empty(responses);
        Assert.False(guard.TryGetSuppressed("test_action",
            call.GetProperty("functionCalls")[0].GetProperty("args"), out _));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Physical_connection_stop_cancels_old_work_without_cancelling_a_replacement_queue()
    {
        var entered = Signal();
        var oldResponses = new ConcurrentQueue<string>();
        var newResponses = new ConcurrentQueue<string>();
        using var oldQueue = Queue(async (_, _, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok();
        }, oldResponses);
        using var newQueue = Queue((_, _, _) => Task.FromResult(Ok()), newResponses);
        oldQueue.Enqueue(Call("old", "old"), new());
        await entered.Task.WaitAsync(AsyncTestTimeout);
        oldQueue.Stop();
        oldQueue.Enqueue(Call("late", "late"), new());
        newQueue.Enqueue(Call("new", "new"), new());
        await Task.WhenAll(oldQueue.Completion, newQueue.Completion).WaitAsync(AsyncTestTimeout);
        Assert.Empty(oldResponses);
        Assert.Single(newResponses);
        Assert.Equal(0, oldQueue.PendingCount);
        Assert.Equal(0, newQueue.PendingCount);
    }

    [Fact]
    public async Task Tool_exception_reports_failure_once_and_does_not_leave_a_pending_provider_call()
    {
        var responses = new ConcurrentQueue<string>();
        var invocations = 0;
        using var queue = Queue((_, _, _) =>
        {
            Interlocked.Increment(ref invocations);
            throw new InvalidOperationException("test failure");
        }, responses);
        queue.Enqueue(Call("failure", "value"), new());
        await queue.Completion.WaitAsync(AsyncTestTimeout);
        var response = JsonDocument.Parse(Assert.Single(responses)).RootElement
            .GetProperty("toolResponse").GetProperty("functionResponses")[0].GetProperty("response");
        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Equal(1, invocations);
        Assert.Equal(0, queue.PendingCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_while_transport_is_busy_suppresses_the_cancelled_result(bool includeUnrelated)
    {
        var waitingForTransport = Signal();
        var transportReady = Signal();
        var responses = new ConcurrentQueue<string>();
        using var queue = new LiveToolCallDispatcher(CancellationToken.None,
            (_, args, _) => Task.FromResult(new Dictionary<string, object>
            {
                ["ok"] = true,
                ["referential_target"] = new { kind = "folder", target = args.GetProperty("value").GetString() },
            }), async (buildResponse, token) =>
            {
                waitingForTransport.TrySetResult(true);
                await transportReady.Task.WaitAsync(token);
                var response = buildResponse();
                if (response != null)
                    responses.Enqueue(JsonSerializer.Serialize(response));
            });
        var calls = new List<object> { new { id = "cancel-while-sending", name = "test_action", args = new { value = "first" } } };
        if (includeUnrelated)
            calls.Add(new { id = "unrelated", name = "test_action", args = new { value = "second" } });
        queue.Enqueue(JsonSerializer.SerializeToElement(new { functionCalls = calls }), new());
        await waitingForTransport.Task.WaitAsync(AsyncTestTimeout);
        queue.Cancel(JsonSerializer.SerializeToElement(new[] { "cancel-while-sending" }));
        transportReady.TrySetResult(true);
        await queue.Completion.WaitAsync(AsyncTestTimeout);
        if (includeUnrelated)
        {
            var batch = JsonDocument.Parse(Assert.Single(responses)).RootElement
                .GetProperty("toolResponse").GetProperty("functionResponses");
            Assert.Equal("unrelated", Assert.Single(batch.EnumerateArray()).GetProperty("id").GetString());
            Assert.False(batch[0].GetProperty("response").TryGetProperty("referential_group", out _));
        }
        else Assert.Empty(responses);
        Assert.Equal(0, queue.PendingCount);
    }

    private static LiveToolCallDispatcher Queue(
        Func<string, JsonElement, CancellationToken, Task<Dictionary<string, object>>> execute,
        ConcurrentQueue<string> responses) => new(CancellationToken.None, execute, (buildResponse, token) =>
        {
            token.ThrowIfCancellationRequested();
            var response = buildResponse();
            if (response != null)
                responses.Enqueue(JsonSerializer.Serialize(response));
            return Task.CompletedTask;
        });

    private static JsonElement Call(string id, string value) => JsonSerializer.SerializeToElement(new
    {
        functionCalls = new[] { new { id, name = "test_action", args = new { value } } },
    });

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Dictionary<string, object> Ok() => new() { ["ok"] = true };
}
