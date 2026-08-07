using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class LedgerInvariantService
{
    private readonly FirestoreDb _db;
    private readonly IClock _clock;
    private readonly PaidSystemOptions _options;
    private readonly FirestoreMaintenanceLease _leases;
    private readonly MaintenanceScanCursor _cursor;
    private readonly ILogger<LedgerInvariantService> _logger;

    public LedgerInvariantService(
        FirestoreDb db,
        IClock clock,
        IOptions<PaidSystemOptions> options,
        ILogger<LedgerInvariantService> logger)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        _leases = new FirestoreMaintenanceLease(db, clock);
        _cursor = new MaintenanceScanCursor(db);
        _logger = logger;
    }

    public async Task<InvariantValidationResult> RunAsync(CancellationToken cancellationToken)
    {
        const string job = "ledger-invariants";
        var startedAt = _clock.UtcNow;
        var runId = $"invariant_{startedAt:yyyyMMddHHmmss}_{Crypto.RandomToken(6)}";
        await using var lease = await _leases.TryAcquireAsync(
            job,
            TimeSpan.FromSeconds(_options.ReconciliationLeaseSeconds),
            cancellationToken);
        if (lease is null)
            return new InvariantValidationResult(runId, 0, 0, [], startedAt, _clock.UtcNow);

        var categories = new HashSet<string>(StringComparer.Ordinal);
        var violations = 0;
        var providerPaymentOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var accountsExamined = 0;
        var baseQuery = _db.Collection("accounts").OrderBy(FieldPath.DocumentId);
        var cursor = await _cursor.LoadAsync(job, cancellationToken);
        DocumentSnapshot? lastDocument = cursor;
        var completedScan = false;
        while (accountsExamined < _options.ReconciliationMaxRecordsPerRun)
        {
            var pageSize = Math.Min(
                _options.ReconciliationBatchSize,
                _options.ReconciliationMaxRecordsPerRun - accountsExamined);
            var pageQuery = lastDocument is null
                ? baseQuery.Limit(pageSize)
                : baseQuery.StartAfter(lastDocument).Limit(pageSize);
            var page = await pageQuery.GetSnapshotAsync(cancellationToken);
            if (page.Count == 0)
            {
                completedScan = true;
                break;
            }
            foreach (var account in page.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                accountsExamined++;
                var accountData = account.ToDictionary();
                var ledger = await account.Reference.Collection("ledger").GetSnapshotAsync(cancellationToken);
                var purchases = await account.Reference.Collection("purchases").GetSnapshotAsync(cancellationToken);
                var reconstructedAvailable = ledger.Documents.Sum(entry => Long(entry.ToDictionary(), "availableDeltaMicros"));
                var reconstructedReserved = ledger.Documents.Sum(entry => Long(entry.ToDictionary(), "reservedDeltaMicros"));
                if (reconstructedAvailable != Long(accountData, "availableCreditMicros"))
                    await AddViolationAsync(account, "available_balance_mismatch", runId, categories, cancellationToken, () => violations++);
                if (reconstructedReserved != Long(accountData, "reservedCreditMicros"))
                    await AddViolationAsync(account, "reserved_balance_mismatch", runId, categories, cancellationToken, () => violations++);
                if (Long(accountData, "availableCreditMicros") < 0
                    || Long(accountData, "reservedCreditMicros") < 0
                    || Long(accountData, "creditDebtMicros") < 0)
                    await AddViolationAsync(account, "negative_balance_component", runId, categories, cancellationToken, () => violations++);

                var ledgerPurchaseIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in ledger.Documents)
                {
                    var data = entry.ToDictionary();
                    var type = String(data, "type", "");
                    var referenceType = String(data, "referenceType", "");
                    var referenceId = String(data, "referenceId", "");
                    if (string.Equals(referenceType, "razorpay-event", StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(referenceId))
                    {
                        var paymentEvent = await _db.Collection("paymentEvents")
                            .Document(Crypto.Sha256($"razorpay:{referenceId}"))
                            .GetSnapshotAsync(cancellationToken);
                        if (!paymentEvent.Exists)
                            await AddViolationAsync(account, "orphaned_payment_ledger_event", runId, categories, cancellationToken, () => violations++);
                    }
                    if (data.TryGetValue("metadata", out var metadataValue)
                        && metadataValue is IDictionary<string, object> metadata)
                    {
                        var purchaseId = String(metadata, "purchaseId", "");
                        if (!string.IsNullOrWhiteSpace(purchaseId))
                            ledgerPurchaseIds.Add(purchaseId);
                        if (string.Equals(type, LedgerTypes.UsageSettlement, StringComparison.Ordinal)
                            && Long(metadata, "chargedCreditMicros") > Long(metadata, "reservedCreditMicros")
                            && Long(metadata, "overageCreditMicros") <= 0)
                            await AddViolationAsync(account, "unaudited_reservation_overage", runId, categories, cancellationToken, () => violations++);
                    }
                }

                foreach (var purchase in purchases.Documents)
                {
                    var data = purchase.ToDictionary();
                    if (!string.Equals(String(data, "userId", account.Id), account.Id, StringComparison.Ordinal))
                        await AddViolationAsync(account, "purchase_owner_mismatch", runId, categories, cancellationToken, () => violations++);
                    if (Bool(data, "credited") && !ledgerPurchaseIds.Contains(purchase.Id))
                        await AddViolationAsync(account, "orphaned_purchase_credit", runId, categories, cancellationToken, () => violations++);
                    var providerPaymentId = String(data, "providerPaymentId", "");
                    if (string.IsNullOrWhiteSpace(providerPaymentId))
                        continue;
                    if (providerPaymentOwners.TryGetValue(providerPaymentId, out var existingOwner)
                        && !string.Equals(existingOwner, account.Id, StringComparison.Ordinal))
                        await AddViolationAsync(account, "provider_payment_cross_account_reuse", runId, categories, cancellationToken, () => violations++);
                    else
                        providerPaymentOwners[providerPaymentId] = account.Id;
                }
            }
            lastDocument = page.Documents[^1];
            if (page.Count < pageSize)
            {
                completedScan = true;
                break;
            }
        }
        await _cursor.SaveAsync(job, lastDocument, completedScan, _clock.UtcNow, cancellationToken);

        var completedAt = _clock.UtcNow;
        var result = new InvariantValidationResult(
            runId,
            accountsExamined,
            violations,
            categories.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            startedAt,
            completedAt);
        await _db.Collection("maintenanceRuns").Document(runId).CreateAsync(
            new Dictionary<string, object>
            {
                ["job"] = job,
                ["accountsExamined"] = result.AccountsExamined,
                ["violations"] = result.Violations,
                ["violationCategories"] = result.ViolationCategories.ToArray(),
                ["startedAt"] = Timestamp.FromDateTime(startedAt.UtcDateTime),
                ["completedAt"] = Timestamp.FromDateTime(completedAt.UtcDateTime),
                ["status"] = violations > 0 ? "attention_required" : "completed",
                ["scanCompleted"] = completedScan,
            },
            cancellationToken);
        _logger.LogInformation(
            "Ledger invariant validation completed run={RunId} accounts={Accounts} violations={Violations}",
            runId,
            accountsExamined,
            violations);
        return result;
    }

    private async Task AddViolationAsync(
        DocumentSnapshot account,
        string category,
        string runId,
        ISet<string> categories,
        CancellationToken cancellationToken,
        Action increment)
    {
        increment();
        categories.Add(category);
        var issueId = Crypto.Sha256($"invariant:{account.Reference.Path}:{category}");
        await AuditIssueStore.UpsertOpenAsync(
            _db,
            "invariantIssues",
            issueId,
            new Dictionary<string, object>
            {
                ["category"] = category,
                ["accountPathHash"] = Crypto.Sha256(account.Reference.Path),
                ["lastRunId"] = runId,
            },
            _clock.UtcNow,
            cancellationToken);
    }

    private static string String(IDictionary<string, object> data, string key, string fallback) =>
        data.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback
            : fallback;

    private static long Long(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : 0;

    private static bool Bool(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is not null && Convert.ToBoolean(value);
}
