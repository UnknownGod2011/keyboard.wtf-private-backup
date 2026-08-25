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
    string SubscriptionStatus = "none",
    long SubscriptionAllowanceCreditMicros = 0,
    long TopUpCreditMicros = 0,
    long OtherAllowanceCreditMicros = 0,
    string AllowanceRulesVersion = "allowance-v1",
    long SubscriptionOriginalCreditMicros = 0,
    long SubscriptionReservedCreditMicros = 0,
    long SubscriptionConsumedCreditMicros = 0,
    string SubscriptionCancellationStatus = "none",
    DateTimeOffset? SubscriptionCancelsAt = null,
    string PlanDisplayName = "No active plan",
    long PlanAmountMinor = 0,
    string PlanCurrency = "INR");

internal sealed record PaidDeviceSnapshot(
    string DeviceId,
    string Name,
    string Status,
    DateTimeOffset LastSeenAt,
    DateTimeOffset CreatedAt,
    string Platform = "Windows",
    bool IsCurrent = false);

internal sealed record PaidUsageSnapshot(
    string RequestId,
    string Model,
    string Outcome,
    long ChargedCreditMicros,
    long InputTokens,
    long OutputTokens,
    DateTimeOffset CreatedAt,
    string ActivityType = "ai_request",
    string PricingVersion = "",
    long CachedInputTokens = 0,
    long ReasoningTokens = 0,
    long InputAudioTokens = 0,
    long OutputAudioTokens = 0,
    long InputImageTokens = 0,
    long OutputImageTokens = 0,
    bool UsageEstimated = false,
    string ProviderModelVersion = "",
    string AccountingStatus = "settled");

internal sealed record PaidPurchaseSnapshot(
    string PurchaseId,
    string ProductSku,
    string Provider,
    string Status,
    long AmountMinor,
    string Currency,
    DateTimeOffset CreatedAt,
    bool Fulfilled = false,
    string DisplayName = "Subscription");

internal sealed record PaidGenerateResponse(
    string RequestId,
    string Text,
    string Model,
    long ChargedCreditMicros,
    long RemainingCreditMicros,
    JsonElement Usage,
    bool Verified);

internal sealed record PaidInlineImage(string MimeType, string Data);
internal sealed record PaidInlineAudio(string MimeType, string Data);

