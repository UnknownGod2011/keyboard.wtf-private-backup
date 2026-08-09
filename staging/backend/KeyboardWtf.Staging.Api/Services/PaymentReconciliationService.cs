using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class PaymentReconciliationService
{
    private readonly FirestoreDb _db;
    private readonly IAccountStore _store;
    private readonly IPaymentProvider _provider;
    private readonly IClock _clock;
    private readonly PaidSystemOptions _options;
    private readonly FirestoreMaintenanceLease _leases;
    private readonly MaintenanceScanCursor _cursor;
    private readonly ILogger<PaymentReconciliationService> _logger;

    public PaymentReconciliationService(
        FirestoreDb db,
        IAccountStore store,
        IPaymentProvider provider,
        IClock clock,
        IOptions<PaidSystemOptions> options,
        ILogger<PaymentReconciliationService> logger)
    {
        _db = db;
        _store = store;
        _provider = provider;
        _clock = clock;
        _options = options.Value;
        _leases = new FirestoreMaintenanceLease(db, clock);
        _cursor = new MaintenanceScanCursor(db);
        _logger = logger;
    }

    public async Task<ReconciliationRunResult> RunAsync(CancellationToken cancellationToken)
    {
        const string job = "razorpay-reconciliation";
        var startedAt = _clock.UtcNow;
        var runId = $"payrec_{startedAt:yyyyMMddHHmmss}_{Crypto.RandomToken(6)}";
        await using var lease = await _leases.TryAcquireAsync(
            job,
            TimeSpan.FromSeconds(_options.ReconciliationLeaseSeconds),
            cancellationToken);
        if (lease is null)
            return new ReconciliationRunResult(
                job, runId, 0, 0, 0, 0, false, startedAt, _clock.UtcNow);
        if (!_provider.IsConfigured)
            throw new InvalidOperationException("Razorpay Test Mode is not configured for reconciliation.");

        var examined = 0;
        var repaired = 0;
        var quarantined = 0;
        var unchanged = 0;
        var baseQuery = _db.CollectionGroup("purchases")
            .WhereEqualTo("provider", "razorpay")
            .OrderBy(FieldPath.DocumentId);
        var cursor = await _cursor.LoadAsync(job, cancellationToken);
        DocumentSnapshot? lastDocument = cursor;
        var completedScan = false;
        while (examined < _options.ReconciliationMaxRecordsPerRun)
        {
            var pageSize = Math.Min(
                _options.ReconciliationBatchSize,
                _options.ReconciliationMaxRecordsPerRun - examined);
            var pageQuery = lastDocument is null
                ? baseQuery.Limit(pageSize)
                : baseQuery.StartAfter(lastDocument).Limit(pageSize);
            var page = await pageQuery.GetSnapshotAsync(cancellationToken);
            if (page.Count == 0)
            {
                completedScan = true;
                break;
            }
            foreach (var purchase in page.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                try
                {
                    var purchaseResult = await ReconcilePurchaseAsync(purchase, runId, cancellationToken);
                    if (purchaseResult == PurchaseReconciliationResult.Repaired)
                        repaired++;
                    else if (purchaseResult == PurchaseReconciliationResult.Quarantined)
                        quarantined++;
                    else
                        unchanged++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var failureStage = exception is PaymentReconciliationStageException staged
                        ? staged.Stage
                        : "unknown";
                    var failureCategory = $"reconciliation_{SafeStatus(failureStage)}_failed";
                    var failureType = exception is PaymentReconciliationStageException { InnerException: not null } wrapped
                        ? wrapped.InnerException!.GetType().Name
                        : exception.GetType().Name;
                    quarantined++;
                    await QuarantineAsync(
                        purchase,
                        failureCategory,
                        runId,
                        failureType,
                        cancellationToken);
                    _logger.LogWarning(
                        "Payment reconciliation failed for purchase {PurchaseHash} at {FailureStage}: {FailureCategory}",
                        Crypto.Sha256(purchase.Id)[..12],
                        failureStage,
                        failureType);
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

        var runResult = new ReconciliationRunResult(
            job, runId, examined, repaired, quarantined, unchanged, true, startedAt, _clock.UtcNow);
        await WriteRunAsync(runResult, completedScan, cancellationToken);
        _logger.LogInformation(
            "Payment reconciliation completed run={RunId} examined={Examined} repaired={Repaired} quarantined={Quarantined}",
            runId,
            examined,
            repaired,
            quarantined);
        return runResult;
    }

    private async Task<PurchaseReconciliationResult> ReconcilePurchaseAsync(
        DocumentSnapshot purchase,
        string runId,
        CancellationToken cancellationToken)
    {
        var stage = "load_purchase";
        try
        {
            var data = purchase.ToDictionary();
            var accountRef = purchase.Reference.Parent.Parent
                ?? throw new InvalidOperationException("The purchase account path is invalid.");
            var userId = accountRef.Id;
            var purchaseId = String(data, "purchaseId", purchase.Id);
            var recordedUserId = String(data, "userId", "");
            if (!string.IsNullOrWhiteSpace(recordedUserId)
                && !string.Equals(recordedUserId, userId, StringComparison.Ordinal))
            {
                await QuarantineAsync(purchase, "purchase_owner_mismatch", runId, "owner", cancellationToken);
                return PurchaseReconciliationResult.Quarantined;
            }
            if (string.IsNullOrWhiteSpace(recordedUserId))
                await purchase.Reference.SetAsync(
                    new Dictionary<string, object> { ["userId"] = userId },
                    SetOptions.MergeAll,
                    cancellationToken);

            var objectId = String(data, "providerObjectId", "");
            var objectType = String(data, "providerObjectType", "");
            if (string.IsNullOrWhiteSpace(objectId))
            {
                var createdAt = Date(data, "createdAt");
                if (createdAt != DateTimeOffset.UnixEpoch
                    && _clock.UtcNow - createdAt > TimeSpan.FromHours(1))
                {
                    await QuarantineAsync(purchase, "provider_object_missing", runId, "missing", cancellationToken);
                    return PurchaseReconciliationResult.Quarantined;
                }
                await MarkCheckedAsync(purchase.Reference, runId, "awaiting_provider_object", cancellationToken);
                return PurchaseReconciliationResult.Unchanged;
            }

            var repaired = false;
            stage = "inspect_provider";
            if (string.Equals(objectType, "order", StringComparison.Ordinal))
            {
                var order = await _provider.InspectOrderAsync(objectId, cancellationToken);
                if (!MatchesPurchase(order.PurchaseId, purchaseId)
                    || order.AmountMinor != Long(data, "amountMinor")
                    || !string.Equals(order.Currency, String(data, "currency", "INR"), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(order.ProductSku, String(data, "productSku", ""), StringComparison.Ordinal))
                {
                    await QuarantineAsync(purchase, "order_identity_or_amount_mismatch", runId, order.Status, cancellationToken);
                    return PurchaseReconciliationResult.Quarantined;
                }
                stage = "apply_order_payments";
                foreach (var payment in order.Payments.OrderBy(item => item.CreatedAt))
                    repaired |= await ReconcilePaymentAsync(
                        purchase,
                        purchaseId,
                        objectId,
                        null,
                        payment,
                        cancellationToken);
            }
            else if (string.Equals(objectType, "subscription", StringComparison.Ordinal))
            {
                var subscription = await _provider.InspectSubscriptionAsync(objectId, cancellationToken);
                if (!MatchesPurchase(subscription.PurchaseId, purchaseId)
                    || !string.Equals(subscription.ProductSku, String(data, "productSku", ""), StringComparison.Ordinal))
                {
                    await QuarantineAsync(purchase, "subscription_identity_mismatch", runId, subscription.Status, cancellationToken);
                    return PurchaseReconciliationResult.Quarantined;
                }

                stage = "apply_subscription_lifecycle";
                var lifecycleEvent = SubscriptionLifecycleEvent(subscription.Status);
                var lifecycle = await _store.ApplyPaymentWebhookAsync(
                    $"reconcile:{objectId}:{String(data, "status", "unknown")}:to:{subscription.Status}",
                    lifecycleEvent,
                    objectId,
                    null,
                    null,
                    objectId,
                    purchaseId,
                    objectId,
                    null,
                    subscription.Status,
                    cancellationToken,
                    subscription.CurrentCycleEndsAt,
                    null,
                    subscription.ProductSku);
                repaired |= !lifecycle.Duplicate;

                stage = "apply_subscription_charges";
                foreach (var payment in subscription.Charges.OrderBy(item => item.CreatedAt))
                {
                    if (!string.Equals(payment.SubscriptionId, objectId, StringComparison.Ordinal))
                    {
                        await QuarantineAsync(purchase, "subscription_charge_mismatch", runId, payment.Status, cancellationToken);
                        return PurchaseReconciliationResult.Quarantined;
                    }
                    repaired |= await ReconcilePaymentAsync(
                        purchase,
                        purchaseId,
                        objectId,
                        objectId,
                        payment,
                        cancellationToken,
                        subscription.CurrentCycleEndsAt);
                }
            }
            else
            {
                await QuarantineAsync(purchase, "unknown_provider_object_type", runId, objectType, cancellationToken);
                return PurchaseReconciliationResult.Quarantined;
            }

            stage = "mark_checked";
            await MarkCheckedAsync(
                purchase.Reference,
                runId,
                repaired ? "repaired" : "consistent",
                cancellationToken);
            stage = "resolve_issues";
            await ResolveIssuesAsync(
                purchase,
                runId,
                repaired ? "provider_state_repaired" : "provider_state_consistent",
                cancellationToken);
            return repaired ? PurchaseReconciliationResult.Repaired : PurchaseReconciliationResult.Unchanged;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new PaymentReconciliationStageException(stage, exception);
        }
    }

    private async Task<bool> ReconcilePaymentAsync(
        DocumentSnapshot purchase,
        string purchaseId,
        string providerObjectId,
        string? subscriptionId,
        ProviderPayment payment,
        CancellationToken cancellationToken,
        DateTimeOffset? cycleEndsAt = null)
    {
        var data = purchase.ToDictionary();
        if (payment.AmountMinor != Long(data, "amountMinor")
            || !string.Equals(payment.Currency, String(data, "currency", "INR"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The provider payment amount or currency does not match the purchase.");
        var captured = payment.Captured
            || string.Equals(payment.Status, "captured", StringComparison.OrdinalIgnoreCase);
        var eventType = subscriptionId is not null
            ? captured ? "subscription.charged" : $"payment.{SafeStatus(payment.Status)}"
            : captured ? "payment.captured" : $"payment.{SafeStatus(payment.Status)}";
        var result = await _store.ApplyPaymentWebhookAsync(
            $"reconcile:{eventType}:{payment.Id}",
            eventType,
            providerObjectId,
            payment.Id,
            payment.OrderId,
            subscriptionId,
            purchaseId,
            payment.Id,
            payment.AmountMinor,
            subscriptionId is null ? payment.Status : captured ? "active" : payment.Status,
            cancellationToken,
            cycleEndsAt,
            payment.Currency,
            String(data, "productSku", ""));
        var changed = !result.Duplicate;

        foreach (var refund in await _provider.InspectRefundsAsync(payment.Id, cancellationToken))
        {
            if (!string.Equals(refund.Status, "processed", StringComparison.OrdinalIgnoreCase))
                continue;
            var refundResult = await _store.ApplyPaymentWebhookAsync(
                $"reconcile:refund:{refund.Id}",
                "refund.processed",
                providerObjectId,
                payment.Id,
                payment.OrderId,
                subscriptionId,
                purchaseId,
                refund.Id,
                refund.AmountMinor,
                refund.Status,
                cancellationToken,
                cycleEndsAt,
                payment.Currency,
                String(data, "productSku", ""));
            changed |= !refundResult.Duplicate;
        }

        foreach (var dispute in await _provider.InspectDisputesAsync(payment.Id, cancellationToken))
        {
            var disputeResult = await _store.ApplyPaymentWebhookAsync(
                $"reconcile:dispute:{dispute.Id}",
                "dispute.created",
                providerObjectId,
                payment.Id,
                payment.OrderId,
                subscriptionId,
                purchaseId,
                dispute.Id,
                dispute.AmountMinor,
                dispute.Status,
                cancellationToken,
                cycleEndsAt,
                payment.Currency,
                String(data, "productSku", ""));
            changed |= !disputeResult.Duplicate;
        }
        return changed;
    }

    private Task MarkCheckedAsync(
        DocumentReference reference,
        string runId,
        string outcome,
        CancellationToken cancellationToken) =>
        reference.SetAsync(new Dictionary<string, object>
        {
            ["reconciliationStatus"] = outcome,
            ["lastReconciliationRunId"] = runId,
            ["lastReconciledAt"] = Timestamp.FromDateTime(_clock.UtcNow.UtcDateTime),
            ["updatedAt"] = Timestamp.FromDateTime(_clock.UtcNow.UtcDateTime),
        }, SetOptions.MergeAll, cancellationToken);

    private Task QuarantineAsync(
        DocumentSnapshot purchase,
        string category,
        string runId,
        string providerState,
        CancellationToken cancellationToken)
    {
        return AuditIssueStore.UpsertOpenAsync(
            _db,
            "reconciliationIssues",
            Crypto.Sha256($"payment:{purchase.Reference.Path}:{category}"),
            new Dictionary<string, object>
        {
            ["kind"] = "payment",
            ["category"] = category,
            ["resourcePathHash"] = Crypto.Sha256(purchase.Reference.Path),
            ["purchaseIdHash"] = Crypto.Sha256(purchase.Id),
            ["providerState"] = SafeStatus(providerState),
            ["lastRunId"] = runId,
        }, _clock.UtcNow, cancellationToken);
    }

    private async Task ResolveIssuesAsync(
        DocumentSnapshot purchase,
        string runId,
        string resolution,
        CancellationToken cancellationToken)
    {
        var resourcePathHash = Crypto.Sha256(purchase.Reference.Path);
        var matching = await _db.Collection("reconciliationIssues")
            .WhereEqualTo("resourcePathHash", resourcePathHash)
            .GetSnapshotAsync(cancellationToken);
        var batch = _db.StartBatch();
        foreach (var issue in matching.Documents.Where(document =>
                     string.Equals(
                         String(document.ToDictionary(), "status", ""),
                         "open",
                         StringComparison.Ordinal)))
        {
            batch.Set(issue.Reference, new Dictionary<string, object>
            {
                ["status"] = "resolved",
                ["resolution"] = resolution,
                ["lastRunId"] = runId,
                ["resolvedAt"] = Timestamp.FromDateTime(_clock.UtcNow.UtcDateTime),
            }, SetOptions.MergeAll);
        }
        batch.Create(
            _db.Collection("auditEvents").Document(
                Crypto.Sha256($"payment-reconciliation-resolution:{purchase.Reference.Path}:{runId}")),
            new Dictionary<string, object>
            {
                ["event"] = "payment_reconciliation_resolved",
                ["resourcePathHash"] = resourcePathHash,
                ["purchaseIdHash"] = Crypto.Sha256(purchase.Id),
                ["resolution"] = resolution,
                ["runId"] = runId,
                ["createdAt"] = Timestamp.FromDateTime(_clock.UtcNow.UtcDateTime),
            });
        await batch.CommitAsync(cancellationToken);
    }

    private Task WriteRunAsync(
        ReconciliationRunResult result,
        bool completedScan,
        CancellationToken cancellationToken) =>
        _db.Collection("maintenanceRuns").Document(result.RunId).CreateAsync(
            new Dictionary<string, object>
            {
                ["job"] = result.Job,
                ["examined"] = result.Examined,
                ["repaired"] = result.Repaired,
                ["quarantined"] = result.Quarantined,
                ["unchanged"] = result.Unchanged,
                ["startedAt"] = Timestamp.FromDateTime(result.StartedAt.UtcDateTime),
                ["completedAt"] = Timestamp.FromDateTime(result.CompletedAt.UtcDateTime),
                ["status"] = result.Quarantined > 0 ? "attention_required" : "completed",
                ["scanCompleted"] = completedScan,
            },
            cancellationToken);

    internal static string SubscriptionLifecycleEvent(string status) =>
        SafeStatus(status) switch
        {
            "active" => "subscription.activated",
            "authenticated" => "subscription.authenticated",
            "paused" => "subscription.paused",
            "cancelled" => "subscription.cancelled",
            "halted" => "subscription.halted",
            "completed" => "subscription.completed",
            "expired" => "subscription.completed",
            _ => "subscription.updated",
        };

    private static bool MatchesPurchase(string? providerPurchaseId, string purchaseId) =>
        !string.IsNullOrWhiteSpace(providerPurchaseId)
        && string.Equals(providerPurchaseId, purchaseId, StringComparison.Ordinal);

    private static string SafeStatus(string? value)
    {
        var safe = new string((value ?? "unknown")
            .Trim()
            .ToLowerInvariant()
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            .Take(80)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private static string String(IDictionary<string, object> data, string key, string fallback) =>
        data.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback
            : fallback;

    private static long Long(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : 0;

    private static DateTimeOffset Date(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is Timestamp timestamp
            ? new DateTimeOffset(timestamp.ToDateTime())
            : DateTimeOffset.UnixEpoch;

    private enum PurchaseReconciliationResult
    {
        Unchanged,
        Repaired,
        Quarantined,
    }

    private sealed class PaymentReconciliationStageException(string stage, Exception innerException)
        : Exception("Payment reconciliation failed at a recorded stage.", innerException)
    {
        public string Stage { get; } = stage;
    }
}
