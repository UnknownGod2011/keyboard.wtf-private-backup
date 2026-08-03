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
    private readonly IAccountStore _store;
    private readonly ICatalogService _catalog;
    private readonly IVertexAccessTokenProvider _tokens;
    private readonly PaidSystemOptions _options;
    private readonly ILogger<VertexLiveProxyService> _logger;

    public VertexLiveProxyService(
        IAccountStore store,
        ICatalogService catalog,
        IVertexAccessTokenProvider tokens,
        IOptions<PaidSystemOptions> options,
        ILogger<VertexLiveProxyService> logger)
    {
        _store = store;
        _catalog = catalog;
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
        if (catalog.KillSwitch)
            throw new InvalidOperationException(catalog.KillSwitchMessage ?? "Paid AI is temporarily unavailable.");
        await _store.EnsureAccountAsync(caller, cancellationToken);
        var reservation = await _store.ReserveAsync(
            caller,
            requestId,
            _options.VertexLiveModel,
            catalog.Pricing.Version,
            _options.LiveSessionCreditMicrosCap,
            _options.LiveSessionCreditMicrosCap,
            _options.MaxRequestsPerMinutePerUser,
            _options.MaxConcurrentRequestsPerUser,
            _options.GlobalDailyCreditMicrosCap,
            cancellationToken);
        if (!reservation.Created)
            throw new InvalidOperationException("This live-session request ID has already been used.");

        ClientWebSocket? provider = null;
        WebSocket? client = null;
        var usage = new LiveUsageAccumulator(_options.LiveSessionMaxTrafficBytes);
        var providerConnected = false;
        var outcome = "cancelled";
        try
        {
            await _store.MarkReservationProviderStartedAsync(
                caller.UserId,
                requestId,
                cancellationToken);
            var accessToken = await _tokens.GetAsync(cancellationToken);
            provider = new ClientWebSocket();
            provider.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            provider.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
            provider.Options.SetRequestHeader("Content-Type", "application/json");
            var endpoint = new Uri(
                $"wss://{_options.VertexLiveRegion}-aiplatform.googleapis.com/ws/" +
                "google.cloud.aiplatform.v1beta1.LlmBidiService/BidiGenerateContent");
            await provider.ConnectAsync(endpoint, cancellationToken);
            providerConnected = true;

            client = await context.WebSockets.AcceptWebSocketAsync();
            var setup = await ReceiveTextAsync(client, MaxMessageBytes, cancellationToken);
            var rewritten = RewriteSetup(
                setup,
                $"projects/{_options.ProjectId}/locations/{_options.VertexLiveRegion}/" +
                $"publishers/google/models/{_options.VertexLiveModel}");
            usage.ObserveClient(rewritten);
            await SendTextAsync(provider, rewritten, cancellationToken);

            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            session.CancelAfter(TimeSpan.FromSeconds(_options.LiveSessionMaxSeconds));
            var toProvider = RelayAsync(client, provider, usage.ObserveClient, session.Token);
            var toClient = RelayAsync(provider, client, usage.ObserveProvider, session.Token);
            await Task.WhenAny(toProvider, toClient);
            session.Cancel();
            await IgnoreExpectedRelayEndAsync(toProvider);
            await IgnoreExpectedRelayEndAsync(toClient);
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
                "Vertex Live session failed for request {RequestId} after connection={ProviderConnected}",
                requestId,
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
            if (providerConnected && measured.TotalTokens > 0)
            {
                var (providerCost, charge) = _catalog.CalculateActualCost(catalog.Pricing, measured);
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
            }
            else
            {
                await _store.ReleaseReservationAsync(
                    caller.UserId,
                    requestId,
                    outcome,
                    CancellationToken.None);
            }
        }
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
        if (root["setup"] is not JsonObject setup)
            throw new ArgumentException("The first Live message must be setup.");
        setup["model"] = model;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static async Task RelayAsync(
        WebSocket source,
        WebSocket destination,
        Action<string> observe,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested
               && source.State == WebSocketState.Open
               && destination.State == WebSocketState.Open)
        {
            var message = await ReceiveTextAsync(source, MaxMessageBytes, cancellationToken);
            observe(message);
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
            if (root.TryGetProperty("clientContent", out var content)
                && content.TryGetProperty("turns", out var turns))
            {
                foreach (var turn in turns.EnumerateArray())
                {
                    if (!turn.TryGetProperty("parts", out var parts))
                        continue;
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var text))
                            Interlocked.Add(ref _inputTextCharacters, text.GetString()?.Length ?? 0);
                    }
                }
            }
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
                Max(ref _promptTokens, Number(usage, "promptTokenCount"));
                Max(ref _candidateTokens, Number(usage, "candidatesTokenCount"));
                Max(ref _totalTokens, Number(usage, "totalTokenCount"));
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
                    if (part.TryGetProperty("text", out var text))
                        Interlocked.Add(ref _outputTextCharacters, text.GetString()?.Length ?? 0);
                }
            }
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
        var input = Math.Max(Interlocked.Read(ref _promptTokens), estimatedText + estimatedInputAudio);
        var output = Math.Max(
            Interlocked.Read(ref _candidateTokens),
            estimatedOutputText + estimatedOutputAudio);
        var total = Math.Max(Interlocked.Read(ref _totalTokens), input + output);
        return new ProviderUsage(
            input,
            output,
            0,
            0,
            0,
            estimatedInputAudio + estimatedOutputAudio,
            total,
            null);
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
