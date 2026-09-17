namespace KeyboardWtf.Services;

using System.Text.Json;

// The socket reader must remain available for input, farewell and cancellation
// while a tool is awaiting I/O. Actions still execute serially, and all work is
// owned by this one physical connection rather than a later replacement socket.
internal sealed class LiveToolCallDispatcher : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Func<string, JsonElement, CancellationToken, Task<Dictionary<string, object>>> _execute;
    private readonly Func<Func<object>, CancellationToken, Task> _respond;
    private Task _completion = Task.CompletedTask;
    private int _stopped;
    private int _disposed;
    private int _pendingObservations;

    internal LiveToolCallDispatcher(
        CancellationToken token,
        Func<string, JsonElement, CancellationToken, Task<Dictionary<string, object>>> execute,
        Func<Func<object>, CancellationToken, Task> respond)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _execute = execute;
        _respond = respond;
    }

    internal Task Completion { get { lock (_sync) return _completion; } }
    internal int PendingCount { get { lock (_sync) return _pending.Count + _pendingObservations; } }

    internal void Observe(Action<string, CancellationToken> observer, string transcript)
    {
        if (observer == null)
            return;
        lock (_sync)
        {
            if (Volatile.Read(ref _stopped) != 0)
                return;
            var previous = _completion;
            _pendingObservations++;
            _completion = Task.Run(async () =>
            {
                try
                {
                    await previous;
                    _lifetime.Token.ThrowIfCancellationRequested();
                    observer(transcript, _lifetime.Token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception exception) { AppLog.Warning(exception, "Jarvis transcript observer failed"); }
                finally { lock (_sync) _pendingObservations--; }
            });
        }
    }

    internal void Enqueue(JsonElement toolCall, ToolAttemptGuard guard)
    {
        if (!toolCall.TryGetProperty("functionCalls", out var calls) || calls.ValueKind != JsonValueKind.Array)
            return;
        lock (_sync)
        {
            if (Volatile.Read(ref _stopped) != 0)
                return;
            var work = new List<Call>();
            foreach (var call in calls.EnumerateArray())
            {
                var id = call.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
                if (!_seen.Add(id))
                    continue;
                var name = call.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                var args = call.TryGetProperty("args", out var argsElement) ? argsElement.Clone() : default;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _pending.Add(id, cancellation);
                work.Add(new Call(id, name, args, cancellation));
            }
            if (work.Count == 0)
                return;
            var previous = _completion;
            _completion = Task.Run(() => RunAsync(previous, work, guard));
        }
    }

    internal void Cancel(JsonElement ids)
    {
        if (ids.ValueKind != JsonValueKind.Array)
            return;
        lock (_sync)
        {
            foreach (var id in ids.EnumerateArray())
                if (id.ValueKind == JsonValueKind.String && _pending.TryGetValue(id.GetString() ?? "", out var pending))
                    pending.Cancel();
        }
    }

    internal void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
            _lifetime.Cancel();
    }

    private async Task RunAsync(Task previous, List<Call> calls, ToolAttemptGuard guard)
    {
        try
        {
            await previous;
            var results = new List<(Call Call, Dictionary<string, object> Response)>();
            foreach (var call in calls)
            {
                var token = call.Cancellation.Token;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!guard.TryGetSuppressed(call.Name, call.Args, out var response))
                    {
                        try
                        {
                            response = await _execute(call.Name, call.Args, token);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            AppLog.Warning(exception, $"Jarvis tool execution failed: {call.Name}");
                            response = new Dictionary<string, object>
                            {
                                ["ok"] = false,
                                ["supported"] = false,
                                ["message"] = "The action failed without a verified result. Do not claim success or automatically repeat it.",
                            };
                        }
                        token.ThrowIfCancellationRequested();
                        guard.Record(call.Name, call.Args, response);
                    }
                    token.ThrowIfCancellationRequested();
                    results.Add((call, response));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    AppLog.Warning(exception, $"Jarvis tool dispatch failed: {call.Name}");
                }
            }
            // One provider request owns one combined response, as in 1.4.17.
            // Keep completed calls cancellable until the batch is published:
            // cancellation arriving during another action must suppress them too.
            _lifetime.Token.ThrowIfCancellationRequested();
            // Materialize only once the transport owns its send lock. A result
            // cancelled while waiting for audio/socket output must not survive
            // in an already-created message; unrelated results still publish.
            await _respond(() =>
            {
                var responses = results
                    .Where(result => !result.Call.Cancellation.IsCancellationRequested)
                    // These declarations use the provider's default blocking
                    // behavior. Scheduling metadata belongs only to explicitly
                    // NON_BLOCKING calls; adding it here can defer a dependency
                    // and replay an already-completed effect. Keep the plain
                    // response shape used by 1.4.17 and the blocking contract.
                    .Select(result => new
                    {
                        id = result.Call.Id,
                        name = result.Call.Name,
                        response = new Dictionary<string, object>(result.Response),
                    })
                    .ToArray();
                AnnotateReferentialGroup(responses.Select(result => result.response));
                return responses.Length == 0 ? null : new { toolResponse = new { functionResponses = responses } };
            }, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // Every earlier batch observes its own failures. This is a final
            // diagnostic boundary, never permission to replay a failed action.
            AppLog.Warning(exception, "Jarvis tool queue ended");
        }
        finally
        {
            lock (_sync)
            {
                foreach (var call in calls)
                {
                    _pending.Remove(call.Id);
                    call.Cancellation.Dispose();
                }
            }
        }
    }

    internal static void AnnotateReferentialGroup(
        IEnumerable<Dictionary<string, object>> responses)
    {
        var owners = (responses ?? [])
            .Where(response => response != null
                && !response.ContainsKey("duplicate_suppressed"))
            .ToArray();
        var targets = owners.SelectMany(response =>
            response.TryGetValue("referential_targets", out var many) && many is IEnumerable<object> list
                ? list
                : response.TryGetValue("referential_target", out var one) && one != null
                    ? new[] { one }
                    : Array.Empty<object>()).ToArray();
        if (targets.Length <= 1)
            return;

        foreach (var response in owners)
        {
            response["referential_group"] = new Dictionary<string, object>
            {
                ["cardinality"] = "multiple",
                ["target_count"] = targets.Length,
                // The individual/workflow results already contain the exact
                // targets. Do not replicate the whole set into every response.
                ["instruction"] = "A later singular reference is ambiguous; ask the user to choose one target instead of selecting by execution order.",
            };
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Stop();
        _lifetime.Dispose();
    }

    private sealed record Call(string Id, string Name, JsonElement Args, CancellationTokenSource Cancellation);
}
