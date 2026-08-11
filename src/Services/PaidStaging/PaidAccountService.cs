namespace KeyboardWtf.Services.PaidStaging;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record PaidAccountSnapshot(
    string UserId,
    string Email,
    string DisplayName,
    string Status,
    string PlanId,
    long AvailableCreditMicros,
    long ReservedCreditMicros,
    long CycleUsageCreditMicros,
    DateTimeOffset? CycleEndsAt,
    IReadOnlyList<PaidDeviceSnapshot> Devices,
    IReadOnlyList<PaidUsageSnapshot> RecentUsage,
    IReadOnlyList<PaidPurchaseSnapshot> RecentPurchases,
    string SubscriptionStatus = "none");

internal sealed record PaidDeviceSnapshot(
    string DeviceId,
    string Name,
    string Status,
    DateTimeOffset LastSeenAt,
    DateTimeOffset CreatedAt);

internal sealed record PaidUsageSnapshot(
    string RequestId,
    string Model,
    string Outcome,
    long ChargedCreditMicros,
    long InputTokens,
    long OutputTokens,
    DateTimeOffset CreatedAt);

internal sealed record PaidPurchaseSnapshot(
    string PurchaseId,
    string ProductSku,
    string Provider,
    string Status,
    long AmountMinor,
    string Currency,
    DateTimeOffset CreatedAt);

internal sealed record PaidGenerateResponse(
    string RequestId,
    string Text,
    string Model,
    long ChargedCreditMicros,
    long RemainingCreditMicros,
    JsonElement Usage,
    bool Verified);

internal sealed record PaidInlineImage(string MimeType, string Data);

internal sealed class PaidAccountService : IDisposable
{
    private const string DesktopVersionHeader = "X-Keyboard-Wtf-Desktop-Version";
    private static readonly string DesktopVersion =
        (typeof(PaidAccountService).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly PaidStagingConfiguration _configuration;
    private readonly PaidSessionStore _sessionStore;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private PaidSessionTokens _tokens;
    private PaidAccountSnapshot _account;
    private string _lastError = "";
    private bool _disposed;

    public PaidAccountService(
        PaidStagingConfiguration configuration = null,
        PaidSessionStore sessionStore = null,
        HttpMessageHandler handler = null)
    {
        _configuration = configuration ?? PaidStagingConfiguration.FromEnvironment();
        _sessionStore = sessionStore ?? new PaidSessionStore();
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(40);
    }

    public bool Enabled => _configuration.Enabled;
    public bool EmergencyByokEnabled => _configuration.EmergencyByokEnabled;
    public bool IsSignedIn => _tokens is not null && _tokens.RefreshTokenExpiresAt > DateTimeOffset.UtcNow;
    public PaidAccountSnapshot Account => _account;
    public string LastError => _lastError;
    public Uri WebBaseUri => _configuration.WebBaseUri;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return;
        _tokens = _sessionStore.Load();
        if (_tokens is null)
            return;
        try
        {
            await RefreshAccountAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _lastError = UserError(exception);
            AppLog.Warning(exception, "Paid account startup refresh failed");
        }
    }

    public async Task<PaidAccountSnapshot> SignInAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        await _authGate.WaitAsync(cancellationToken);
        try
        {
            var state = RandomToken(32);
            var nonce = RandomToken(32);
            var verifier = RandomToken(48);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var deviceInstallationId = _tokens?.DeviceInstallationId ?? $"win_{RandomToken(24)}";
            var port = ReserveLoopbackPort();
            var callback = $"http://127.0.0.1:{port}/keyboard-wtf/callback";
            var start = await SendAsync<LoginStartResponse>(
                HttpMethod.Post,
                "v1/desktop-login/start",
                new { state, deviceNonce = nonce, pkceChallenge = challenge, callbackUri = callback },
                authenticated: false,
                cancellationToken);

            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            Process.Start(new ProcessStartInfo { FileName = start.BrowserUrl, UseShellExecute = true });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            var request = context.Request;
            var callbackState = request.QueryString["state"];
            var callbackAttempt = request.QueryString["attempt"];
            var authorizationCode = request.QueryString["code"];
            var valid = string.Equals(request.Url?.AbsolutePath, "/keyboard-wtf/callback", StringComparison.Ordinal)
                && string.Equals(callbackState, state, StringComparison.Ordinal)
                && string.Equals(callbackAttempt, start.AttemptId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(authorizationCode);
            await WriteCallbackResponseAsync(context.Response, valid, timeout.Token);
            if (!valid)
                throw new UnauthorizedAccessException("The desktop sign-in callback did not match this request.");

            var exchanged = await SendAsync<SessionTokenResponse>(
                HttpMethod.Post,
                "v1/desktop-login/exchange",
                new
                {
                    attemptId = start.AttemptId,
                    state,
                    deviceNonce = nonce,
                    authorizationCode,
                    pkceVerifier = verifier,
                    deviceName = Environment.MachineName,
                    deviceInstallationId,
                },
                authenticated: false,
                cancellationToken);
            SaveTokens(exchanged, deviceInstallationId);
            return await RefreshAccountAsync(cancellationToken);
        }
        finally
        {
            _authGate.Release();
        }
    }