internal sealed class PaidAccountService : IDisposable
{
    internal static readonly IReadOnlyList<TimeSpan> LiveSlotConflictRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(12),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(20),
    ];
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
    private readonly Action<string> _browserLauncher;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly object _signInSync = new();
    private PaidSessionTokens _tokens;
    private PaidAccountSnapshot _account;
    private Task<PaidAccountSnapshot> _activeSignInTask;
    private CancellationTokenSource _activeSignInCancellation;
    private string _signInState = "idle";
    private DateTimeOffset? _signInExpiresAt;
    private string _lastError = "";
    private bool _disposed;

    public PaidAccountService(
        PaidStagingConfiguration configuration = null,
        PaidSessionStore sessionStore = null,
        HttpMessageHandler handler = null,
        Action<string> browserLauncher = null)
    {
        _configuration = configuration ?? PaidStagingConfiguration.FromEnvironment();
        _sessionStore = sessionStore ?? new PaidSessionStore(Path.Combine(
            SettingsService.AppDataDir,
            _configuration.IsProduction
                ? "paid-production-session.dat"
                : "paid-staging-session.dat"));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = TimeSpan.FromSeconds(40);
        _browserLauncher = browserLauncher ?? (url => Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        }));
    }

    public bool Enabled => _configuration.Enabled;
    public bool EmergencyByokEnabled => _configuration.EmergencyByokEnabled;
    public bool PreserveExistingRegistrations => _configuration.PreserveExistingRegistrations;
    public bool IsSignedIn => _tokens is not null && _tokens.RefreshTokenExpiresAt > DateTimeOffset.UtcNow;
    public bool IsAccountLoading => IsSignedIn && _account is null;
    public PaidAccountSnapshot Account => _account;
    public string LastError => _lastError;
    public Uri WebBaseUri => _configuration.WebBaseUri;
    public string SignInState
    {
        get { lock (_signInSync) return _signInState; }
    }
    public DateTimeOffset? SignInExpiresAt
    {
        get { lock (_signInSync) return _signInExpiresAt; }
    }
    public bool SignInPending
    {
        get
        {
            lock (_signInSync)
                return _activeSignInTask is { IsCompleted: false };
        }
    }

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

    public Task<PaidAccountSnapshot> SignInAsync(CancellationToken cancellationToken = default) =>
        StartSignInAsync(allowTerminalRetry: false, forceAccountChooser: false, cancellationToken);

    public Task<PaidAccountSnapshot> RetrySignInAsync(CancellationToken cancellationToken = default) =>
        StartSignInAsync(allowTerminalRetry: true, forceAccountChooser: false, cancellationToken);

    public async Task<PaidAccountSnapshot> SwitchAccountAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        await SignOutAsync(allDevices: false, cancellationToken);
        return await StartSignInAsync(
            allowTerminalRetry: true,
            forceAccountChooser: true,
            cancellationToken);
    }

    private Task<PaidAccountSnapshot> StartSignInAsync(
        bool allowTerminalRetry,
        bool forceAccountChooser,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (IsSignedIn)
            return RefreshAccountAsync(cancellationToken);

        Task<PaidAccountSnapshot> task;
        lock (_signInSync)
        {
            if (_activeSignInTask is { IsCompleted: false })
                task = _activeSignInTask;
            else if (_signInState is "cancelled" or "expired" or "failed" && !allowTerminalRetry)
                task = Task.FromException<PaidAccountSnapshot>(new InvalidOperationException(
                    "The previous sign-in has ended. Use Try again to start one fresh request."));
            else
            {
                _activeSignInCancellation?.Dispose();
                _activeSignInCancellation = new CancellationTokenSource();
                _signInState = "starting";
                _signInExpiresAt = null;
                _lastError = "";
                task = _activeSignInTask = RunSignInAsync(
                    forceAccountChooser,
                    _activeSignInCancellation.Token);
            }
        }
        return WaitForSharedSignInAsync(task, cancellationToken);
    }

    public bool CancelSignIn()
    {
        lock (_signInSync)
        {
            if (_activeSignInTask is not { IsCompleted: false })
                return false;
            _signInState = "cancelling";
            _activeSignInCancellation?.Cancel();
            return true;
        }
    }

    public async Task<bool> CancelSignInAsync()
    {
        Task<PaidAccountSnapshot> task;
        lock (_signInSync)
        {
            if (_activeSignInTask is not { IsCompleted: false })
                return false;
            task = _activeSignInTask;
            _signInState = "cancelling";
            _activeSignInCancellation?.Cancel();
        }
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation is complete only after the shared sign-in
            // task has unwound and can no longer exchange or persist a session.
        }
        return true;
    }

    private async Task<PaidAccountSnapshot> RunSignInAsync(
        bool forceAccountChooser,
        CancellationToken cancellationToken)
    {
        await _authGate.WaitAsync(cancellationToken);
        try
        {
            if (IsSignedIn)
                return await RefreshAccountAsync(cancellationToken);
            var state = RandomToken(32);
            var nonce = RandomToken(32);
            var verifier = RandomToken(48);
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var deviceInstallationId = _tokens?.DeviceInstallationId
                ?? _sessionStore.LoadOrCreateDeviceInstallationId();
            var port = ReserveLoopbackPort();
            var callback = $"http://127.0.0.1:{port}/keyboard-wtf/callback";
            var start = await SendAsync<LoginStartResponse>(
                HttpMethod.Post,
                "v1/desktop-login/start",
                new { state, deviceNonce = nonce, pkceChallenge = challenge, callbackUri = callback },
                authenticated: false,
                cancellationToken);

            lock (_signInSync)
            {
                _signInState = "waiting_for_google";
                _signInExpiresAt = start.ExpiresAt;
            }

            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _browserLauncher(forceAccountChooser
                ? AppendQueryParameter(start.BrowserUrl, "switch", "1")
                : start.BrowserUrl);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remaining = start.ExpiresAt - DateTimeOffset.UtcNow;
            timeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The desktop sign-in request expired. Try again.");
            }
            var request = context.Request;
            var callbackState = request.QueryString["state"];
            var callbackAttempt = request.QueryString["attempt"];
            var authorizationCode = request.QueryString["code"];
            var valid = string.Equals(request.Url?.AbsolutePath, "/keyboard-wtf/callback", StringComparison.Ordinal)
                && string.Equals(callbackState, state, StringComparison.Ordinal)
                && string.Equals(callbackAttempt, start.AttemptId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(authorizationCode);
            if (!valid)
            {
                await WriteCallbackResponseAsync(context.Response, false, _configuration.WebBaseUri, cancellationToken);
                throw new UnauthorizedAccessException("The desktop sign-in callback did not match this request.");
            }

            PaidAccountSnapshot account;
            try
            {
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
                account = await RefreshAccountAsync(cancellationToken);
                await WriteCallbackResponseAsync(context.Response, true, _configuration.WebBaseUri, cancellationToken);
            }
            catch
            {
                await TryWriteCallbackResponseAsync(context.Response, success: false, _configuration.WebBaseUri);
                throw;
            }
            lock (_signInSync)
            {
                _signInState = "completed";
                _signInExpiresAt = null;
            }
            return account;
        }
        catch (OperationCanceledException)
        {
            lock (_signInSync)
            {
                _signInState = "cancelled";
                _signInExpiresAt = null;
            }
            throw;
        }
        catch (TimeoutException exception)
        {
            _lastError = exception.Message;
            lock (_signInSync)
            {
                _signInState = "expired";
                _signInExpiresAt = null;
            }
            throw;
        }
        catch (Exception exception)
        {
            _lastError = UserError(exception);
            lock (_signInSync)
            {
                _signInState = "failed";
                _signInExpiresAt = null;
            }
            throw;
        }
        finally
        {
            _authGate.Release();
        }
    }

    private static async Task<PaidAccountSnapshot> WaitForSharedSignInAsync(
        Task<PaidAccountSnapshot> task,
        CancellationToken cancellationToken) =>
        await task.WaitAsync(cancellationToken);

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
        await CancelSignInAsync();
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
            _lastError = "";
            lock (_signInSync)
            {
                _signInState = "idle";
                _signInExpiresAt = null;
            }
        }
    }

    public async Task RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 128)
            throw new ArgumentException("A valid device is required.", nameof(deviceId));
        if (_account?.Devices.Any(device =>
                device.IsCurrent
                && string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal)) == true)
            throw new InvalidOperationException("Sign out to remove the current device.");

        await SendAuthorizedAsync<JsonElement>(
            HttpMethod.Post,
            "v1/devices/revoke",
            new { deviceId },
            cancellationToken);
        await RefreshAccountAsync(cancellationToken);
    }

    public async Task CancelSubscriptionAtCycleEndAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        await SendAuthorizedAsync<JsonElement>(
            HttpMethod.Post,
            "v1/billing/subscription/cancel",
            new { atCycleEnd = true },
            cancellationToken);
        await RefreshAccountAsync(cancellationToken);
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
        CancellationToken cancellationToken) =>
        await GenerateCoreAsync(
            prompt,
            systemInstruction,
            maxOutputTokens,
            responseJsonSchema,
            images,
            null,
            taskKind,
            cancellationToken);

    public async Task<PaidGenerateResponse> GenerateAudioAsync(
        string prompt,
        string systemInstruction,
        int maxOutputTokens,
        PaidInlineAudio audio,
        string taskKind,
        CancellationToken cancellationToken) =>
        await GenerateCoreAsync(
            prompt,
            systemInstruction,
            maxOutputTokens,
            null,
            null,
            audio,
            taskKind,
            cancellationToken);

    private async Task<PaidGenerateResponse> GenerateCoreAsync(
        string prompt,
        string systemInstruction,
        int maxOutputTokens,
        object responseJsonSchema,
        IReadOnlyList<PaidInlineImage> images,
        PaidInlineAudio audio,
        string taskKind,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var totalStarted = Stopwatch.GetTimestamp();
        var entitlementStarted = Stopwatch.GetTimestamp();
        await EnsureUsableCreditsAsync(cancellationToken);
        var entitlementMs = (long)Stopwatch.GetElapsedTime(entitlementStarted).TotalMilliseconds;
        var requestId = $"desktop_{Guid.NewGuid():N}";
        AppLog.Info(
            $"Paid AI request stage: request={requestId} task={SafeTaskKind(taskKind)} stage=request_started entitlementMs={entitlementMs}");
        var requestStarted = Stopwatch.GetTimestamp();
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
                audio,
            },
            cancellationToken);
        var requestMs = (long)Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
        AppLog.Info(
            $"Paid AI request stage: request={requestId} task={SafeTaskKind(taskKind)} stage=response_completed requestMs={requestMs}");
        if (_account is not null)
        {
            _account = _account with
            {
                AvailableCreditMicros = response.RemainingCreditMicros,
                CycleUsageCreditMicros = checked(
                    _account.CycleUsageCreditMicros + response.ChargedCreditMicros),
            };
        }
        AppLog.Info(
            $"Paid AI request timing: task={SafeTaskKind(taskKind)} entitlementMs={entitlementMs} " +
            $"authorizedRequestMs={requestMs} totalMs=" +
            $"{(long)Stopwatch.GetElapsedTime(totalStarted).TotalMilliseconds}");
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
        var authenticationRefreshAttempted = false;
        var liveSlotConflictRetries = 0;
        while (true)
        {
            var socket = CreateLiveSocket(accessToken);
            try
            {
                await socket.ConnectAsync(builder.Uri, cancellationToken);
                if (authenticationRefreshAttempted)
                    AppLog.Info("Jarvis Live authentication recovered after one token refresh");
                if (liveSlotConflictRetries > 0)
                {
                    AppLog.Info(
                        $"Jarvis Live recovered after a previous-session slot conflict: retries={liveSlotConflictRetries}");
                }
                return socket;
            }
            catch (WebSocketException)
                when (!authenticationRefreshAttempted
                    && IsLiveAuthenticationRejection(socket.HttpStatusCode))
            {
                AppLog.Warning(
                    $"Jarvis Live authentication was rejected with HTTP {(int?)socket.HttpStatusCode}; refreshing once");
                socket.Dispose();
                accessToken = await RefreshTokensAsync(cancellationToken, forceRefresh: true);
                authenticationRefreshAttempted = true;
            }
            catch (WebSocketException exception)
                when (IsLiveAuthenticationRejection(socket.HttpStatusCode))
            {
                socket.Dispose();
                InvalidateLocalSession(
                    "This desktop session was signed out or revoked. Sign in again to use Jarvis AI.");
                throw new InvalidOperationException(_lastError, exception);
            }
            catch (WebSocketException)
                when (IsLiveSlotConflict(socket.HttpStatusCode)
                    && liveSlotConflictRetries < LiveSlotConflictRetryDelays.Count)
            {
                var delay = LiveSlotConflictRetryDelays[liveSlotConflictRetries++];
                AppLog.Warning(
                    $"Jarvis Live previous-session slot is still finishing; retry={liveSlotConflictRetries} delayMs={(long)delay.TotalMilliseconds}");
                socket.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (WebSocketException exception)
                when (IsLiveSlotConflict(socket.HttpStatusCode))
            {
                socket.Dispose();
                throw new InvalidOperationException(
                    "The previous Jarvis session is still finishing. Wait a moment and try again.",
                    exception);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    private static ClientWebSocket CreateLiveSocket(string accessToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        socket.Options.SetRequestHeader(DesktopVersionHeader, DesktopVersion);
        return socket;
    }

    internal static bool IsLiveAuthenticationRejection(HttpStatusCode? statusCode) =>
        statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    internal static bool IsLiveSlotConflict(HttpStatusCode? statusCode) =>
        statusCode == HttpStatusCode.Conflict;

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
            accessToken = await RefreshTokensAsync(cancellationToken, forceRefresh: true);
            try
            {
                return await SendAsync<T>(method, path, body, true, cancellationToken, accessToken);
            }
            catch (PaidApiException retryException) when (retryException.StatusCode == HttpStatusCode.Unauthorized)
            {
                InvalidateLocalSession("This desktop session was signed out or revoked. Sign in again to use Jarvis AI.");
                throw new InvalidOperationException(_lastError, retryException);
            }
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

    private async Task<string> RefreshTokensAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        await _authGate.WaitAsync(cancellationToken);
        try
        {
            if (_tokens is null || _tokens.RefreshTokenExpiresAt <= DateTimeOffset.UtcNow)
            {
                InvalidateLocalSession("The desktop session expired. Sign in again to use Jarvis AI.");
                throw new InvalidOperationException(_lastError);
            }
            if (!forceRefresh && _tokens.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return _tokens.AccessToken;
            SessionTokenResponse refreshed;
            try
            {
                refreshed = await SendAsync<SessionTokenResponse>(
                    HttpMethod.Post,
                    "v1/sessions/refresh",
                    new { refreshToken = _tokens.RefreshToken },
                    authenticated: false,
                    cancellationToken);
            }
            catch (PaidApiException exception)
                when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                InvalidateLocalSession(
                    "The desktop session expired or was revoked. Sign in again to use Jarvis AI.");
                throw new InvalidOperationException(_lastError, exception);
            }
            SaveTokens(refreshed, _tokens.DeviceInstallationId);
            AppLog.Info("Paid account access token refreshed");
            return _tokens.AccessToken;
        }
        finally
        {
            _authGate.Release();
        }
    }

    private static string SafeTaskKind(string taskKind)
    {
        var value = string.IsNullOrWhiteSpace(taskKind) ? "unspecified" : taskKind.Trim();
        value = new string(value
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            .Take(80)
            .ToArray());
        return string.IsNullOrWhiteSpace(value) ? "unspecified" : value;
    }

    private void InvalidateLocalSession(string message)
    {
        _tokens = null;
        _account = null;
        _sessionStore.Clear();
        _lastError = message ?? "The desktop session is no longer valid. Sign in again.";
        lock (_signInSync)
        {
            _signInState = "idle";
            _signInExpiresAt = null;
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
        var requestStarted = Stopwatch.GetTimestamp();
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var headersMs = (long)Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
        var bodyStarted = Stopwatch.GetTimestamp();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        AppLog.Info(
            $"Paid API response timing: path={path} status={(int)response.StatusCode} " +
            $"headersMs={headersMs} bodyMs={(long)Stopwatch.GetElapsedTime(bodyStarted).TotalMilliseconds}");
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
            throw new InvalidOperationException("Paid account is not enabled for this build.");
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
        Uri webBaseUri,
        CancellationToken cancellationToken)
    {
        var targetOrigin = JsonSerializer.Serialize(webBaseUri.GetLeftPart(UriPartial.Authority));
        var successJson = success ? "true" : "false";
        var html = success
            ? "<!doctype html><meta charset=utf-8><title>Jarvis connected</title><h1>Jarvis connected</h1><p>You can close this tab and return to keyboard.wtf.</p>"
            : "<!doctype html><meta charset=utf-8><title>Connection rejected</title><h1>Connection rejected</h1><p>Return to keyboard.wtf and try again.</p>";
        html += $"<script>if(parent!==window)parent.postMessage({{type:'keyboard-wtf-desktop-callback',success:{successJson}}},{targetOrigin});</script>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.StatusCode = success ? 200 : 400;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }

    private static async Task TryWriteCallbackResponseAsync(
        HttpListenerResponse response,
        bool success,
        Uri webBaseUri)
    {
        try
        {
            await WriteCallbackResponseAsync(response, success, webBaseUri, CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The browser may have closed the callback tab. The desktop session
            // result remains authoritative and is surfaced in Settings.
        }
    }

    private static string RandomToken(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string AppendQueryParameter(string url, string name, string value)
    {
        var builder = new UriBuilder(url);
        var separator = string.IsNullOrEmpty(builder.Query) ? "" : "&";
        builder.Query = builder.Query.TrimStart('?')
            + separator
            + Uri.EscapeDataString(name)
            + "="
            + Uri.EscapeDataString(value);
        return builder.Uri.ToString();
    }
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
        CancelSignIn();
        _activeSignInCancellation?.Dispose();
        if (_activeSignInTask is not { IsCompleted: false })
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
