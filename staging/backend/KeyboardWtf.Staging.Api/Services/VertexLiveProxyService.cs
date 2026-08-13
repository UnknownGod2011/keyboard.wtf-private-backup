using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Apis.Auth.OAuth2;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

public interface IVertexAccessTokenProvider
{
    Task<string> GetAsync(CancellationToken cancellationToken);
}

public sealed class VertexAccessTokenProvider : IVertexAccessTokenProvider
{
    private readonly GoogleCredential _credential = GoogleCredential
        .GetApplicationDefault()
        .CreateScoped("https://www.googleapis.com/auth/cloud-platform");

    public Task<string> GetAsync(CancellationToken cancellationToken) =>
        _credential.UnderlyingCredential.GetAccessTokenForRequestAsync(
            cancellationToken: cancellationToken);
}

public sealed class VertexLiveProxyService
{
    private const int MaxMessageBytes = 2 * 1024 * 1024;
    private const int MaxSystemInstructionCharacters = 20_000;
    private const int MaxToolDeclarations = 64;
    private const string ServerSafetyInstruction =
        "This is keyboard.wtf Jarvis. Follow the user's current request, but never expose credentials, " +
        "bypass authentication or security controls, or claim a local action succeeded without a tool result. " +
        "Passwords, OTPs, CAPTCHAs, payments, destructive actions, and public sends/submissions require explicit user control.";
    private static readonly HashSet<string> AllowedVoiceNames = new(StringComparer.Ordinal)
    {
        "Kore", "Orus", "Puck", "Charon", "Aoede", "Zephyr", "Leda", "Fenrir",
    };
    private static readonly HashSet<string> AllowedToolNames = new(StringComparer.Ordinal)
    {
        "open_app", "open_folder", "open_path", "open_url", "window_action", "browser_action",
        "web_search", "browser_automation", "browser_automation_status", "browser_automation_control",
        "get_desktop_context", "get_clipboard_text", "get_selected_text", "replace_selected_text",
        "type_text", "press_key", "search_files", "save_note", "add_todo", "list_todos",
        "complete_todo", "set_timer", "cancel_timer", "list_timers", "system_control", "system_status",
        "play_media", "open_service_page", "open_camera", "take_photo", "take_screenshot", "inspect_screen",
        "virtual_desktop_action", "windows_recording_action", "create_workflow", "remember_routine",
        "list_workflows", "run_workflow", "delete_workflow", "remember_app_alias", "remember_path_alias",
        "remember_link_alias", "remember_workflow_alias", "list_learned_mappings", "forget_learned_mapping",
        "set_browser_preference", "request_sensitive_action", "confirm_sensitive_action",
        "cancel_sensitive_action", "open_gmail_draft", "prepare_whatsapp_message", "copy_text",
        "remember_intent", "recall_intent", "list_intent_memory", "forget_intent_memory", "end_conversation",
    };
    private readonly IAccountStore _store;
    private readonly ICatalogService _catalog;
    private readonly IModelRoutingService _routing;
    private readonly IVertexAccessTokenProvider _tokens;
    private readonly PaidSystemOptions _options;
    private readonly ILogger<VertexLiveProxyService> _logger;

