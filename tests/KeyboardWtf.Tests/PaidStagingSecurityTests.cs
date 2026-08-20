namespace KeyboardWtf.Tests;

using System.Text;
using System.Net;
using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai;
using KeyboardWtf.Services.PaidStaging;
using KeyboardWtf.Models;
using Xunit;

public sealed class PaidStagingSecurityTests
{
    [Fact]
    public void Paid_mode_routes_every_selectable_desktop_provider_through_cloud_accounting()
    {
        using var paid = new PaidAccountService(new PaidStagingConfiguration(
            true,
            false,
            new Uri("https://paid-api.example.test/"),
            new Uri("https://paid.example.test/")));
        using var claude = new ClaudeApiService();
        try
        {
            AiProviderRegistry.Initialize(claude, paid);

            Assert.Equal("keyboard.wtf Cloud AI", AiProviderRegistry.Get(AiProvider.Gemini).DisplayName);
            Assert.Equal("keyboard.wtf Cloud AI", AiProviderRegistry.Get(AiProvider.Claude).DisplayName);
            Assert.Equal("keyboard.wtf Cloud AI", AiProviderRegistry.Get(AiProvider.OpenAi).DisplayName);
        }
        finally
        {
            AiProviderRegistry.Initialize(claude, null);
        }
    }

    [Fact]
    public void AppDataOverride_MustRemainUnderLocalAppData()
    {
        var root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-local-appdata");
        var expected = Path.Combine(root, "keyboard.wtf-paid-staging");

        Assert.Equal(expected, SettingsService.ResolveAppDataDir(expected, root));
        Assert.Throws<InvalidOperationException>(() =>
            SettingsService.ResolveAppDataDir(Path.GetPathRoot(root), root));
        Assert.Throws<InvalidOperationException>(() =>
            SettingsService.ResolveAppDataDir(Path.Combine(root, "..", "outside"), root));
    }