    public async Task<PaidAccountSnapshot> RefreshAccountAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        _account = await SendAuthorizedAsync<PaidAccountSnapshot>(
            HttpMethod.Get,
            "v1/account",
            null,
            cancellationToken);
        _lastError = "";
        return _account;
    }

    public async Task WarmBackendAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled || !IsSignedIn)
            return;
        try
        {
            await SendAuthorizedAsync<JsonElement>(
                HttpMethod.Get,
                "v1/ready",
                null,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Warmup is latency optimization only. The real paid request still
            // authenticates and fails honestly through its normal path.
            AppLog.Warning(exception, "Paid backend warmup failed");
        }
    }

    public async Task SignOutAsync(bool allDevices = false, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            return;
        try
        {
            if (_tokens is not null)
            {
                await SendAuthorizedAsync<JsonElement>(
                    HttpMethod.Post,
                    "v1/sessions/logout",
                    new { sessionId = _tokens.SessionId, allDevices },
                    cancellationToken);
            }
        }
        finally
        {
            _tokens = null;
            _account = null;
            _sessionStore.Clear();
        }
    }

    public void OpenManageAccount() => OpenWeb("account.html");
    public void OpenBilling() => OpenWeb("account.html#billing");

    public async Task<PaidGenerateResponse> GenerateAsync(
        string prompt,
        string systemInstruction,
        int maxOutputTokens,
        object responseJsonSchema,
        IReadOnlyList<PaidInlineImage> images,
        string taskKind,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await EnsureUsableCreditsAsync(cancellationToken);
        var requestId = $"desktop_{Guid.NewGuid():N}";
        var response = await SendAuthorizedAsync<PaidGenerateResponse>(
            HttpMethod.Post,
            "v1/ai/generate",
            new
            {
                requestId,
                prompt,
                systemInstruction,
                maxOutputTokens,
                taskKind,
                responseJsonSchema,
                images,
            },
            cancellationToken);
        if (_account is not null)
        {
            _account = _account with
            {
                AvailableCreditMicros = response.RemainingCreditMicros,
                CycleUsageCreditMicros = checked(
                    _account.CycleUsageCreditMicros + response.ChargedCreditMicros),
            };
        }
        return response;
    }

    public async Task<ClientWebSocket> ConnectLiveAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        await EnsureUsableCreditsAsync(cancellationToken);
        var accessToken = await EnsureAccessTokenAsync(cancellationToken);
        var requestId = $"desktop_live_{Guid.NewGuid():N}";
        var builder = new UriBuilder(new Uri(_configuration.BackendBaseUri, "v1/ai/live"))
        {
            Scheme = _configuration.BackendBaseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Query = $"requestId={Uri.EscapeDataString(requestId)}",
        };
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        socket.Options.SetRequestHeader(DesktopVersionHeader, DesktopVersion);
        try
        {
            await socket.ConnectAsync(builder.Uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task EnsureUsableCreditsAsync(CancellationToken cancellationToken)
    {
        if (!IsSignedIn)
            throw new InvalidOperationException("Sign in with Google in Settings before using paid AI.");
        if (_account is null || _account.AvailableCreditMicros <= 0)
            await RefreshAccountAsync(cancellationToken);
        if (_account?.AvailableCreditMicros <= 0)
            throw new InvalidOperationException("This account has no usable credits. Open Account & Billing to add credits.");
    }

    private async Task<T> SendAuthorizedAsync<T>(
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        var accessToken = await EnsureAccessTokenAsync(cancellationToken);
        try
        {
            return await SendAsync<T>(method, path, body, true, cancellationToken, accessToken);
        }
        catch (PaidApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            accessToken = await RefreshTokensAsync(cancellationToken);
            return await SendAsync<T>(method, path, body, true, cancellationToken, accessToken);
        }
    }

    private async Task<string> EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_tokens is null)
            throw new InvalidOperationException("Sign in with Google in Settings first.");
        if (_tokens.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return _tokens.AccessToken;
        return await RefreshTokensAsync(cancellationToken);
    }

    private async Task<string> RefreshTokensAsync(CancellationToken cancellationToken)
    {
        await _authGate.WaitAsync(cancellationToken);
        try
        {
            if (_tokens is null || _tokens.RefreshTokenExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("The desktop session expired. Sign in again.");
            if (_tokens.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return _tokens.AccessToken;
            var refreshed = await SendAsync<SessionTokenResponse>(
                HttpMethod.Post,
                "v1/sessions/refresh",
                new { refreshToken = _tokens.RefreshToken },
                authenticated: false,
                cancellationToken);
            SaveTokens(refreshed, _tokens.DeviceInstallationId);
            return _tokens.AccessToken;
        }
        catch
        {
            _tokens = null;
            _account = null;
            _sessionStore.Clear();
            throw;
        }
        finally
        {
            _authGate.Release();
        }
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object body,
        bool authenticated,
        CancellationToken cancellationToken,
        string accessToken = null)
    {
        EnsureEnabled();
        using var request = new HttpRequestMessage(method, new Uri(_configuration.BackendBaseUri, path));
        request.Headers.TryAddWithoutValidation(DesktopVersionHeader, DesktopVersion);
        if (authenticated)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new PaidApiException(response.StatusCode, ErrorMessage(text, response.StatusCode));
        if (typeof(T) == typeof(JsonElement) && string.IsNullOrWhiteSpace(text))
            return (T)(object)default(JsonElement);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)
            ?? throw new InvalidDataException("The paid service returned an empty response.");
    }

    private void SaveTokens(SessionTokenResponse response, string deviceInstallationId)
    {
        _tokens = new PaidSessionTokens(
            response.AccessToken,
            response.AccessTokenExpiresAt,
            response.RefreshToken,
            response.RefreshTokenExpiresAt,
            response.SessionId,
            response.DeviceId,
            deviceInstallationId);
        _sessionStore.Save(_tokens);
    }

    private void OpenWeb(string relative)
    {
        EnsureEnabled();
        Process.Start(new ProcessStartInfo
        {
            FileName = new Uri(_configuration.WebBaseUri, relative).ToString(),
            UseShellExecute = true,
        });
    }

    private void EnsureEnabled()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enabled)
            throw new InvalidOperationException("Paid staging is not enabled for this build.");
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WriteCallbackResponseAsync(
        HttpListenerResponse response,
        bool success,
        CancellationToken cancellationToken)
    {
        var html = success
            ? "<!doctype html><meta charset=utf-8><title>Jarvis connected</title><h1>Jarvis connected</h1><p>You can close this tab and return to keyboard.wtf.</p>"
            : "<!doctype html><meta charset=utf-8><title>Connection rejected</title><h1>Connection rejected</h1><p>Return to keyboard.wtf and try again.</p>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = success ? 200 : 400;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }

    private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ErrorMessage(string text, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.TryGetProperty("detail", out var detail) && !string.IsNullOrWhiteSpace(detail.GetString()))
                return detail.GetString()!;
            if (root.TryGetProperty("title", out var title) && !string.IsNullOrWhiteSpace(title.GetString()))
                return title.GetString()!;
        }
        catch (JsonException) { }
        return $"The paid service returned {(int)status}.";
    }

    private static string UserError(Exception exception) =>
        exception is PaidApiException or InvalidOperationException
            ? exception.Message
            : "The paid service is temporarily unavailable.";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _authGate.Dispose();
        _http.Dispose();
    }

    private sealed record LoginStartResponse(string AttemptId, string BrowserUrl, DateTimeOffset ExpiresAt);
    private sealed record SessionTokenResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt,
        string SessionId,
        string DeviceId);
}

internal sealed class PaidApiException : Exception
{
    public PaidApiException(HttpStatusCode statusCode, string message) : base(message) =>
        StatusCode = statusCode;
    public HttpStatusCode StatusCode { get; }
}