    public VertexLiveProxyService(
        IAccountStore store,
        ICatalogService catalog,
        IModelRoutingService routing,
        IVertexAccessTokenProvider tokens,
        IOptions<PaidSystemOptions> options,
        ILogger<VertexLiveProxyService> logger)
    {
        _store = store;
        _catalog = catalog;
        _routing = routing;
        _tokens = tokens;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(
        HttpContext context,
        CallerIdentity caller,
        CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
            throw new ArgumentException("A WebSocket upgrade is required.");
        var requestId = context.Request.Query["requestId"].ToString();
        if (!IsSafeRequestId(requestId))
            throw new ArgumentException("A stable live-session request ID is required.");

        var catalog = await _catalog.GetAsync(cancellationToken);
        if (catalog.KillSwitch && !CatalogService.IsAiCanary(_options, caller.Email))
            throw new InvalidOperationException(catalog.KillSwitchMessage ?? "Paid AI is temporarily unavailable.");
        var route = await _routing.ResolveAsync(
            "live-audio",
            $"{caller.UserId}:{requestId}",
            cancellationToken);
        ClientCompatibility.Require(
            context.Request.Headers[ClientCompatibility.DesktopVersionHeader].ToString(),
            route.MinimumDesktopVersion);
        var livePolicy = route.LivePolicy
            ?? throw new InvalidOperationException("The Live route has no server-owned session policy.");
        var selectedCandidate = route.Candidates[0];
        await _store.EnsureAccountAsync(caller, cancellationToken);
        var initialReservation = Math.Min(
            livePolicy.ReservationWindowCreditMicros,
            livePolicy.MaxSessionCostCreditMicros);
        var reservation = await _store.ReserveAsync(
            caller,
            requestId,
            selectedCandidate.Model,
            selectedCandidate.Pricing.Version,
            initialReservation,
            livePolicy.MaxSessionCostCreditMicros,
            _options.MaxRequestsPerMinutePerUser,
            _options.MaxConcurrentRequestsPerUser,
            _options.GlobalDailyCreditMicrosCap,
            cancellationToken,
            route.Name);
        if (!reservation.Created)
            throw new InvalidOperationException("This live-session request ID has already been used.");

        ClientWebSocket? provider = null;
        WebSocket? client = null;
        var usage = new LiveUsageAccumulator(_options.LiveSessionMaxTrafficBytes);
        var providerConnected = false;
        var providerDispatchStarted = false;
        var outcome = "cancelled";
        try
        {
            var accessToken = await _tokens.GetAsync(cancellationToken);
            provider = new ClientWebSocket();
            provider.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            provider.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            provider.Options.SetRequestHeader("Content-Type", "application/json");
            var endpoint = new Uri(
                $"wss://{selectedCandidate.Location}-aiplatform.googleapis.com/ws/" +
                "google.cloud.aiplatform.v1beta1.LlmBidiService/BidiGenerateContent");
            await provider.ConnectAsync(endpoint, cancellationToken);
            providerConnected = true;

            client = await context.WebSockets.AcceptWebSocketAsync();
            var setup = await ReceiveTextAsync(client, MaxMessageBytes, cancellationToken);
            var rewritten = RewriteSetup(
                setup,
                $"projects/{_options.ProjectId}/locations/{selectedCandidate.Location}/" +
                $"publishers/google/models/{selectedCandidate.Model}");
            usage.ObserveClient(rewritten);
            await _store.MarkReservationProviderStartedAsync(
                caller.UserId,
                requestId,
                cancellationToken);
            providerDispatchStarted = true;
            await SendTextAsync(provider, rewritten, cancellationToken);

            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            session.CancelAfter(TimeSpan.FromSeconds(livePolicy.MaxSessionSeconds));
            long reservedCreditMicros = reservation.Reservation.ReservedCreditMicros;
            async Task EnsureBudgetAsync(CancellationToken token)
            {
                var current = usage.Snapshot();
                if (current.TotalTokens <= 0)
                    return;
                var (_, currentCharge) = _catalog.CalculateLiveActualCost(
                    selectedCandidate.Pricing,
                    current,
                    _options.LiveEstimateSafetyBasisPoints);
                if (currentCharge >= livePolicy.MaxSessionCostCreditMicros)
                    throw new InvalidOperationException("The Live session reached its approved server budget.");
                var target = RequiredLiveReservation(
                    currentCharge,
                    livePolicy.ReservationWindowCreditMicros,
                    livePolicy.MaxSessionCostCreditMicros);
                if (target <= Interlocked.Read(ref reservedCreditMicros))
                    return;
                var ensured = await _store.EnsureReservationCapacityAsync(
                    caller.UserId,
                    requestId,
                    target,
                    token);
                Max(ref reservedCreditMicros, ensured);
            }

            var toProvider = RelayAsync(client, provider, async (message, token) =>
            {
                usage.ObserveClient(message);
                await EnsureBudgetAsync(token);
            }, session.Token);
            var activity = new LiveActivityTracker();
            var toClient = RelayAsync(provider, client, async (message, token) =>
            {
                usage.ObserveProvider(message);
                activity.ObserveProvider(message);
                await EnsureBudgetAsync(token);
            }, session.Token);
            var checkpoints = CheckpointUsageAsync(
                caller.UserId,
                requestId,
                usage,
                selectedCandidate.Pricing,
                session.Token);
            var idle = WaitForPostTurnIdleAsync(
                activity,
                TimeSpan.FromSeconds(_options.LiveSessionIdleSeconds),
                session.Token);
            await Task.WhenAny(toProvider, toClient, checkpoints, idle);
            session.Cancel();
            await IgnoreExpectedRelayEndAsync(toProvider);
            await IgnoreExpectedRelayEndAsync(toClient);
            await IgnoreExpectedCheckpointEndAsync(checkpoints);
            await IgnoreExpectedCheckpointEndAsync(idle);
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "completed";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
        }
        catch (OperationCanceledException)
        {
            outcome = "timeout";
        }
        catch (Exception exception)
        {
            outcome = "provider_failed";
            _logger.LogWarning(
                exception,
                "Vertex Live session failed for request {RequestHash} after connection={ProviderConnected}",
                Crypto.Sha256(requestId)[..12],
                providerConnected);
            await CloseQuietlyAsync(client, WebSocketCloseStatus.InternalServerError, "Live session ended.");
            if (client is null)
                throw;
        }
        finally
        {
            await CloseQuietlyAsync(provider, WebSocketCloseStatus.NormalClosure, "Session ended.");
            await CloseQuietlyAsync(client, WebSocketCloseStatus.NormalClosure, "Session ended.");
            provider?.Dispose();
            client?.Dispose();

            var measured = usage.Snapshot();
            switch (DetermineAccountingDisposition(providerDispatchStarted, measured.TotalTokens))
            {
            case LiveAccountingDisposition.Settle:
            {
                var (providerCost, charge) = _catalog.CalculateLiveActualCost(
                    selectedCandidate.Pricing,
                    measured,
                    _options.LiveEstimateSafetyBasisPoints);
                try
                {
                    await _store.RecordProviderCompletionAsync(
                        caller.UserId,
                        requestId,
                        measured,
                        charge,
                        providerCost,
                        outcome,
                        CancellationToken.None,
                        selectedCandidate.Model,
                        selectedCandidate.Pricing.Version);
                }
                catch (Exception exception)
                {
                    await _store.MarkReservationForReconciliationAsync(
                        caller.UserId,
                        requestId,
                        "live_provider_completion_evidence_write_failed",
                        CancellationToken.None);
                    throw new InvalidOperationException(
                        "Live usage was incurred but its durable accounting evidence is pending reconciliation.",
                        exception);
                }
                Exception? settlementError = null;
                for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
                {
                    try
                    {
                        await _store.SettleAsync(
                            caller.UserId,
                            requestId,
                            measured,
                            charge,
                            providerCost,
                            outcome,
                            CancellationToken.None);
                        settlementError = null;
                        break;
                    }
                    catch (Exception exception)
                    {
                        settlementError = exception;
                        if (attempt < _options.MaxRetries)
                            await Task.Delay(TimeSpan.FromMilliseconds(150 * (attempt + 1)));
                    }
                }
                if (settlementError is not null)
                    throw new InvalidOperationException(
                        "Live usage was incurred but its accounting settlement is pending reconciliation.",
                        settlementError);
                break;
            }
            case LiveAccountingDisposition.Reconcile:
            {
                // A successful provider connection plus dispatched setup can
                // still be billable even when the Live API supplies no usage
                // metadata (for example when the client disconnects before a
                // model turn).  Do not issue an automatic refund here: mark
                // the reservation for the maintenance resolver, which keeps
                // the ledger auditable and releases it only with proof that
                // no billable provider work occurred.
                await _store.MarkReservationForReconciliationAsync(
                    caller.UserId,
                    requestId,
                    "live_provider_dispatch_without_usage_metadata",
                    CancellationToken.None);
                break;
            }
            default:
            {
                await _store.ReleaseReservationAsync(
                    caller.UserId,
                    requestId,
                    outcome,
                    CancellationToken.None);
                break;
            }
            }
        }
    }

    internal static LiveAccountingDisposition DetermineAccountingDisposition(
        bool providerDispatchStarted,
        long measuredTotalTokens) =>
        measuredTotalTokens > 0
            ? LiveAccountingDisposition.Settle
            : providerDispatchStarted
                ? LiveAccountingDisposition.Reconcile
                : LiveAccountingDisposition.Release;

    internal static long RequiredLiveReservation(
        long currentChargeCreditMicros,
        long windowCreditMicros,
        long sessionCapCreditMicros)
    {
        if (currentChargeCreditMicros < 0
            || windowCreditMicros <= 0
            || sessionCapCreditMicros <= 0)
            throw new ArgumentOutOfRangeException(nameof(currentChargeCreditMicros));
        return Math.Min(
            sessionCapCreditMicros,
            checked(currentChargeCreditMicros + windowCreditMicros));
    }

    internal static string RewriteSetup(string json, string model)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
            throw new ArgumentException("The Live setup is too large.");
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new ArgumentException("The Live setup is invalid.");
        }
        catch (JsonException)
        {
            throw new ArgumentException("The Live setup is invalid.");
        }
        if (root.Count != 1 || root["setup"] is not JsonObject setup)
            throw new ArgumentException("The first Live message must be setup.");

