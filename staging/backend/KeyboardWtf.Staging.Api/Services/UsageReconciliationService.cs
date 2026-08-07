using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class UsageReconciliationService
{
    private readonly FirestoreDb _db;
    private readonly IAccountStore _store;
    private readonly IClock _clock;
    private readonly PaidSystemOptions _options;
    private readonly FirestoreMaintenanceLease _leases;
    private readonly MaintenanceScanCursor _cursor;
    private readonly ILogger<UsageReconciliationService> _logger;

    public UsageReconciliationService(
        FirestoreDb db,
        IAccountStore store,
        IClock clock,
        IOptions<PaidSystemOptions> options,
        ILogger<UsageReconciliationService> logger)
    {
        _db = db;
        _store = store;
        _clock = clock;
        _options = options.Value;
        _leases = new FirestoreMaintenanceLease(db, clock);
        _cursor = new MaintenanceScanCursor(db);
        _logger = logger;
    }

    public async Task<ReconciliationRunResult> RunAsync(CancellationToken cancellationToken)
    {
        const string job = "vertex-reservation-reconciliation";
        var startedAt = _clock.UtcNow;
        var runId = $"usagerec_{startedAt:yyyyMMddHHmmss}_{Crypto.RandomToken(6)}";
        await using var lease = await _leases.TryAcquireAsync(
            job,
            TimeSpan.FromSeconds(_options.ReconciliationLeaseSeconds),
            cancellationToken);
        if (lease is null)
            return new ReconciliationRunResult(
                job, runId, 0, 0, 0, 0, false, startedAt, _clock.UtcNow);

        var examined = 0;
        var repaired = 0;
        var quarantined = 0;
        var unchanged = 0;
        var baseQuery = _db.CollectionGroup("reservations")
            .WhereIn("status", new object[] { "reserved", "provider_completed", "reconciliation_pending" })
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
            foreach (var reservation in page.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                examined++;
                try
                {
                    var reservationResult = await ReconcileReservationAsync(reservation, runId, cancellationToken);
                    if (reservationResult == ReservationReconciliationResult.Repaired)
                        repaired++;
                    else if (reservationResult == ReservationReconciliationResult.Quarantined)
                        quarantined++;
                    else
                        unchanged++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    quarantined++;
                    await QuarantineAsync(
                        reservation,
                        "reconciliation_execution_failed",
                        runId,
                        exception.GetType().Name,
                        cancellationToken);
                    _logger.LogWarning(
                        "Usage reconciliation failed reservation={ReservationHash} category={FailureCategory}",
                        Crypto.Sha256(reservation.Id)[..12],
                        exception.GetType().Name);
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
            "Usage reconciliation completed run={RunId} examined={Examined} repaired={Repaired} quarantined={Quarantined}",
            runId,
            examined,
            repaired,
            quarantined);
        return runResult;
    }

    private async Task<ReservationReconciliationResult> ReconcileReservationAsync(
        DocumentSnapshot reservation,
        string runId,
        CancellationToken cancellationToken)
    {
        var data = reservation.ToDictionary();
        var accountRef = reservation.Reference.Parent.Parent
            ?? throw new InvalidOperationException("The reservation account path is invalid.");
        var userId = accountRef.Id;
        var requestId = String(data, "requestId", reservation.Id);
        if (!string.Equals(String(data, "userId", userId), userId, StringComparison.Ordinal))
        {
            await QuarantineAsync(reservation, "reservation_owner_mismatch", runId, "owner", cancellationToken);
            return ReservationReconciliationResult.Quarantined;
        }

        var status = String(data, "status", "reserved");
        var completionRecorded = Bool(data, "providerCompletionRecorded");
        if (completionRecorded || string.Equals(status, "provider_completed", StringComparison.Ordinal))
        {
            if (!completionRecorded)
            {
                await QuarantineAsync(reservation, "completion_status_without_evidence", runId, status, cancellationToken);
                return ReservationReconciliationResult.Quarantined;
            }
            var usage = CompletedUsage(data);
            await _store.SettleAsync(
                userId,
                requestId,
                usage,
                Long(data, "plannedChargeCreditMicros"),
                Long(data, "providerCostUsdMicros"),
                String(data, "outcome", "provider_completed_reconciled"),
                cancellationToken);
            await CloseIssueAsync(reservation, "provider_completion_settled", runId, cancellationToken);
            return ReservationReconciliationResult.Repaired;
        }

        var expiresAt = Date(data, "expiresAt");
        if (expiresAt != DateTimeOffset.UnixEpoch && expiresAt > _clock.UtcNow)
            return ReservationReconciliationResult.Unchanged;

        var providerStarted = data.ContainsKey("providerStartedAt");
        if (!providerStarted)
        {
            await _store.ReleaseReservationAsync(
                userId,
                requestId,
                "expired_before_provider_start",
                cancellationToken);
            await CloseIssueAsync(reservation, "safe_no_dispatch_release", runId, cancellationToken);
            return ReservationReconciliationResult.Repaired;
        }

        if (string.Equals(status, "reserved", StringComparison.Ordinal))
            await _store.MarkReservationForReconciliationAsync(
                userId,
                requestId,
                "maintenance_detected_expired_provider_dispatch",
                cancellationToken);
        var category = Long(data, "checkpointTotalTokens") > 0
            ? "provider_dispatch_with_partial_usage"
            : "provider_dispatch_without_authoritative_completion";
        await QuarantineAsync(reservation, category, runId, status, cancellationToken);
        return ReservationReconciliationResult.Quarantined;
    }

    private Task QuarantineAsync(
        DocumentSnapshot reservation,
        string category,
        string runId,
        string state,
        CancellationToken cancellationToken)
    {
        return AuditIssueStore.UpsertOpenAsync(
            _db,
            "reconciliationIssues",
            Crypto.Sha256($"usage:{reservation.Reference.Path}:{category}"),
            new Dictionary<string, object>
        {
            ["kind"] = "usage",
            ["category"] = category,
            ["resourcePathHash"] = Crypto.Sha256(reservation.Reference.Path),
            ["requestIdHash"] = Crypto.Sha256(reservation.Id),
            ["providerState"] = Safe(state),
            ["lastRunId"] = runId,
        }, _clock.UtcNow, cancellationToken);
    }

    private async Task CloseIssueAsync(
        DocumentSnapshot reservation,
        string resolution,
        string runId,
        CancellationToken cancellationToken)
    {
        var resourcePathHash = Crypto.Sha256(reservation.Reference.Path);
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
        var audit = _db.Collection("auditEvents").Document(
            Crypto.Sha256($"usage-reconciliation-resolution:{reservation.Reference.Path}:{runId}"));
        batch.Create(audit, new Dictionary<string, object>
        {
            ["event"] = "usage_reconciliation_resolved",
            ["resourcePathHash"] = resourcePathHash,
            ["requestIdHash"] = Crypto.Sha256(reservation.Id),
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

    private static ProviderUsage CompletedUsage(IDictionary<string, object> data) => new(
        Long(data, "completedInputTokens"),
        Long(data, "completedOutputTokens"),
        Long(data, "completedCachedInputTokens"),
        Long(data, "completedReasoningTokens"),
        Long(data, "completedImageTokens"),
        Long(data, "completedAudioTokens"),
        Long(data, "completedTotalTokens"),
        String(data, "completedProviderRequestId", ""),
        String(data, "completedProviderModelVersion", ""),
        Long(data, "completedInputAudioTokens"),
        Long(data, "completedOutputAudioTokens"),
        Long(data, "completedInputImageTokens"),
        Long(data, "completedOutputImageTokens"),
        Bool(data, "completedEstimated"));

    private static string String(IDictionary<string, object> data, string key, string fallback) =>
        data.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback
            : fallback;

    private static long Long(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : 0;

    private static bool Bool(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is not null && Convert.ToBoolean(value);

    private static DateTimeOffset Date(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is Timestamp timestamp
            ? new DateTimeOffset(timestamp.ToDateTime())
            : DateTimeOffset.UnixEpoch;

    private static string Safe(string? value)
    {
        var safe = new string((value ?? "unknown")
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(80)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private enum ReservationReconciliationResult
    {
        Unchanged,
        Repaired,
        Quarantined,
    }
}
