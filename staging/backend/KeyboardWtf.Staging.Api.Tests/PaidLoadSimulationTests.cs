using System.Collections.Concurrent;
using System.Diagnostics;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Contracts;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class PaidLoadSimulationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Five_hundred_users_complete_five_thousand_metered_requests_without_leakage()
    {
        const int users = 500;
        const int requestsPerUser = 10;
        var store = new LoadAccountStore();
        var catalog = new LoadCatalog();
        var vertex = new LoadVertex();
        var service = new AiUsageService(
            store,
            catalog,
            new LoadRouting(catalog.Pricing),
            vertex,
            Options.Create(new PaidSystemOptions
            {
                MaxRequestSeconds = 5,
                MaxRetries = 0,
                MaxConcurrentRequestsPerUser = 1,
                MaxRequestsPerMinutePerUser = 60,
                DefaultTaskCreditMicrosCap = 25_000_000,
            }));
        var durations = new ConcurrentBag<double>();
        var total = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, users),
            new ParallelOptions { MaxDegreeOfParallelism = 100 },
            async (userIndex, cancellationToken) =>
            {
                var caller = new CallerIdentity(
                    $"load-user-{userIndex:D4}",
                    $"load-{userIndex:D4}@example.invalid",
                    "Synthetic Load User",
                    "test");
                await store.EnsureAccountAsync(caller, cancellationToken);
                for (var requestIndex = 0; requestIndex < requestsPerUser; requestIndex++)
                {
                    var timer = Stopwatch.StartNew();
                    var request = new GenerateRequest(
                        $"request-{requestIndex:D2}",
                        "safe deterministic load request",
                        null,
                        64,
                        TaskKind: "desktop-health");
                    var response = await service.GenerateAsync(
                        caller,
                        request,
                        "1.3.0",
                        cancellationToken);
                    timer.Stop();
                    Assert.True(response.Verified);
                    Assert.Equal(10, response.ChargedCreditMicros);
                    durations.Add(timer.Elapsed.TotalMilliseconds);
                }
            });
        total.Stop();

        Assert.Equal(users * requestsPerUser, vertex.Calls);
        Assert.Equal(users * requestsPerUser, store.Settlements);
        Assert.Equal(users, store.AccountCount);
        Assert.All(store.Snapshots, snapshot =>
        {
            Assert.Equal(999_900, snapshot.AvailableCreditMicros);
            Assert.Equal(0, snapshot.ReservedCreditMicros);
            Assert.Equal(100, snapshot.CycleUsageCreditMicros);
        });
        var ordered = durations.OrderBy(value => value).ToArray();
        var p50 = Percentile(ordered, 0.50);
        var p95 = Percentile(ordered, 0.95);
        var p99 = Percentile(ordered, 0.99);
        output.WriteLine(
            "fake-provider normal load users={0} requests={1} concurrency=100 totalMs={2:F1} p50Ms={3:F3} p95Ms={4:F3} p99Ms={5:F3} failures=0",
            users,
            users * requestsPerUser,
            total.Elapsed.TotalMilliseconds,
            p50,
            p95,
            p99);
    }

    [Fact]
    public async Task Twenty_five_concurrent_live_sessions_use_cumulative_high_water_accounting()
    {
        var totals = new ConcurrentBag<long>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 25),
            new ParallelOptions { MaxDegreeOfParallelism = 25 },
            (session, _) =>
            {
                var usage = new LiveUsageAccumulator(1_000_000);
                usage.ObserveProvider("""{"usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":50,"totalTokenCount":150}}""");
                usage.ObserveProvider("""{"usageMetadata":{"promptTokenCount":200,"candidatesTokenCount":100,"totalTokenCount":300}}""");
                usage.ObserveProvider("""{"usageMetadata":{"promptTokenCount":200,"candidatesTokenCount":100,"totalTokenCount":300}}""");
                usage.ObserveProvider("""{"usageMetadata":{"promptTokenCount":120,"candidatesTokenCount":60,"totalTokenCount":180}}""");
                totals.Add(usage.Snapshot().TotalTokens);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(25, totals.Count);
        Assert.All(totals, total => Assert.Equal(300, total));
        output.WriteLine("fake-provider live load sessions=25 cumulativeSnapshots=100 failures=0");
    }

    private static double Percentile(double[] ordered, double fraction) =>
        ordered.Length == 0
            ? 0
            : ordered[Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * fraction) - 1)];

    private sealed class LoadCatalog : ICatalogService
    {
        public PricingSnapshot Pricing { get; } = new(
            "load-pricing-v1", "gemini-load", 100_000, 200_000, 25_000, 200_000,
            10_000, 10_000, DateTimeOffset.Parse("2026-08-11T00:00:00Z"));

        public Task<CatalogSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogSnapshot("load-v1", [], Pricing, false, null));
        public Task<CatalogProduct> GetProductAsync(string sku, CancellationToken cancellationToken, bool allowPaymentCanary = false) =>
            throw new NotSupportedException();
        public long EstimateReservationCreditMicros(PricingSnapshot pricing, string prompt, int maxOutputTokens, long taskCapCreditMicros) =>
            Math.Min(1_000, taskCapCreditMicros);
        public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateActualCost(PricingSnapshot pricing, ProviderUsage usage) =>
            (100, 10);
        public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateLiveActualCost(PricingSnapshot pricing, ProviderUsage usage, int estimateSafetyBasisPoints) =>
            (100, 10);
    }

    private sealed class LoadRouting(PricingSnapshot pricing) : IModelRoutingService
    {
        public Task<ModelRouteSnapshot> ResolveAsync(string? taskKind, string routingKey, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelRouteSnapshot(
                "fast_text",
                "load-route-v1",
                "vertex",
                [new ModelCandidateSnapshot("gemini-load", "global", pricing)],
                true,
                false,
                10_000,
                1_024,
                5,
                0,
                0,
                "1.3.0",
                "load simulation"));
    }

    private sealed class LoadVertex : IVertexAiClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<VertexGenerateResult> GenerateAsync(
            string model,
            string location,
            string prompt,
            string? systemInstruction,
            int maxOutputTokens,
            System.Text.Json.JsonElement? responseJsonSchema,
            IReadOnlyList<InlineImageRequest>? images,
            InlineAudioRequest? audio,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new VertexGenerateResult(
                "ok",
                new ProviderUsage(20, 5, 0, 0, 0, 0, 25, "load-provider", "load-model")));
        }
    }

    private sealed class LoadAccountStore : IAccountStore
    {
        private sealed class State
        {
            public object Gate { get; } = new();
            public long Available { get; set; } = 1_000_000;
            public long Reserved { get; set; }
            public long Consumed { get; set; }
            public Dictionary<string, long> Reservations { get; } = new(StringComparer.Ordinal);
            public HashSet<string> UsedRequestIds { get; } = new(StringComparer.Ordinal);
        }

        private readonly ConcurrentDictionary<string, State> _accounts = new(StringComparer.Ordinal);
        private int _settlements;
        public int AccountCount => _accounts.Count;
        public int Settlements => Volatile.Read(ref _settlements);
        public IReadOnlyList<AccountSnapshot> Snapshots =>
            _accounts.Select(pair => Snapshot(pair.Key, pair.Value)).ToArray();

        public Task EnsureAccountAsync(CallerIdentity caller, CancellationToken cancellationToken)
        {
            _accounts.GetOrAdd(caller.UserId, _ => new State());
            return Task.CompletedTask;
        }

        public Task<AccountSnapshot> GetAccountAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot(userId, Account(userId)));

        public Task<UsagePageSnapshot> GetUsagePageAsync(string userId, int limit, string? cursor, CancellationToken cancellationToken) =>
            Task.FromResult(new UsagePageSnapshot([], null));

        public Task<ReservationResult> ReserveAsync(
            CallerIdentity caller,
            string requestId,
            string model,
            string pricingVersion,
            long reserveCreditMicros,
            long taskCapCreditMicros,
            int requestsPerMinute,
            int maxConcurrent,
            long globalDailyCap,
            CancellationToken cancellationToken,
            string activityType = "ai_request")
        {
            var state = Account(caller.UserId);
            lock (state.Gate)
            {
                if (!state.UsedRequestIds.Add(requestId))
                    return Task.FromResult(new ReservationResult(false, Reservation(caller.UserId, requestId, 0), Snapshot(caller.UserId, state)));
                if (state.Available < reserveCreditMicros || state.Reservations.Count >= maxConcurrent)
                    throw new InvalidOperationException("Synthetic account budget or concurrency exceeded.");
                state.Available -= reserveCreditMicros;
                state.Reserved += reserveCreditMicros;
                state.Reservations.Add(requestId, reserveCreditMicros);
                return Task.FromResult(new ReservationResult(true, Reservation(caller.UserId, requestId, reserveCreditMicros), Snapshot(caller.UserId, state)));
            }
        }

        public Task MarkReservationProviderStartedAsync(string userId, string requestId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<long> EnsureReservationCapacityAsync(string userId, string requestId, long targetReservedCreditMicros, CancellationToken cancellationToken) =>
            Task.FromResult(Account(userId).Reservations[requestId]);
        public Task RecordProviderCompletionAsync(string userId, string requestId, ProviderUsage usage, long actualCreditMicros, long providerCostUsdMicros, string outcome, CancellationToken cancellationToken, string? model = null, string? pricingVersion = null) => Task.CompletedTask;
        public Task CheckpointReservationUsageAsync(string userId, string requestId, ProviderUsage usage, long estimatedCreditMicros, long providerCostUsdMicros, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MarkReservationForReconciliationAsync(string userId, string requestId, string outcome, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SettlementResult> SettleAsync(string userId, string requestId, ProviderUsage usage, long actualCreditMicros, long providerCostUsdMicros, string outcome, CancellationToken cancellationToken)
        {
            var state = Account(userId);
            lock (state.Gate)
            {
                if (!state.Reservations.Remove(requestId, out var reserved))
                    throw new InvalidOperationException("Synthetic reservation is missing.");
                state.Reserved -= reserved;
                state.Available += reserved - actualCreditMicros;
                state.Consumed += actualCreditMicros;
                Interlocked.Increment(ref _settlements);
                return Task.FromResult(new SettlementResult(true, actualCreditMicros, reserved - actualCreditMicros, Snapshot(userId, state)));
            }
        }

        public Task ReleaseReservationAsync(string userId, string requestId, string outcome, CancellationToken cancellationToken)
        {
            var state = Account(userId);
            lock (state.Gate)
            {
                if (state.Reservations.Remove(requestId, out var reserved))
                {
                    state.Reserved -= reserved;
                    state.Available += reserved;
                }
            }
            return Task.CompletedTask;
        }

        private State Account(string userId) => _accounts.GetOrAdd(userId, _ => new State());
        private static AccountSnapshot Snapshot(string userId, State state)
        {
            lock (state.Gate)
                return new AccountSnapshot(userId, $"{userId}@example.invalid", "Synthetic", "active", "load", state.Available, state.Reserved, state.Consumed, null, [], [], []);
        }
        private static Reservation Reservation(string userId, string requestId, long reserved) =>
            new(requestId, userId, reserved, "gemini-load", "load-pricing-v1", "reserved", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1));

        public Task<DesktopLoginAttempt> CreateLoginAttemptAsync(string stateHash, string deviceNonceHash, string pkceChallenge, string callbackUri, DateTimeOffset expiresAt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(DesktopLoginAttempt Attempt, string AuthorizationCode)> AuthorizeLoginAttemptAsync(string attemptId, string stateHash, string deviceNonceHash, string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DesktopSessionTokens> ExchangeLoginAttemptAsync(string attemptId, string stateHash, string deviceNonceHash, string authorizationCodeHash, string pkceVerifier, string deviceName, string deviceInstallationId, TimeSpan accessLifetime, TimeSpan refreshLifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CallerIdentity?> AuthenticateDesktopTokenAsync(string accessToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DesktopSessionTokens> RefreshSessionAsync(string refreshToken, TimeSpan accessLifetime, TimeSpan refreshLifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RevokeSessionAsync(string userId, string? sessionId, bool allDevices, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RevokeDeviceAsync(string userId, string deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAccountOperationalDataAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AccountSnapshot> GrantCreditsAsync(string userId, long creditMicros, string reason, string idempotencyKey, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> BeginPurchaseAsync(string userId, string purchaseId, CatalogProduct product, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AttachPurchaseProviderObjectAsync(string userId, string purchaseId, string providerObjectId, string providerObjectType, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkPurchaseFailedAsync(string userId, string purchaseId, string outcome, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelPendingPurchaseAsync(string userId, string purchaseId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task MarkCheckoutSignatureVerifiedAsync(string userId, string purchaseId, string providerObjectId, string providerPaymentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentWebhookResult> ApplyPaymentWebhookAsync(string eventId, string eventType, string providerObjectId, string? providerPaymentId, string? providerOrderId, string? providerSubscriptionId, string? purchaseId, string? providerEventObjectId, long? providerAmountMinor, string status, CancellationToken cancellationToken, DateTimeOffset? subscriptionCycleEndsAt = null, string? providerCurrency = null, string? providerProductSku = null) => throw new NotSupportedException();
    }
}