        var voiceName = ReadVoiceName(setup);
        var clientInstruction = ReadSystemInstruction(setup);
        var tools = ValidateAndCloneTools(setup["tools"]);
        var rewritten = new JsonObject
        {
            ["setup"] = new JsonObject
            {
                ["model"] = model,
                ["generationConfig"] = new JsonObject
                {
                    ["responseModalities"] = new JsonArray("AUDIO"),
                    ["speechConfig"] = new JsonObject
                    {
                        ["voiceConfig"] = new JsonObject
                        {
                            ["prebuiltVoiceConfig"] = new JsonObject
                            {
                                ["voiceName"] = voiceName,
                            },
                        },
                    },
                },
                ["tools"] = tools,
                // Transcripts are needed by the current desktop for turn state
                // and visible feedback. Their presence is controlled here, not
                // by a modified desktop client.
                ["inputAudioTranscription"] = new JsonObject(),
                ["outputAudioTranscription"] = new JsonObject(),
                ["realtimeInputConfig"] = new JsonObject
                {
                    ["automaticActivityDetection"] = new JsonObject
                    {
                        ["disabled"] = false,
                        ["prefixPaddingMs"] = 80,
                        ["silenceDurationMs"] = 550,
                    },
                },
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray(
                        new JsonObject
                        {
                            ["text"] = ServerSafetyInstruction + "\n\nDesktop context:\n" + clientInstruction,
                        }),
                },
            },
        };
        return rewritten.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static string ReadVoiceName(JsonObject setup)
    {
        var voice = setup["generationConfig"]?["speechConfig"]?["voiceConfig"]?
            ["prebuiltVoiceConfig"]?["voiceName"]?.GetValue<string>();
        return voice is not null && AllowedVoiceNames.Contains(voice) ? voice : "Kore";
    }

    private static string ReadSystemInstruction(JsonObject setup)
    {
        var parts = setup["systemInstruction"]?["parts"] as JsonArray;
        var text = parts?.FirstOrDefault()?["text"]?.GetValue<string>() ?? "";
        if (text.Length > MaxSystemInstructionCharacters)
            throw new ArgumentException("The Live system instruction is too large.");
        return text;
    }

    private static JsonArray ValidateAndCloneTools(JsonNode? node)
    {
        if (node is not JsonArray tools || tools.Count == 0 || tools.Count > 4)
            throw new ArgumentException("The Live tool catalog is missing or invalid.");

        var result = new JsonArray();
        var declarationCount = 0;
        foreach (var toolNode in tools)
        {
            if (toolNode is not JsonObject tool
                || tool.Count != 1
                || tool["functionDeclarations"] is not JsonArray declarations)
                throw new ArgumentException("The Live tool catalog contains an unsupported tool type.");
            var clonedDeclarations = new JsonArray();
            foreach (var declarationNode in declarations)
            {
                declarationCount++;
                if (declarationCount > MaxToolDeclarations
                    || declarationNode is not JsonObject declaration
                    || declaration["name"] is not JsonValue nameValue
                    || !nameValue.TryGetValue<string>(out var name)
                    || !AllowedToolNames.Contains(name))
                    throw new ArgumentException("The Live tool catalog contains an unapproved function.");
                clonedDeclarations.Add(declaration.DeepClone());
            }
            result.Add(new JsonObject { ["functionDeclarations"] = clonedDeclarations });
        }
        return result;
    }

    private static async Task RelayAsync(
        WebSocket source,
        WebSocket destination,
        Func<string, CancellationToken, Task> observe,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested
               && source.State == WebSocketState.Open
               && destination.State == WebSocketState.Open)
        {
            var message = await ReceiveTextAsync(source, MaxMessageBytes, cancellationToken);
            await observe(message, cancellationToken);
            await SendTextAsync(destination, message, cancellationToken);
        }
    }

    private static async Task<string> ReceiveTextAsync(
        WebSocket socket,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new OperationCanceledException("The peer closed the Live session.", cancellationToken);
            if (result.MessageType != WebSocketMessageType.Text)
                throw new ArgumentException("Only JSON text WebSocket messages are supported.");
            if (stream.Length + result.Count > maxBytes)
                throw new ArgumentException("A Live message exceeded the size limit.");
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static Task SendTextAsync(
        WebSocket socket,
        string message,
        CancellationToken cancellationToken) =>
        socket.SendAsync(
            Encoding.UTF8.GetBytes(message),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

    private static async Task IgnoreExpectedRelayEndAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private static async Task IgnoreExpectedCheckpointEndAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private async Task CheckpointUsageAsync(
        string userId,
        string requestId,
        LiveUsageAccumulator usage,
        PricingSnapshot pricing,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.LiveUsageCheckpointSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var measured = usage.Snapshot();
            if (measured.TotalTokens <= 0)
                continue;
            var (providerCost, charge) = _catalog.CalculateLiveActualCost(
                pricing,
                measured,
                _options.LiveEstimateSafetyBasisPoints);
            await _store.CheckpointReservationUsageAsync(
                userId,
                requestId,
                measured,
                charge,
                providerCost,
                cancellationToken);
        }
    }

    private static async Task WaitForPostTurnIdleAsync(
        LiveActivityTracker activity,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var completedAt = activity.LastCompletedTurnAt;
            if (completedAt is not null && DateTimeOffset.UtcNow - completedAt >= idleTimeout)
                return;
        }
    }

    private static async Task CloseQuietlyAsync(
        WebSocket? socket,
        WebSocketCloseStatus status,
        string description)
    {
        if (socket is null)
            return;
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(status, description, CancellationToken.None);
        }
        catch { }
    }

    private static bool IsSafeRequestId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length is >= 16 and <= 120
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static void Max(ref long target, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref target);
            if (value <= current)
                return;
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}

