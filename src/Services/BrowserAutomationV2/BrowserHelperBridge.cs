namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

public sealed class BrowserHelperBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentQueue<BrowserActionRequest> _commands = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _statusLock = new();
    private readonly Dictionary<string, DateTime> _helperHeartbeats = new(
        StringComparer.OrdinalIgnoreCase);
    private Task _serverTask;
    private BrowserHelperStatus _status = new();
    private bool _disposed;

    public event Action<BrowserHelperStatus> StatusChanged;

    public BrowserHelperStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                if (_status.LastHeartbeatUtc is not DateTime heartbeat
                    || DateTime.UtcNow - heartbeat <= TimeSpan.FromSeconds(4))
                {
                    return _status;
                }

                return new BrowserHelperStatus
                {
                    NativeHostRegistered = _status.NativeHostRegistered,
                    Connected = false,
                    CommandChannelReady = false,
                    ActiveTabFresh = false,
                    MultipleHelpersDetected = _status.MultipleHelpersDetected,
                    ExpectedVersion = _status.ExpectedVersion,
                    ServiceWorkerVersion = _status.ServiceWorkerVersion,
                    ContentScriptVersion = _status.ContentScriptVersion,
                    ExtensionId = _status.ExtensionId,
                    SessionId = _status.SessionId,
                    WindowId = _status.WindowId,
                    TabId = _status.TabId,
                    ActiveUrl = _status.ActiveUrl,
                    ActiveTitle = _status.ActiveTitle,
                    GraphVersion = _status.GraphVersion,
                    DomEpoch = _status.DomEpoch,
                    ControlCount = _status.ControlCount,
                    LastHeartbeatUtc = _status.LastHeartbeatUtc,
                    LastError = "Chrome Helper heartbeat is stale.",
                };
            }
        }
    }

    public void Start()
    {
        if (_serverTask != null)
            return;
        _serverTask = Task.Run(() => RunServerAsync(_lifetime.Token));
    }

    public async Task<BrowserPageSnapshot> BeginTaskAsync(
        string taskId,
        bool useCurrentTab,
        CancellationToken token)
    {
        var result = await SendAsync(
            new BrowserActionRequest
            {
                TaskId = taskId,
                Type = "begin_task",
                Text = useCurrentTab ? "current" : "new",
            },
            token);
        return DeserializeResult<BrowserPageSnapshot>(result, "The Chrome Helper did not return a page snapshot.");
    }

    public async Task<BrowserPageSnapshot> ObserveAsync(
        string taskId,
        int tabId,
        CancellationToken token)
    {
        var result = await SendAsync(
            new BrowserActionRequest
            {
                TaskId = taskId,
                Type = "observe",
                TabId = tabId,
            },
            token);
        return DeserializeResult<BrowserPageSnapshot>(result, "The Chrome Helper could not read the controlled tab.");
    }

    public async Task<BrowserActionReceipt> ExecuteAsync(
        BrowserActionRequest action,
        CancellationToken token)
    {
        var result = await SendAsync(action, token);
        return DeserializeResult<BrowserActionReceipt>(result, "The Chrome Helper returned no action receipt.");
    }

    public async Task<BrowserActionReceipt> ActivateExistingTabAsync(
        int windowId,
        string selection,
        CancellationToken token)
    {
        var result = await SendAsync(
            new BrowserActionRequest
            {
                TaskId = $"tab-selection-{Guid.NewGuid():N}",
                Type = "activate_existing_tab",
                WindowId = windowId,
                Text = selection,
            },
            token,
            TimeSpan.FromSeconds(5));
        return DeserializeResult<BrowserActionReceipt>(
            result,
            "The Chrome Helper did not confirm the selected tab.");
    }

    public async Task EndTaskAsync(string taskId, CancellationToken token = default)
    {
        try
        {
            await SendAsync(
                new BrowserActionRequest { TaskId = taskId, Type = "end_task" },
                token,
                TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warning(ex, "Chrome Helper cleanup did not acknowledge");
        }
    }

    public async Task CancelTaskAsync(string taskId)
    {
        var cancelled = _pending
            .Where(pair => string.Equals(
                GetTaskId(pair.Key),
                taskId,
                StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in cancelled)
        {
            if (_pending.TryRemove(key, out var completion))
                completion.TrySetCanceled();
        }

        try
        {
            await SendAsync(
                new BrowserActionRequest { TaskId = taskId, Type = "cancel_task" },
                CancellationToken.None,
                TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Local cancellation is authoritative even if the helper disconnected.
        }
    }

    private async Task<JsonElement> SendAsync(
        BrowserActionRequest action,
        CancellationToken token,
        TimeSpan? timeout = null)
    {
        var commandTimeout = timeout ?? TimeSpan.FromSeconds(35);
        await WaitForCommandChannelAsync(
            action.Type == "activate_existing_tab",
            token,
            commandTimeout < TimeSpan.FromSeconds(10)
                ? commandTimeout
                : TimeSpan.FromSeconds(10));

        var key = PendingKey(action.TaskId, action.CommandId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(key, completion))
            throw new InvalidOperationException("Duplicate browser command ID");

        _commands.Enqueue(action);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        linked.CancelAfter(commandTimeout);
        using var registration = linked.Token.Register(() =>
        {
            if (_pending.TryRemove(key, out var pending))
                pending.TrySetCanceled(linked.Token);
        });

        return await completion.Task;
    }

    private async Task WaitForCommandChannelAsync(
        bool serviceWorkerOnly,
        CancellationToken token,
        TimeSpan maximumWait)
    {
        var deadline = DateTime.UtcNow + maximumWait;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var status = Status;
            if (status.CommandChannelReady
                || (serviceWorkerOnly
                    && status.Connected
                    && string.Equals(
                        status.ServiceWorkerVersion,
                        BrowserHelperProtocol.Version,
                        StringComparison.Ordinal)))
                return;

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(status.LastError)
                        ? "Existing Chrome Helper is not connected."
                        : status.LastError);
            }

            await Task.Delay(150, token);
        }
    }

    private async Task RunServerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    BrowserHelperProtocol.PipeName,
                    PipeDirection.InOut,
                    4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                var connectedPipe = pipe;
                pipe = null;
                _ = Task.Run(
                    () => HandleConnectionAsync(connectedPipe, token),
                    CancellationToken.None);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                AppLog.Warning(ex, "Browser native bridge listener failed");
                await Task.Delay(500, token);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        using (pipe)
        {
            while (pipe.IsConnected && !token.IsCancellationRequested)
            {
                try
                {
                    var payload = await ReadFrameAsync(pipe, token);
                    var response = ProcessMessage(Encoding.UTF8.GetString(payload));
                    await WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(response), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (EndOfStreamException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, "Browser native bridge message failed");
                    break;
                }
            }
        }
    }

    private string ProcessMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = String(root, "type");

        var helperMayExecute = true;
        if (type is "hello" or "heartbeat" or "poll" or "command_result")
            helperMayExecute = UpdateStatus(root);

        if (type == "command_result")
        {
            var taskId = String(root, "taskId");
            var commandId = String(root, "commandId");
            var key = PendingKey(taskId, commandId);
            if (_pending.TryRemove(key, out var completion)
                && root.TryGetProperty("result", out var result))
            {
                completion.TrySetResult(result.Clone());
            }
        }

        BrowserActionRequest command = null;
        if (type == "poll" && helperMayExecute)
            _commands.TryDequeue(out command);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            protocolVersion = BrowserHelperProtocol.Version,
            command,
        }, JsonOptions);
    }

    private bool UpdateStatus(JsonElement root)
    {
        var helper = root.TryGetProperty("helper", out var helperElement)
            ? helperElement
            : root;
        var now = DateTime.UtcNow;
        var serviceWorkerVersion = String(helper, "serviceWorkerVersion");
        var contentScriptVersion = String(helper, "contentScriptVersion");
        var extensionId = String(helper, "extensionId");
        var activeUrl = String(helper, "activeUrl");
        var heartbeatFresh = Bool(helper, "activeTabFresh");
        var extensionIdAllowed = BrowserHelperProtocol.IsAllowedExtensionId(extensionId);
        var versionsMatch = string.Equals(
                serviceWorkerVersion,
                BrowserHelperProtocol.Version,
                StringComparison.Ordinal)
            && string.Equals(
                contentScriptVersion,
                BrowserHelperProtocol.Version,
                StringComparison.Ordinal);
        var helperError = BrowserAutomationText.Truncate(
            String(helper, "lastError"),
            240);

        BrowserHelperStatus next;
        bool multipleHelpersDetected;
        lock (_statusLock)
        {
            if (extensionIdAllowed)
                _helperHeartbeats[extensionId] = now;
            foreach (var stale in _helperHeartbeats
                .Where(pair => now - pair.Value > TimeSpan.FromSeconds(4))
                .Select(pair => pair.Key)
                .ToArray())
            {
                _helperHeartbeats.Remove(stale);
            }
            multipleHelpersDetected = HasMultipleFreshExtensionIds(
                _helperHeartbeats,
                now);

            var statusError = multipleHelpersDetected
                ? "Both the Chrome Web Store Helper and unpacked fallback are enabled. Disable the unpacked fallback before browser automation."
                : ResolveStatusError(
                    serviceWorkerVersion,
                    contentScriptVersion,
                    activeUrl,
                    helperError,
                    extensionId);
            next = new BrowserHelperStatus
            {
                NativeHostRegistered = true,
                Connected = true,
                CommandChannelReady = extensionIdAllowed
                    && !multipleHelpersDetected
                    && versionsMatch
                    && heartbeatFresh
                    && Bool(helper, "commandChannelReady"),
                ActiveTabFresh = heartbeatFresh,
                MultipleHelpersDetected = multipleHelpersDetected,
                ExpectedVersion = BrowserHelperProtocol.Version,
                ServiceWorkerVersion = serviceWorkerVersion,
                ContentScriptVersion = contentScriptVersion,
                ExtensionId = extensionId,
                SessionId = String(helper, "sessionId"),
                WindowId = NullableInt(helper, "windowId"),
                TabId = NullableInt(helper, "tabId"),
                ActiveUrl = IsSafePageUrl(activeUrl) ? activeUrl : "",
                ActiveTitle = BrowserAutomationText.Truncate(String(helper, "activeTitle"), 160),
                GraphVersion = Long(helper, "graphVersion"),
                DomEpoch = String(helper, "domEpoch"),
                ControlCount = Int(helper, "controlCount"),
                LastHeartbeatUtc = now,
                LastError = statusError,
            };
            _status = next;
        }
        StatusChanged?.Invoke(next);
        return extensionIdAllowed && !multipleHelpersDetected;
    }

    internal static bool HasMultipleFreshExtensionIds(
        IReadOnlyDictionary<string, DateTime> heartbeats,
        DateTime nowUtc) =>
        heartbeats
            .Where(pair =>
                BrowserHelperProtocol.IsAllowedExtensionId(pair.Key)
                && nowUtc - pair.Value <= TimeSpan.FromSeconds(4))
            .Select(pair => pair.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Count() > 1;

    internal static string ResolveStatusError(
        string serviceWorkerVersion,
        string contentScriptVersion,
        string activeUrl,
        string helperError,
        string extensionId = "")
    {
        if (!string.IsNullOrWhiteSpace(extensionId)
            && !BrowserHelperProtocol.IsAllowedExtensionId(extensionId))
        {
            return "This Chrome Helper identity is not authorized for keyboard.wtf.";
        }

        if (!string.Equals(
                serviceWorkerVersion,
                BrowserHelperProtocol.Version,
                StringComparison.Ordinal))
        {
            return $"Chrome Helper service worker version mismatch. Expected {BrowserHelperProtocol.Version}.";
        }

        if (string.IsNullOrWhiteSpace(contentScriptVersion))
        {
            if (string.IsNullOrWhiteSpace(activeUrl)
                && !string.IsNullOrWhiteSpace(helperError))
            {
                return helperError;
            }

            return $"Chrome Helper content script is unavailable. Expected {BrowserHelperProtocol.Version}.";
        }

        if (!string.Equals(
                contentScriptVersion,
                BrowserHelperProtocol.Version,
                StringComparison.Ordinal))
        {
            return $"Chrome Helper content script version mismatch. Expected {BrowserHelperProtocol.Version}.";
        }

        return helperError ?? "";
    }

    private static T DeserializeResult<T>(JsonElement result, string fallback)
    {
        var value = result.Deserialize<T>(JsonOptions);
        return value ?? throw new InvalidDataException(fallback);
    }

    private static string PendingKey(string taskId, string commandId) =>
        $"{taskId ?? ""}:{commandId ?? ""}";

    private static string GetTaskId(string key)
    {
        var index = key.IndexOf(':');
        return index < 0 ? "" : key[..index];
    }

    private static string String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool Bool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    private static int Int(JsonElement element, string name) =>
        NullableInt(element, name) ?? 0;

    private static int? NullableInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static long Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static bool IsSafePageUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, token);
        var length = BitConverter.ToInt32(header, 0);
        if (length is <= 0 or > 32 * 1024 * 1024)
            throw new InvalidDataException("Invalid browser bridge frame length");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, token);
        return payload;
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        byte[] payload,
        CancellationToken token)
    {
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                token);
            if (read == 0)
                throw new EndOfStreamException();
            offset += read;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        foreach (var completion in _pending.Values)
            completion.TrySetCanceled();
        _pending.Clear();
        try { _serverTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _lifetime.Dispose();
    }
}
