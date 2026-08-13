using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;

namespace KeyboardWtf.Staging.Api.Services;

public sealed record SubscriptionCancellationResult(
    string Status,
    DateTimeOffset? CancelsAt,
    bool ProviderUpdated);

public sealed class SubscriptionManagementService
{
    private static readonly TimeSpan RequestLease = TimeSpan.FromMinutes(2);
    private readonly FirestoreDb _db;
    private readonly IPaymentProvider _provider;
    private readonly IClock _clock;
    private readonly ILogger<SubscriptionManagementService> _logger;

    public SubscriptionManagementService(
        FirestoreDb db,
        IPaymentProvider provider,
        IClock clock,
        ILogger<SubscriptionManagementService> logger)
    {
        _db = db;
        _provider = provider;
        _clock = clock;
        _logger = logger;
    }

    public async Task<SubscriptionCancellationResult> CancelAtCycleEndAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var request = await BeginAsync(userId, cancellationToken);
        if (!request.ShouldCallProvider)
            return new SubscriptionCancellationResult(
                request.Status,
                request.CancelsAt,
                ProviderUpdated: false);

        try
        {
            var providerResult = await _provider.CancelSubscriptionAtCycleEndAsync(
                request.ProviderSubscriptionId,
                cancellationToken);
            var cancelsAt = ValidateScheduledCancellation(providerResult, request.CycleEndsAt);
            await MarkScheduledAsync(
                userId,
                request,
                providerResult.Status,
                cancelsAt,
                cancellationToken);
            return new SubscriptionCancellationResult("scheduled", cancelsAt, ProviderUpdated: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                var inspection = await _provider.InspectSubscriptionAsync(
                    request.ProviderSubscriptionId,
                    cancellationToken);
                if (IsScheduledAtCycleEnd(inspection))
                {
                    var cancelsAt = inspection.CurrentCycleEndsAt ?? request.CycleEndsAt
                        ?? throw new InvalidOperationException("Razorpay did not return the cancellation date.");
                    await MarkScheduledAsync(
                        userId,
                        request,
                        inspection.Status,
                        cancelsAt,
                        cancellationToken);
                    return new SubscriptionCancellationResult("scheduled", cancelsAt, ProviderUpdated: true);
                }
            }
            catch (Exception inspectionError) when (inspectionError is not OperationCanceledException)
            {
                _logger.LogWarning(
                    inspectionError,
                    "Subscription cancellation verification failed for account {AccountHash}",
                    Crypto.Sha256(userId)[..12]);
            }

            await MarkRetryableAsync(userId, request, cancellationToken);
            throw new InvalidOperationException(
                "Cancellation could not be confirmed with Razorpay. No local plan change was made; retry shortly.",
                exception);
        }
    }

    public async Task RecordProviderInspectionAsync(
        string userId,
        string providerSubscriptionId,
        ProviderSubscriptionInspection inspection,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var now = _clock.UtcNow;
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            if (!snapshot.Exists)
                return 0;
            var data = snapshot.ToDictionary();
            if (!string.Equals(
                    String(data, "activeSubscriptionProviderObjectId"),
                    providerSubscriptionId,
                    StringComparison.Ordinal))
                return 0;

            if (IsScheduledAtCycleEnd(inspection))
            {
                var cancelsAt = inspection.CurrentCycleEndsAt;
                if (cancelsAt is not null)
                    transaction.Update(accountRef, new Dictionary<string, object>
                    {
                        ["subscriptionCancellationStatus"] = "scheduled",
                        ["subscriptionCancelsAt"] = Timestamp.FromDateTime(cancelsAt.Value.UtcDateTime),
                        ["subscriptionCancellationProviderStatus"] = inspection.Status,
                        ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
                    });
            }
            else if (string.Equals(inspection.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                transaction.Update(accountRef, new Dictionary<string, object>
                {
                    ["subscriptionCancellationStatus"] = "cancelled",
                    ["subscriptionCancelsAt"] = Timestamp.FromDateTime(
                        (inspection.CurrentCycleEndsAt ?? inspection.ChangeScheduledAt ?? now).UtcDateTime),
                    ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
                });
            }
            else if (string.Equals(String(data, "subscriptionCancellationStatus"), "requesting", StringComparison.Ordinal)
                && Date(data, "subscriptionCancellationRequestedAt") < now.Subtract(RequestLease))
            {
                transaction.Update(accountRef, new Dictionary<string, object>
                {
                    ["subscriptionCancellationStatus"] = "retryable",
                    ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
                });
            }
            return 0;
        }, cancellationToken: cancellationToken);
    }

    private async Task<CancellationRequest> BeginAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var now = _clock.UtcNow;
        var requestId = $"cancel_{Crypto.RandomToken(12)}";
        return await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            if (!snapshot.Exists)
                throw new KeyNotFoundException("The account is unavailable.");
            var data = snapshot.ToDictionary();
            var subscriptionStatus = String(data, "subscriptionStatus").ToLowerInvariant();
            var cancellationStatus = String(data, "subscriptionCancellationStatus", "none").ToLowerInvariant();
            var cancelsAt = NullableDate(data, "subscriptionCancelsAt");
            if (cancellationStatus is "scheduled" or "cancelled")
                return new CancellationRequest("", "scheduled", cancelsAt, null, "", false);
            if (subscriptionStatus is "cancelled" or "completed" or "expired")
                return new CancellationRequest("", "cancelled", cancelsAt, null, "", false);
            if (subscriptionStatus is not ("active" or "pending" or "paused" or "halted" or "authenticated"))
                throw new InvalidOperationException("There is no cancellable subscription on this account.");

            var providerSubscriptionId = String(data, "activeSubscriptionProviderObjectId");
            if (!providerSubscriptionId.StartsWith("sub_", StringComparison.Ordinal))
                throw new InvalidOperationException("The active subscription is not linked to Razorpay.");
            var requestedAt = Date(data, "subscriptionCancellationRequestedAt");
            if (cancellationStatus == "requesting" && requestedAt >= now.Subtract(RequestLease))
                return new CancellationRequest(
                    providerSubscriptionId,
                    "processing",
                    cancelsAt,
                    NullableDate(data, "cycleEndsAt"),
                    String(data, "subscriptionCancellationRequestId"),
                    false);

            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["subscriptionCancellationStatus"] = "requesting",
                ["subscriptionCancellationRequestId"] = requestId,
                ["subscriptionCancellationRequestedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
                ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
            });
            transaction.Create(accountRef.Collection("subscriptionActions").Document(requestId),
                new Dictionary<string, object>
                {
                    ["action"] = "cancel_at_cycle_end",
                    ["status"] = "requesting",
                    ["actor"] = "account-owner",
                    ["createdAt"] = Timestamp.FromDateTime(now.UtcDateTime),
                });
            return new CancellationRequest(
                providerSubscriptionId,
                "requesting",
                cancelsAt,
                NullableDate(data, "cycleEndsAt"),
                requestId,
                true);
        }, cancellationToken: cancellationToken);
    }

    private async Task MarkScheduledAsync(
        string userId,
        CancellationRequest request,
        string providerStatus,
        DateTimeOffset cancelsAt,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var now = _clock.UtcNow;
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            if (!snapshot.Exists)
                throw new KeyNotFoundException("The account is unavailable.");
            var data = snapshot.ToDictionary();
            if (!string.Equals(
                    String(data, "activeSubscriptionProviderObjectId"),
                    request.ProviderSubscriptionId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException("The active subscription changed during cancellation.");
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["subscriptionCancellationStatus"] = "scheduled",
                ["subscriptionCancelsAt"] = Timestamp.FromDateTime(cancelsAt.UtcDateTime),
                ["subscriptionCancellationProviderStatus"] = providerStatus,
                ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
            });
            if (!string.IsNullOrWhiteSpace(request.RequestId))
                transaction.Set(
                    accountRef.Collection("subscriptionActions").Document(request.RequestId),
                    new Dictionary<string, object>
                    {
                        ["status"] = "scheduled",
                        ["cancelsAt"] = Timestamp.FromDateTime(cancelsAt.UtcDateTime),
                        ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
                    },
                    SetOptions.MergeAll);
            return 0;
        }, cancellationToken: cancellationToken);
    }

    private Task MarkRetryableAsync(
        string userId,
        CancellationRequest request,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var now = _clock.UtcNow;
        var batch = _db.StartBatch();
        batch.Set(accountRef, new Dictionary<string, object>
        {
            ["subscriptionCancellationStatus"] = "retryable",
            ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
        }, SetOptions.MergeAll);
        if (!string.IsNullOrWhiteSpace(request.RequestId))
            batch.Set(accountRef.Collection("subscriptionActions").Document(request.RequestId),
                new Dictionary<string, object>
                {
                    ["status"] = "retryable",
                    ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
                },
                SetOptions.MergeAll);
        return batch.CommitAsync(cancellationToken);
    }

    internal static bool IsScheduledAtCycleEnd(ProviderSubscriptionInspection inspection) =>
        inspection.HasScheduledChanges
        && string.Equals(inspection.ScheduledChangeAt, "cycle_end", StringComparison.OrdinalIgnoreCase);

    internal static DateTimeOffset ValidateScheduledCancellation(
        ProviderSubscriptionCancellation result,
        DateTimeOffset? accountCycleEndsAt)
    {
        if (!result.HasScheduledChanges
            || !string.Equals(result.ScheduledChangeAt, "cycle_end", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Razorpay did not schedule cancellation at the billing-cycle boundary.");
        return result.CurrentCycleEndsAt ?? accountCycleEndsAt
            ?? throw new InvalidOperationException("Razorpay did not return the cancellation date.");
    }

    private DocumentReference Account(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Contains('/'))
            throw new ArgumentException("The account identity is invalid.", nameof(userId));
        return _db.Collection("accounts").Document(userId);
    }

    private static string String(IDictionary<string, object> data, string key, string fallback = "") =>
        data.TryGetValue(key, out var value) && value is string text ? text : fallback;

    private static DateTimeOffset Date(IDictionary<string, object> data, string key) =>
        NullableDate(data, key) ?? DateTimeOffset.UnixEpoch;

    private static DateTimeOffset? NullableDate(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is Timestamp timestamp
            ? new DateTimeOffset(timestamp.ToDateTime())
            : null;

    private sealed record CancellationRequest(
        string ProviderSubscriptionId,
        string Status,
        DateTimeOffset? CancelsAt,
        DateTimeOffset? CycleEndsAt,
        string RequestId,
        bool ShouldCallProvider);
}