internal enum LiveAccountingDisposition
{
    Settle,
    Reconcile,
    Release,
}

internal sealed class LiveActivityTracker
{
    private long _completedTurnUnixMilliseconds;

    public DateTimeOffset? LastCompletedTurnAt
    {
        get
        {
            var value = Interlocked.Read(ref _completedTurnUnixMilliseconds);
            return value <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }

    public void ObserveProvider(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("serverContent", out var server))
                return;

            // A transcript or model turn means the conversation is active
            // again. Raw microphone packets are deliberately ignored because
            // they continue while the user is silent.
            if (server.TryGetProperty("inputTranscription", out var transcription)
                && transcription.TryGetProperty("text", out var text)
                && !string.IsNullOrWhiteSpace(text.GetString()))
                Interlocked.Exchange(ref _completedTurnUnixMilliseconds, 0);
            if (server.TryGetProperty("modelTurn", out _))
                Interlocked.Exchange(ref _completedTurnUnixMilliseconds, 0);
            if (server.TryGetProperty("turnComplete", out var complete)
                && complete.ValueKind == JsonValueKind.True)
                Interlocked.Exchange(
                    ref _completedTurnUnixMilliseconds,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        catch (JsonException)
        {
            // Relay validation/accounting handles malformed provider frames;
            // idle tracking must never make the WebSocket loop less reliable.
        }
    }
}

internal sealed class LiveUsageAccumulator
{
    private readonly long _maxTrafficBytes;
    private long _trafficBytes;
    private long _inputAudioBytes;
    private long _outputAudioBytes;
    private long _inputTextCharacters;
    private long _outputTextCharacters;
    private long _promptTokens;
    private long _candidateTokens;
    private long _totalTokens;
    private long _reasoningTokens;
    private long _inputAudioTokens;
    private long _outputAudioTokens;
    private long _inputImageTokens;
    private long _outputImageTokens;
    private int _usageMetadataSeen;
    private readonly object _metadataLock = new();
    private string? _providerRequestId;
    private string? _providerModelVersion;

