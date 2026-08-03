namespace KeyboardWtf.Tests;

using System.Text;
using System.Net;
using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.PaidStaging;
using Xunit;

public sealed class PaidStagingSecurityTests
{
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
    public void Remote_plain_http_paid_endpoint_is_rejected()
    {
        using var scope = new EnvironmentScope(
            ("KEYBOARD_WTF_PAID_STAGING", "1"),
            ("KEYBOARD_WTF_PAID_BACKEND_URL", "http://example.test"),
            ("KEYBOARD_WTF_PAID_WEB_URL", "https://staging.example.test"));
        Assert.Throws<InvalidOperationException>(PaidStagingConfiguration.FromEnvironment);
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
            store.Clear();
            Assert.False(File.Exists(path));
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

        public PaidApiHandler(long availableCreditMicros) =>
            _availableCreditMicros = availableCreditMicros;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
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
