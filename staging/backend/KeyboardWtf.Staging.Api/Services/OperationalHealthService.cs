using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class OperationalHealthService
{
    private readonly FirestoreDb _db;
    private readonly ICatalogService _catalog;
    private readonly IClock _clock;
    private readonly PaidSystemOptions _options;
    private readonly ILogger<OperationalHealthService> _logger;

    public OperationalHealthService(
        FirestoreDb db,
        ICatalogService catalog,
        IClock clock,
        IOptions<PaidSystemOptions> options,
        ILogger<OperationalHealthService> logger)
    {
        _db = db;
        _catalog = catalog;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var catalog = await _catalog.GetAsync(cancellationToken);
        if (catalog.KillSwitch || catalog.PaymentsKillSwitch)
            _logger.LogWarning(
                "Operational kill switch active ai={AiKillSwitch} payments={PaymentsKillSwitch}",
                catalog.KillSwitch,
                catalog.PaymentsKillSwitch);

        var openIssues = await _db.Collection("reconciliationIssues")
            .WhereEqualTo("status", "open")
            .Limit(501)
            .GetSnapshotAsync(cancellationToken);
        if (openIssues.Count > 0)
            _logger.LogWarning(
                "Unresolved reconciliation mismatches count={OpenIssues}",
                openIssues.Count);

        var invariantIssues = await _db.Collection("invariantIssues")
            .WhereEqualTo("status", "open")
            .Limit(501)
            .GetSnapshotAsync(cancellationToken);
        if (invariantIssues.Count > 0)
            _logger.LogWarning(
                "Open financial invariant issues count={OpenIssues}",
                invariantIssues.Count);

        var providerCostIssues = await _db.Collection("providerCostIssues")
            .WhereEqualTo("status", "open")
            .Limit(501)
            .GetSnapshotAsync(cancellationToken);
        if (providerCostIssues.Count > 0)
            _logger.LogWarning(
                "Provider cost drift reconciliation issues count={OpenIssues}",
                providerCostIssues.Count);

        var highSpendAccounts = await _db.Collection("accounts")
            .WhereGreaterThanOrEqualTo("cycleUsageCreditMicros", _options.PerUserSpendAlertCreditMicros)
            .Limit(101)
            .GetSnapshotAsync(cancellationToken);
        if (highSpendAccounts.Count > 0)
            _logger.LogWarning(
                "Per-user spend threshold exceeded accountCount={AccountCount}",
                highSpendAccounts.Count);

        var date = _clock.UtcNow.ToString("yyyyMMdd");
        var legacy = await _db.Collection("globalUsage").Document(date).GetSnapshotAsync(cancellationToken);
        var shards = await _db.Collection("globalUsageShards")
            .WhereGreaterThanOrEqualTo(FieldPath.DocumentId, $"{date}-000")
            .WhereLessThanOrEqualTo(FieldPath.DocumentId, $"{date}-999")
            .GetSnapshotAsync(cancellationToken);
        var globalCommitted = ReadCommitted(legacy)
            + shards.Documents.Sum(ReadCommitted);
        if (globalCommitted >= _options.GlobalDailyCreditMicrosCap * 8 / 10)
            _logger.LogWarning(
                "Global daily AI spend threshold reached committedCreditMicros={Committed} capCreditMicros={Cap}",
                globalCommitted,
                _options.GlobalDailyCreditMicrosCap);

        var stuckBefore = Timestamp.FromDateTime(
            _clock.UtcNow.AddMinutes(-_options.StuckPaymentMinutes).UtcDateTime);
        var pendingPurchases = await _db.CollectionGroup("purchases")
            .WhereIn("status", new object[] { "creating", "pending", "verification_pending", "authorized" })
            .WhereLessThan("updatedAt", stuckBefore)
            .Limit(501)
            .GetSnapshotAsync(cancellationToken);
        if (pendingPurchases.Count > 0)
            _logger.LogWarning(
                "Stuck payment confirmation detected count={StuckPayments}",
                pendingPurchases.Count);
    }

    private static long ReadCommitted(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists)
            return 0;
        var data = snapshot.ToDictionary();
        return ReadLong(data, "reservedCreditMicros") + ReadLong(data, "settledCreditMicros");
    }

    private static long ReadLong(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : 0;
}