    [Fact]
    public void DefaultAppDataPath_RemainsTheStableKeyboardWtfDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-local-appdata-default");
        Assert.Equal(
            Path.Combine(root, "keyboard.wtf"),
            SettingsService.ResolveAppDataDir(null, root));
    }

    [Fact]
    public void Disabled_paid_staging_does_not_require_cloud_urls()
    {
        using var scope = new EnvironmentScope(
            ("KEYBOARD_WTF_PAID_STAGING", null),
            ("KEYBOARD_WTF_PAID_BACKEND_URL", null),
            ("KEYBOARD_WTF_PAID_WEB_URL", null));
        var configuration = PaidStagingConfiguration.FromEnvironment();
        Assert.False(configuration.Enabled);
        Assert.False(configuration.PreserveExistingRegistrations);
    }

    [Fact]
    public void Enabled_paid_staging_requires_explicit_safe_urls()
    {
        using var scope = new EnvironmentScope(
            ("KEYBOARD_WTF_PAID_STAGING", "1"),
            ("KEYBOARD_WTF_PAID_BACKEND_URL", null),
            ("KEYBOARD_WTF_PAID_WEB_URL", null));
        Assert.Throws<InvalidOperationException>(PaidStagingConfiguration.FromEnvironment);
    }

    [Fact]
    public void Enabled_paid_staging_preserves_the_stable_install_registrations()
    {
        using var scope = new EnvironmentScope(
            ("KEYBOARD_WTF_PAID_STAGING", "1"),
            ("KEYBOARD_WTF_PAID_BACKEND_URL", "https://paid-api.example.test"),
            ("KEYBOARD_WTF_PAID_WEB_URL", "https://paid.example.test"));

        var configuration = PaidStagingConfiguration.FromEnvironment();

        Assert.True(configuration.Enabled);
        Assert.True(configuration.PreserveExistingRegistrations);
        Assert.False(configuration.IsProduction);
    }

    [Fact]
    public void Remote_plain_http_paid_endpoint_is_rejected()
    {
        using var scope = new EnvironmentScope(
            ("KEYBOARD_WTF_PAID_STAGING", "1"),
            ("KEYBOARD_WTF_PAID_BACKEND_URL", "http://example.test"),
            ("KEYBOARD_WTF_PAID_WEB_URL", "https://staging.example.test"));
        Assert.Throws<InvalidOperationException>(PaidStagingConfiguration.FromEnvironment);
    }

    [Fact]
    public void Production_configuration_is_pinned_and_disables_emergency_byok()
    {
        var configuration = PaidStagingConfiguration.Production();

        Assert.True(configuration.Enabled);
        Assert.True(configuration.IsProduction);
        Assert.False(configuration.EmergencyByokEnabled);
        Assert.Equal("keyboard-wtf-paid-api-mhrc6c7goa-el.a.run.app", configuration.BackendBaseUri.Host);
        Assert.Equal("keyboardwtf.com", configuration.WebBaseUri.Host);
        Assert.Equal(Uri.UriSchemeHttps, configuration.BackendBaseUri.Scheme);
        Assert.Equal(Uri.UriSchemeHttps, configuration.WebBaseUri.Scheme);
        Assert.False(configuration.PreserveExistingRegistrations);
    }

    [Fact]
    public void Paid_session_store_round_trips_without_plaintext_tokens()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-paid-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.dat");
        try
        {
            var store = new PaidSessionStore(path);
            var deviceInstallationId = store.LoadOrCreateDeviceInstallationId();
            var tokens = new PaidSessionTokens(
                "access_token_secret_marker",
                DateTimeOffset.UtcNow.AddMinutes(10),
                "refresh_token_secret_marker",
                DateTimeOffset.UtcNow.AddDays(10),
                "session-id",
                "device-id",
                "installation-id");
            store.Save(tokens);
            var bytes = File.ReadAllBytes(path);
            var raw = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("access_token_secret_marker", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("refresh_token_secret_marker", raw, StringComparison.Ordinal);
            Assert.Equal(tokens, store.Load());
            var deviceBytes = File.ReadAllBytes(path + ".device");
            Assert.DoesNotContain(deviceInstallationId, Encoding.UTF8.GetString(deviceBytes), StringComparison.Ordinal);
            store.Clear();
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(path + ".device"));
            Assert.Equal(deviceInstallationId, new PaidSessionStore(path).LoadOrCreateDeviceInstallationId());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Zero_credit_account_is_blocked_before_generate_endpoint()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            var handler = new PaidApiHandler(availableCreditMicros: 0);
            using var account = CreateAccount(store, handler);
            await account.InitializeAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => account.GenerateAsync(
                "hello", null, 32, null, null, "unit-test", CancellationToken.None));

            Assert.Contains("no usable credits", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(handler.Requests, path => path.EndsWith("/v1/ai/generate", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Credited_account_uses_authenticated_generate_endpoint()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            var handler = new PaidApiHandler(availableCreditMicros: 1_000_000);
            using var account = CreateAccount(store, handler);
            await account.InitializeAsync();

            var response = await account.GenerateAsync(
                "hello", null, 32, null, null, "unit-test", CancellationToken.None);

            Assert.True(response.Verified);
            Assert.Equal("ok", response.Text);
            Assert.Equal(999_990, account.Account.AvailableCreditMicros);
            Assert.Equal(10, account.Account.CycleUsageCreditMicros);
            Assert.Contains(handler.Requests, path => path.EndsWith("/v1/ai/generate", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Smart_writing_cancellation_reaches_the_authenticated_generate_request()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            var handler = new CancellableGenerateHandler();
            using var account = CreateAccount(store, handler);
            await account.InitializeAsync();
            var provider = new PaidCloudAiProvider(account);
            using var cancellation = new CancellationTokenSource();

            var request = provider.ReformatAsync(
                "messy words",
                "Polish this text.",
                "en",
                cancellation.Token);
            await handler.GenerateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.Equal(1, handler.GenerateCalls);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Intentional_activation_can_warm_the_authenticated_backend_without_using_AI()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            var handler = new PaidApiHandler(availableCreditMicros: 0);
            using var account = CreateAccount(store, handler);
            await account.InitializeAsync();

            await account.WarmBackendAsync();

            Assert.Contains("/v1/ready", handler.Requests);
            Assert.DoesNotContain(handler.Requests, path =>
                path.EndsWith("/v1/ai/generate", StringComparison.Ordinal)
                || path.EndsWith("/v1/ai/live", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(400, false)]
    [InlineData(429, false)]
    [InlineData(500, false)]
    public void Live_authentication_retry_is_limited_to_auth_rejections(
        int statusCode,
        bool expected) =>
        Assert.Equal(
            expected,
            PaidAccountService.IsLiveAuthenticationRejection(
                (System.Net.HttpStatusCode)statusCode));

    [Fact]
    public async Task Sign_out_all_devices_is_server_authoritative_and_clears_the_local_session()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            var handler = new PaidApiHandler(availableCreditMicros: 1_000_000);
            using var account = CreateAccount(store, handler);
            await account.InitializeAsync();

            await account.SignOutAsync(allDevices: true);

            Assert.False(account.IsSignedIn);
            Assert.Null(store.Load());
            var body = Assert.Single(
                handler.Bodies,
                pair => pair.Path == "/v1/sessions/logout").Body;
            using var json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.GetProperty("allDevices").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Revoking_another_device_is_server_authoritative_and_refreshes_the_account()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            var handler = new PaidApiHandler(availableCreditMicros: 1_000_000);
            using var account = CreateAccount(store, handler);
            await account.InitializeAsync();

            await account.RevokeDeviceAsync("other-device");

            var request = Assert.Single(
                handler.Bodies,
                pair => pair.Path == "/v1/devices/revoke").Body;
            using var json = JsonDocument.Parse(request);
            Assert.Equal("other-device", json.RootElement.GetProperty("deviceId").GetString());
            Assert.Equal(2, handler.Requests.Count(path => path == "/v1/account"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Desktop_cancellation_uses_the_authenticated_server_endpoint_and_refreshes_account()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            var handler = new PaidApiHandler(availableCreditMicros: 1_000_000);
            using var account = CreateAccount(store, handler);
            await account.InitializeAsync();

            await account.CancelSubscriptionAtCycleEndAsync();

            var request = Assert.Single(
                handler.Bodies,
                pair => pair.Path == "/v1/billing/subscription/cancel").Body;
            using var json = JsonDocument.Parse(request);
            Assert.True(json.RootElement.GetProperty("atCycleEnd").GetBoolean());
            Assert.Equal(2, handler.Requests.Count(path => path == "/v1/account"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Concurrent_desktop_sign_in_requests_share_one_attempt_and_one_browser_launch()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            var handler = new DesktopLoginHandler();
            var browserLaunches = 0;
            using var account = new PaidAccountService(
                new PaidStagingConfiguration(
                    true,
                    false,
                    new Uri("https://paid-api.example.test/"),
                    new Uri("https://paid.example.test/")),
                store,
                handler,
                browserUrl =>
                {
                    Assert.Contains("desktop-login", browserUrl, StringComparison.Ordinal);
                    Interlocked.Increment(ref browserLaunches);
                    _ = Task.Run(async () =>
                    {
                        var request = await handler.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        var callback = new UriBuilder(request.CallbackUri);
                        callback.Query = $"attempt=attempt-1&state={Uri.EscapeDataString(request.State)}&code={new string('a', 43)}";
                        using var client = new HttpClient();
                        using var response = await client.GetAsync(callback.Uri);
                        response.EnsureSuccessStatusCode();
                    });
                });

            var first = account.SignInAsync();
            var second = account.SignInAsync();
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, browserLaunches);
            Assert.Equal("completed", account.SignInState);
            Assert.True(account.IsSignedIn);
            Assert.All(results, result => Assert.Equal("uid-1", result.UserId));
            Assert.Equal(1, handler.StartCalls);
            Assert.Equal(1, handler.ExchangeCalls);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Cancelling_desktop_sign_in_stops_the_pending_attempt_without_exchange()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            var handler = new DesktopLoginHandler();
            var browserLaunches = 0;
            using var account = new PaidAccountService(
                new PaidStagingConfiguration(
                    true,
                    false,
                    new Uri("https://paid-api.example.test/"),
                    new Uri("https://paid.example.test/")),
                store,
                handler,
                _ => Interlocked.Increment(ref browserLaunches));

            var signIn = account.SignInAsync();
            await handler.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await account.CancelSignInAsync());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn);

            Assert.Equal(1, browserLaunches);
            Assert.Equal("cancelled", account.SignInState);
            Assert.False(account.SignInPending);
            Assert.Equal(0, handler.ExchangeCalls);
            Assert.False(account.IsSignedIn);

            var lateDuplicate = await Assert.ThrowsAsync<InvalidOperationException>(
                () => account.SignInAsync());
            Assert.Contains("Try again", lateDuplicate.Message, StringComparison.Ordinal);
            Assert.Equal(1, handler.StartCalls);
            Assert.Equal(1, browserLaunches);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Existing_desktop_session_refreshes_without_opening_another_browser()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            var handler = new PaidApiHandler(availableCreditMicros: 0);
            var browserLaunches = 0;
            using var account = new PaidAccountService(
                new PaidStagingConfiguration(
                    true,
                    false,
                    new Uri("https://paid-api.example.test/"),
                    new Uri("https://paid.example.test/")),
                store,
                handler,
                _ => Interlocked.Increment(ref browserLaunches));
            await account.InitializeAsync();

            var result = await account.SignInAsync();

            Assert.Equal("uid-1", result.UserId);
            Assert.Equal(0, browserLaunches);
            Assert.DoesNotContain(handler.Requests, path =>
                path.Contains("desktop-login", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Desktop_account_contract_preserves_friendly_billing_usage_and_device_fields()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            using var account = CreateAccount(store, new RichAccountHandler());

            await account.InitializeAsync();

            var snapshot = Assert.IsType<PaidAccountSnapshot>(account.Account);
            Assert.Equal("Jarvis Monthly", snapshot.PlanDisplayName);
            Assert.Equal(99_900, snapshot.PlanAmountMinor);
            Assert.Equal("scheduled", snapshot.SubscriptionCancellationStatus);
            Assert.NotNull(snapshot.SubscriptionCancelsAt);
            var usage = Assert.Single(snapshot.RecentUsage);
            Assert.Equal("browser_planner", usage.ActivityType);
            Assert.Equal("pricing-production-v1", usage.PricingVersion);
            Assert.Equal("settled", usage.AccountingStatus);
            Assert.Equal("Jarvis Monthly", Assert.Single(snapshot.RecentPurchases).DisplayName);
            var device = Assert.Single(snapshot.Devices);
            Assert.Equal("Windows", device.Platform);
            Assert.True(device.IsCurrent);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Restored_session_reports_loading_until_authoritative_account_arrives()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            var handler = new BlockingAccountHandler();
            using var account = CreateAccount(store, handler);

            var initialize = account.InitializeAsync();
            await handler.AccountRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(account.IsSignedIn);
            Assert.True(account.IsAccountLoading);
            Assert.Null(account.Account);

            handler.ReleaseAccount.TrySetResult();
            await initialize.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(account.IsAccountLoading);
            Assert.NotNull(account.Account);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Terminal_unauthorized_response_clears_stale_account_and_blocks_paid_AI()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            var handler = new RevokedSessionHandler();
            using var account = CreateAccount(store, handler);
            await account.InitializeAsync();
            Assert.NotNull(account.Account);
            Assert.True(account.IsSignedIn);

            handler.Revoked = true;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => account.RefreshAccountAsync());

            Assert.Contains("revoked", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(account.IsSignedIn);
            Assert.Null(account.Account);
            Assert.Null(store.Load());
            await Assert.ThrowsAsync<InvalidOperationException>(() => account.GenerateAsync(
                "must not reach AI", null, 32, null, null, "unit-test", CancellationToken.None));
            Assert.Equal(0, handler.GenerateCalls);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Transient_refresh_network_failure_preserves_the_desktop_session()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            store.Save(new PaidSessionTokens(
                "expired-access-token",
                DateTimeOffset.UtcNow.AddMinutes(-2),
                "still-valid-refresh-token",
                DateTimeOffset.UtcNow.AddDays(10),
                "session-id",
                "device-id",
                "installation-id"));
            using var account = CreateAccount(store, new RefreshNetworkFailureHandler());

            await account.InitializeAsync();

            Assert.True(account.IsSignedIn);
            Assert.NotNull(store.Load());
            Assert.Contains("temporarily unavailable", account.LastError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Switching_accounts_revokes_the_current_session_and_forces_one_account_chooser()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var (store, directory) = CreateSessionStore();
        try
        {
            SaveValidSession(store);
            var handler = new DesktopLoginHandler();
            var browserLaunches = 0;
            string launchedUrl = null;
            using var account = new PaidAccountService(
                new PaidStagingConfiguration(
                    true,
                    false,
                    new Uri("https://paid-api.example.test/"),
                    new Uri("https://paid.example.test/")),
                store,
                handler,
                browserUrl =>
                {
                    launchedUrl = browserUrl;
                    Interlocked.Increment(ref browserLaunches);
                    _ = Task.Run(async () =>
                    {
                        var request = await handler.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        var callback = new UriBuilder(request.CallbackUri);
                        callback.Query = $"attempt=attempt-1&state={Uri.EscapeDataString(request.State)}&code={new string('a', 43)}";
                        using var client = new HttpClient();
                        using var response = await client.GetAsync(callback.Uri);
                        response.EnsureSuccessStatusCode();
                        handler.CallbackHtml = await response.Content.ReadAsStringAsync();
                    });
                });
            await account.InitializeAsync();

            var result = await account.SwitchAccountAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("uid-1", result.UserId);
            Assert.Equal(1, handler.LogoutCalls);
            Assert.Equal(1, handler.StartCalls);
            Assert.Equal(1, handler.ExchangeCalls);
            Assert.Equal(1, browserLaunches);
            Assert.Contains("switch=1", launchedUrl, StringComparison.Ordinal);
            Assert.Contains("keyboard-wtf-desktop-callback", handler.CallbackHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("location.replace", handler.CallbackHtml, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    private static PaidAccountService CreateAccount(PaidSessionStore store, HttpMessageHandler handler) =>
        new(
            new PaidStagingConfiguration(
                true,
                false,
                new Uri("https://paid-api.example.test/"),
                new Uri("https://paid.example.test/")),
            store,
            handler);

    private static (PaidSessionStore Store, string Directory) CreateSessionStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-paid-tests", Guid.NewGuid().ToString("N"));
        return (new PaidSessionStore(Path.Combine(directory, "session.dat")), directory);
    }

    private static void SaveValidSession(PaidSessionStore store) =>
        store.Save(new PaidSessionTokens(
            "access-token",
            DateTimeOffset.UtcNow.AddMinutes(10),
            "refresh-token",
            DateTimeOffset.UtcNow.AddDays(10),
            "session-id",
            "device-id",
            "installation-id"));

    private sealed class PaidApiHandler : HttpMessageHandler
    {
        private readonly long _availableCreditMicros;
        public List<string> Requests { get; } = [];
        public List<(string Path, string Body)> Bodies { get; } = [];

        public PaidApiHandler(long availableCreditMicros) =>
            _availableCreditMicros = availableCreditMicros;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (request.Content is not null)
                Bodies.Add((path, await request.Content.ReadAsStringAsync(cancellationToken)));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            var payload = path switch
            {
                "/v1/account" => JsonSerializer.Serialize(new
                {
                    userId = "uid-1",
                    email = "person@example.test",
                    displayName = "Test Person",
                    status = "active",
                    planId = "none",
                    availableCreditMicros = _availableCreditMicros,
                    reservedCreditMicros = 0,
                    cycleUsageCreditMicros = 0,
                    cycleEndsAt = (DateTimeOffset?)null,
                    devices = Array.Empty<object>(),
                    recentUsage = Array.Empty<object>(),
                    recentPurchases = Array.Empty<object>(),
                    subscriptionStatus = "none",
                }),
                "/v1/ready" => "{\"status\":\"ready\"}",
                "/v1/sessions/logout" => "{}",
                "/v1/devices/revoke" => "{}",
                "/v1/billing/subscription/cancel" => "{}",
                "/v1/ai/generate" => JsonSerializer.Serialize(new
                {
                    requestId = "request-1",
                    text = "ok",
                    model = "gemini-test",
                    chargedCreditMicros = 10,
                    remainingCreditMicros = _availableCreditMicros - 10,
                    usage = new { inputTokens = 1, outputTokens = 1 },
                    verified = true,
                }),
                _ => throw new InvalidOperationException($"Unexpected paid API request: {path}"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class BlockingAccountHandler : HttpMessageHandler
    {
        public TaskCompletionSource AccountRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAccount { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/account", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            AccountRequested.TrySetResult();
            await ReleaseAccount.Task.WaitAsync(cancellationToken);
            var payload = JsonSerializer.Serialize(new
            {
                userId = "uid-1",
                email = "person@example.test",
                displayName = "Test Person",
                status = "active",
                planId = "monthly_standard_inr_999_v1",
                availableCreditMicros = 500_000_000,
                reservedCreditMicros = 0,
                cycleUsageCreditMicros = 0,
                cycleEndsAt = DateTimeOffset.UtcNow.AddDays(30),
                devices = Array.Empty<object>(),
                recentUsage = Array.Empty<object>(),
                recentPurchases = Array.Empty<object>(),
                subscriptionStatus = "active",
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CancellableGenerateHandler : HttpMessageHandler
    {
        public TaskCompletionSource GenerateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int GenerateCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/account")
            {
                var account = JsonSerializer.Serialize(new
                {
                    userId = "uid-1",
                    email = "person@example.test",
                    displayName = "Test Person",
                    status = "active",
                    planId = "monthly_standard_inr_999_v1",
                    availableCreditMicros = 1_000_000,
                    reservedCreditMicros = 0,
                    cycleUsageCreditMicros = 0,
                    cycleEndsAt = DateTimeOffset.UtcNow.AddDays(30),
                    devices = Array.Empty<object>(),
                    recentUsage = Array.Empty<object>(),
                    recentPurchases = Array.Empty<object>(),
                    subscriptionStatus = "active",
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(account, Encoding.UTF8, "application/json"),
                };
            }

            Assert.Equal("/v1/ai/generate", request.RequestUri.AbsolutePath);
            GenerateCalls++;
            GenerateStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled request unexpectedly resumed.");
        }
    }

    private sealed class RevokedSessionHandler : HttpMessageHandler
    {
        public bool Revoked { get; set; }
        public int GenerateCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/ai/generate")
                GenerateCalls++;
            if (Revoked)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"title\":\"Session revoked\"}", Encoding.UTF8, "application/json"),
                });

            var payload = JsonSerializer.Serialize(new
            {
                userId = "uid-1",
                email = "person@example.test",
                displayName = "Test Person",
                status = "active",
                planId = "monthly_standard_inr_999_v1",
                availableCreditMicros = 500_000_000,
                reservedCreditMicros = 0,
                cycleUsageCreditMicros = 0,
                cycleEndsAt = DateTimeOffset.UtcNow.AddDays(30),
                devices = Array.Empty<object>(),
                recentUsage = Array.Empty<object>(),
                recentPurchases = Array.Empty<object>(),
                subscriptionStatus = "active",
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RefreshNetworkFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/sessions/refresh", request.RequestUri!.AbsolutePath);
            throw new HttpRequestException("Simulated network interruption");
        }
    }

    private sealed class RichAccountHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/account", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var now = DateTimeOffset.Parse("2026-08-14T10:00:00Z");
            var payload = JsonSerializer.Serialize(new
            {
                userId = "uid-1",
                email = "person@example.test",
                displayName = "Test Person",
                status = "active",
                planId = "monthly_standard_inr_999_v1",
                planDisplayName = "Jarvis Monthly",
                planAmountMinor = 99_900,
                planCurrency = "INR",
                availableCreditMicros = 499_000_000,
                reservedCreditMicros = 0,
                cycleUsageCreditMicros = 1_000_000,
                cycleEndsAt = now.AddDays(30),
                subscriptionStatus = "active",
                subscriptionCancellationStatus = "scheduled",
                subscriptionCancelsAt = now.AddDays(30),
                devices = new[]
                {
                    new { deviceId = "device-id", name = "My PC", status = "active", lastSeenAt = now, createdAt = now.AddDays(-1), platform = "Windows", isCurrent = true },
                },
                recentUsage = new[]
                {
                    new { requestId = "req-1", model = "gemini-test", outcome = "completed", chargedCreditMicros = 1_000_000, inputTokens = 10, outputTokens = 4, createdAt = now, activityType = "browser_planner", pricingVersion = "pricing-production-v1", accountingStatus = "settled" },
                },
                recentPurchases = new[]
                {
                    new { purchaseId = "purchase-1", productSku = "monthly_standard_inr_999_v1", provider = "razorpay", status = "paid", amountMinor = 99_900, currency = "INR", createdAt = now.AddDays(-1), fulfilled = true, displayName = "Jarvis Monthly" },
                },
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record DesktopLoginStartBody(
        string State,
        string DeviceNonce,
        string PkceChallenge,
        string CallbackUri);

    private sealed class DesktopLoginHandler : HttpMessageHandler
    {
        public TaskCompletionSource<DesktopLoginStartBody> LoginStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StartCalls;
        public int ExchangeCalls;
        public int LogoutCalls;
        public string CallbackHtml = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string payload;
            switch (path)
            {
                case "/v1/desktop-login/start":
                    Assert.Null(request.Headers.Authorization);
                    Interlocked.Increment(ref StartCalls);
                    var startJson = await request.Content!.ReadAsStringAsync(cancellationToken);
                    var start = JsonSerializer.Deserialize<DesktopLoginStartBody>(
                        startJson,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                    LoginStarted.TrySetResult(start);
                    payload = JsonSerializer.Serialize(new
                    {
                        attemptId = "attempt-1",
                        browserUrl = "https://paid.example.test/desktop-login.html?attempt=attempt-1",
                        expiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
                    });
                    break;
                case "/v1/sessions/logout":
                    Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                    Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
                    Interlocked.Increment(ref LogoutCalls);
                    payload = "{}";
                    break;
                case "/v1/desktop-login/exchange":
                    Assert.Null(request.Headers.Authorization);
                    Interlocked.Increment(ref ExchangeCalls);
                    payload = JsonSerializer.Serialize(new
                    {
                        accessToken = "desktop-access-token",
                        accessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                        refreshToken = "desktop-refresh-token",
                        refreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                        sessionId = "session-1",
                        deviceId = "device-1",
                    });
                    break;
                case "/v1/account":
                    Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                    Assert.Contains(
                        request.Headers.Authorization?.Parameter,
                        new[] { "access-token", "desktop-access-token" });
                    payload = JsonSerializer.Serialize(new
                    {
                        userId = "uid-1",
                        email = "person@example.test",
                        displayName = "Test Person",
                        status = "active",
                        planId = "none",
                        availableCreditMicros = 0,
                        reservedCreditMicros = 0,
                        cycleUsageCreditMicros = 0,
                        cycleEndsAt = (DateTimeOffset?)null,
                        devices = Array.Empty<object>(),
                        recentUsage = Array.Empty<object>(),
                        recentPurchases = Array.Empty<object>(),
                        subscriptionStatus = "none",
                    });
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected desktop-login request: {path}");
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string> _previous = new(StringComparer.Ordinal);

        public EnvironmentScope(params (string Name, string Value)[] values)
        {
            foreach (var (name, value) in values)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var pair in _previous)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