    public LiveUsageAccumulator(long maxTrafficBytes) =>
        _maxTrafficBytes = maxTrafficBytes;

    public void ObserveClient(string json)
    {
        AddTraffic(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("realtimeInput", out var realtime)
                && realtime.TryGetProperty("audio", out var audio)
                && audio.TryGetProperty("data", out var data))
                Interlocked.Add(ref _inputAudioBytes, Base64ByteCount(data.GetString()));
            Interlocked.Add(ref _inputTextCharacters, CountTextCharacters(root));
        }
        catch (JsonException) { }
    }

    public void ObserveProvider(string json)
    {
        AddTraffic(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("usageMetadata", out var usage))
            {
                Interlocked.Exchange(ref _usageMetadataSeen, 1);
                Max(ref _promptTokens, Number(usage, "promptTokenCount"));
                Max(ref _candidateTokens, Number(usage, "candidatesTokenCount"));
                Max(ref _totalTokens, Number(usage, "totalTokenCount"));
                Max(ref _reasoningTokens, Number(usage, "thoughtsTokenCount"));
                Max(ref _inputAudioTokens, Modality(usage, "promptTokensDetails", "AUDIO"));
                Max(ref _outputAudioTokens, Modality(usage, "candidatesTokensDetails", "AUDIO"));
                Max(ref _inputImageTokens, Modality(usage, "promptTokensDetails", "IMAGE"));
                Max(ref _outputImageTokens, Modality(usage, "candidatesTokensDetails", "IMAGE"));
            }
            lock (_metadataLock)
            {
                if (root.TryGetProperty("responseId", out var responseId))
                    _providerRequestId = responseId.GetString() ?? _providerRequestId;
                if (root.TryGetProperty("modelVersion", out var modelVersion))
                    _providerModelVersion = modelVersion.GetString() ?? _providerModelVersion;
            }
            if (root.TryGetProperty("serverContent", out var server)
                && server.TryGetProperty("modelTurn", out var turn)
                && turn.TryGetProperty("parts", out var parts))
            {
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("inlineData", out var inlineData)
                        && inlineData.TryGetProperty("data", out var data))
                        Interlocked.Add(ref _outputAudioBytes, Base64ByteCount(data.GetString()));
                }
            }
            Interlocked.Add(ref _outputTextCharacters, CountTextCharacters(root));
        }
        catch (JsonException) { }
    }

    public ProviderUsage Snapshot()
    {
        // Vertex Live does not currently provide countTokens. When cumulative
        // usage metadata is absent, estimate 25 native-audio tokens per second
        // from 16 kHz input and 24 kHz output PCM byte counts, conservatively
        // folding those tokens into the billable input/output categories.
        var estimatedInputAudio = DivideRoundUp(Interlocked.Read(ref _inputAudioBytes), 1_280);
        var estimatedOutputAudio = DivideRoundUp(Interlocked.Read(ref _outputAudioBytes), 1_920);
        var estimatedText = DivideRoundUp(Interlocked.Read(ref _inputTextCharacters), 4);
        var estimatedOutputText = DivideRoundUp(Interlocked.Read(ref _outputTextCharacters), 4);
        var exactMetadata = Volatile.Read(ref _usageMetadataSeen) == 1;
        var providerInput = Interlocked.Read(ref _promptTokens);
        var providerOutput = Interlocked.Read(ref _candidateTokens);
        // Vertex documents Live usageMetadata as session-to-date. The high-water
        // marks above therefore reject duplicates and out-of-order snapshots.
        // Once metadata exists, provider token totals are authoritative; local
        // byte estimates classify missing modalities but never inflate totals.
        var input = exactMetadata
            ? providerInput
            : estimatedText + estimatedInputAudio;
        var output = exactMetadata
            ? providerOutput
            : estimatedOutputText + estimatedOutputAudio;
        var total = exactMetadata
            ? Math.Max(Interlocked.Read(ref _totalTokens), input + output)
            : input + output;
        var providerInputAudio = Interlocked.Read(ref _inputAudioTokens);
        var providerOutputAudio = Interlocked.Read(ref _outputAudioTokens);
        var inputAudio = Math.Clamp(
            providerInputAudio > 0 ? providerInputAudio : estimatedInputAudio,
            0,
            input);
        var outputAudio = Math.Clamp(
            providerOutputAudio > 0 ? providerOutputAudio : estimatedOutputAudio,
            0,
            output);
        var inputImage = Math.Clamp(Interlocked.Read(ref _inputImageTokens), 0, input - inputAudio);
        var outputImage = Math.Clamp(Interlocked.Read(ref _outputImageTokens), 0, output - outputAudio);
        var estimated = !exactMetadata
            || (input > 0 && providerInputAudio == 0 && estimatedInputAudio > 0)
            || (output > 0 && providerOutputAudio == 0 && estimatedOutputAudio > 0);
        string? requestId;
        string? modelVersion;
        lock (_metadataLock)
        {
            requestId = _providerRequestId;
            modelVersion = _providerModelVersion;
        }
        return new ProviderUsage(
            input,
            output,
            0,
            Interlocked.Read(ref _reasoningTokens),
            inputImage + outputImage,
            inputAudio + outputAudio,
            total,
            requestId,
            modelVersion,
            inputAudio,
            outputAudio,
            inputImage,
            outputImage,
            estimated);
    }

    private void AddTraffic(string json)
    {
        var total = Interlocked.Add(ref _trafficBytes, Encoding.UTF8.GetByteCount(json));
        if (total > _maxTrafficBytes)
            throw new InvalidOperationException("The Live session traffic limit was reached.");
    }

    private static long Base64ByteCount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        var padding = value.EndsWith("==", StringComparison.Ordinal) ? 2
            : value.EndsWith('=') ? 1 : 0;
        return Math.Max(0, value.Length * 3L / 4L - padding);
    }

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static long Modality(JsonElement element, string property, string modality)
    {
        if (!element.TryGetProperty(property, out var details)
            || details.ValueKind != JsonValueKind.Array)
            return 0;
        long result = 0;
        foreach (var detail in details.EnumerateArray())
        {
            if (detail.TryGetProperty("modality", out var kind)
                && string.Equals(kind.GetString(), modality, StringComparison.OrdinalIgnoreCase)
                && detail.TryGetProperty("tokenCount", out var count)
                && count.TryGetInt64(out var tokens))
                result += tokens;
        }
        return result;
    }

    private static long CountTextCharacters(JsonElement element)
    {
        long total = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "text", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                    total += property.Value.GetString()?.Length ?? 0;
                else
                    total += CountTextCharacters(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                total += CountTextCharacters(item);
        }
        return total;
    }

    private static long DivideRoundUp(long value, long divisor) =>
        value <= 0 ? 0 : (value + divisor - 1) / divisor;

    private static void Max(ref long target, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref target);
            if (value <= current)
                return;
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
