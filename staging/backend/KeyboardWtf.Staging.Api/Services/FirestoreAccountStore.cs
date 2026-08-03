using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;
using Grpc.Core;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class FirestoreAccountStore : IAccountStore
{
    private readonly FirestoreDb _db;
    private readonly IClock _clock;
    private readonly PaidSystemOptions _options;

    public FirestoreAccountStore(FirestoreDb db, IClock clock, IOptions<PaidSystemOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
    }

    public async Task EnsureAccountAsync(CallerIdentity caller, CancellationToken cancellationToken)
    {
        var accountRef = Account(caller.UserId);
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var now = Ts(_clock.UtcNow);
            if (!snapshot.Exists)
            {
                transaction.Create(accountRef, new Dictionary<string, object>
                {
                    ["uid"] = caller.UserId,
                    ["email"] = caller.Email.Trim().ToLowerInvariant(),
                    ["displayName"] = caller.DisplayName,
                    ["status"] = "active",
                    ["planId"] = "none",
                    ["subscriptionStatus"] = "none",
                    ["availableCreditMicros"] = 0L,
                    ["reservedCreditMicros"] = 0L,
                    ["creditDebtMicros"] = 0L,
                    ["cycleUsageCreditMicros"] = 0L,
                    ["activeRequests"] = 0L,
                    ["rateWindowCount"] = 0L,
                    ["rateWindowStartedAt"] = now,
                    ["createdAt"] = now,
                    ["updatedAt"] = now,
                });
            }
            else
            {
                transaction.Update(accountRef, new Dictionary<string, object>
                {
                    ["email"] = caller.Email.Trim().ToLowerInvariant(),
                    ["displayName"] = caller.DisplayName,
                    ["updatedAt"] = now,
                });
            }
            return 0;
        }, cancellationToken: cancellationToken);
    }

    public async Task<AccountSnapshot> GetAccountAsync(string userId, CancellationToken cancellationToken)
    {
        var accountSnapshot = await Account(userId).GetSnapshotAsync(cancellationToken);
        if (!accountSnapshot.Exists)
            throw new KeyNotFoundException("Account not found.");

        var devicesTask = Account(userId).Collection("devices")
            .OrderByDescending("lastSeenAt").Limit(20).GetSnapshotAsync(cancellationToken);
        var usageTask = Account(userId).Collection("usage")
            .OrderByDescending("createdAt").Limit(20).GetSnapshotAsync(cancellationToken);
        var purchasesTask = Account(userId).Collection("purchases")
            .OrderByDescending("createdAt").Limit(20).GetSnapshotAsync(cancellationToken);
        await Task.WhenAll(devicesTask, usageTask, purchasesTask);

        var account = accountSnapshot.ToDictionary();
        var devices = devicesTask.Result.Documents.Select(document =>
        {
            var data = document.ToDictionary();
            return new DeviceSnapshot(
                document.Id,
                String(data, "name", "Windows device"),
                String(data, "status", "active"),
                Date(data, "lastSeenAt"),
                Date(data, "createdAt"));
        }).ToArray();
        var usage = usageTask.Result.Documents.Select(document =>
        {
            var data = document.ToDictionary();
            return new UsageSnapshot(
                document.Id,
                String(data, "model", ""),
                String(data, "outcome", ""),
                Long(data, "chargedCreditMicros"),
                Long(data, "inputTokens"),
                Long(data, "outputTokens"),
                Date(data, "createdAt"));
        }).ToArray();
        var stalePurchaseUpdates = new List<Task>();
        var purchases = purchasesTask.Result.Documents.Select(document =>
        {
            var data = document.ToDictionary();
            var currentStatus = String(data, "status", "pending");
            var displayStatus = StatusAfterAbandonment(
                currentStatus,
                Bool(data, "credited", false),
                Date(data, "createdAt"),
                _clock.UtcNow);
            if (!string.Equals(displayStatus, currentStatus, StringComparison.Ordinal))
            {
                stalePurchaseUpdates.Add(document.Reference.UpdateAsync(
                    new Dictionary<string, object>
                    {
                        ["status"] = displayStatus,
                        ["updatedAt"] = Ts(_clock.UtcNow),
                    },
                    cancellationToken: cancellationToken));
            }
            return new PurchaseSnapshot(
                document.Id,
                String(data, "productSku", ""),
                String(data, "provider", "razorpay"),
                displayStatus,
                Long(data, "amountMinor"),
                String(data, "currency", "INR"),
                Date(data, "createdAt"),
                Bool(data, "credited", false));
        }).ToArray();

        if (stalePurchaseUpdates.Count > 0)
            await Task.WhenAll(stalePurchaseUpdates);

        return Snapshot(accountSnapshot.Id, account, devices, usage, purchases);
    }

    public async Task<ReservationResult> ReserveAsync(
        CallerIdentity caller,
        string requestId,
        string model,
        string pricingVersion,
        long reserveCreditMicros,
        long taskCapCreditMicros,
        int requestsPerMinute,
        int maxConcurrent,
        long globalDailyCap,
        CancellationToken cancellationToken)
    {
        if (reserveCreditMicros <= 0 || reserveCreditMicros > taskCapCreditMicros)
            throw new InvalidOperationException("The request exceeds its approved task budget.");

        var now = _clock.UtcNow;
        await RecoverExpiredReservationsAsync(caller.UserId, now, cancellationToken);
        var accountRef = Account(caller.UserId);
        var reservationRef = accountRef.Collection("reservations").Document(SafeId(requestId));
        var idempotencyRef = _db.Collection("idempotency")
            .Document(Crypto.Sha256($"usage-reserve:{caller.UserId}:{requestId}"));
        var leaseSeconds = Math.Max(_options.MaxRequestSeconds, _options.LiveSessionMaxSeconds)
            + _options.ReservationLeaseGraceSeconds;
        var expiresAt = now.AddSeconds(leaseSeconds);
        var globalUsageDate = now.ToString("yyyyMMdd");
        var globalRef = _db.Collection("globalUsage").Document(globalUsageDate);

        async Task<bool> ExecuteReservationTransactionAsync() =>
            await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            if (!accountSnapshot.Exists)
                throw new KeyNotFoundException("Account not found.");
            var idempotencySnapshot = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            if (idempotencySnapshot.Exists)
                return false;

            var account = accountSnapshot.ToDictionary();
            if (!string.Equals(String(account, "status", "active"), "active", StringComparison.Ordinal))
                throw new InvalidOperationException("The account is not active.");
            var available = Long(account, "availableCreditMicros");
            if (available < reserveCreditMicros)
                throw new InvalidOperationException("No usable credits are available. Add credits to continue.");

            var activeRequests = Long(account, "activeRequests");
            if (activeRequests >= maxConcurrent)
                throw new InvalidOperationException("Another AI request is already running for this account.");

            var windowStartedAt = Date(account, "rateWindowStartedAt");
            var rateCount = Long(account, "rateWindowCount");
            if (now - windowStartedAt >= TimeSpan.FromMinutes(1))
            {
                windowStartedAt = now;
                rateCount = 0;
            }
            if (rateCount >= requestsPerMinute)
                throw new InvalidOperationException("The per-minute request limit has been reached. Try again shortly.");

            var globalSnapshot = await transaction.GetSnapshotAsync(globalRef, cancellationToken);
            var global = globalSnapshot.Exists ? globalSnapshot.ToDictionary() : [];
            var globalCommitted = Long(global, "settledCreditMicros") + Long(global, "reservedCreditMicros");
            if (globalCommitted + reserveCreditMicros > globalDailyCap)
                throw new InvalidOperationException("The staging daily spending cap has been reached.");

            var eventId = $"reserve_{SafeId(requestId)}";
            transaction.Create(reservationRef, new Dictionary<string, object>
            {
                ["requestId"] = requestId,
                ["userId"] = caller.UserId,
                ["reservedCreditMicros"] = reserveCreditMicros,
                ["model"] = model,
                ["pricingVersion"] = pricingVersion,
                ["globalUsageDate"] = globalUsageDate,
                ["status"] = "reserved",
                ["expiresAt"] = Ts(expiresAt),
                ["createdAt"] = Ts(now),
                ["updatedAt"] = Ts(now),
            });
            transaction.Create(accountRef.Collection("ledger").Document(eventId), LedgerMap(new LedgerEntry(
                eventId,
                LedgerTypes.UsageReservation,
                -reserveCreditMicros,
                reserveCreditMicros,
                "usage",
                requestId,
                idempotencyRef.Id,
                caller.UserId,
                "AI request budget reserved",
                now)));
            transaction.Create(idempotencyRef, new Dictionary<string, object>
            {
                ["scope"] = "usage-reserve",
                ["userId"] = caller.UserId,
                ["resourceId"] = requestId,
                ["createdAt"] = Ts(now),
            });
            transaction.Set(globalRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = FieldValue.Increment(reserveCreditMicros),
                ["settledCreditMicros"] = Long(global, "settledCreditMicros"),
                ["updatedAt"] = Ts(now),
            }, SetOptions.MergeAll);
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["availableCreditMicros"] = available - reserveCreditMicros,
                ["reservedCreditMicros"] = Long(account, "reservedCreditMicros") + reserveCreditMicros,
                ["activeRequests"] = activeRequests + 1,
                ["rateWindowStartedAt"] = Ts(windowStartedAt),
                ["rateWindowCount"] = rateCount + 1,
                ["updatedAt"] = Ts(now),
            });
            return true;
        }, cancellationToken: cancellationToken);

        var created = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                created = await ExecuteReservationTransactionAsync();
                break;
            }
            catch (RpcException exception) when (
                exception.StatusCode == StatusCode.Aborted
                && attempt < _options.MaxRetries)
            {
                // Firestore has already rolled the transaction back. A short,
                // bounded retry re-reads the authoritative balance and either
                // reserves safely or returns the real account-level blocker.
                await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1)), cancellationToken);
            }
        }

        var accountResult = await GetAccountAsync(caller.UserId, cancellationToken);
        var reservationSnapshot = await reservationRef.GetSnapshotAsync(cancellationToken);
        var reservation = ReservationFrom(reservationSnapshot);
        return new ReservationResult(created, reservation, accountResult);
    }

    public async Task<SettlementResult> SettleAsync(
        string userId,
        string requestId,
        ProviderUsage usage,
        long actualCreditMicros,
        long providerCostUsdMicros,
        string outcome,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var reservationRef = accountRef.Collection("reservations").Document(SafeId(requestId));
        var idempotencyRef = _db.Collection("idempotency")
            .Document(Crypto.Sha256($"usage-settle:{userId}:{requestId}"));
        var now = _clock.UtcNow;

        var result = await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var reservationSnapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            var idempotencySnapshot = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            if (!accountSnapshot.Exists || !reservationSnapshot.Exists)
                throw new InvalidOperationException("The usage reservation no longer exists.");
            var account = accountSnapshot.ToDictionary();
            var reservation = reservationSnapshot.ToDictionary();
            var globalRef = _db.Collection("globalUsage").Document(
                String(reservation, "globalUsageDate", now.ToString("yyyyMMdd")));
            var reserved = Long(reservation, "reservedCreditMicros");
            if (idempotencySnapshot.Exists
                || string.Equals(String(reservation, "status", ""), "settled", StringComparison.Ordinal))
            {
                return (Settled: false, Charged: Long(reservation, "chargedCreditMicros"), Released: Long(reservation, "releasedCreditMicros"));
            }

            var charged = Math.Clamp(actualCreditMicros, 0, reserved);
            var released = reserved - charged;
            var eventId = $"settle_{SafeId(requestId)}";
            transaction.Create(accountRef.Collection("ledger").Document(eventId), LedgerMap(new LedgerEntry(
                eventId,
                LedgerTypes.UsageSettlement,
                released,
                -reserved,
                "usage",
                requestId,
                idempotencyRef.Id,
                "system",
                outcome,
                now,
                new Dictionary<string, object?>
                {
                    ["providerCostUsdMicros"] = providerCostUsdMicros,
                    ["chargedCreditMicros"] = charged,
                })));
            transaction.Create(accountRef.Collection("usage").Document(SafeId(requestId)), new Dictionary<string, object>
            {
                ["requestId"] = requestId,
                ["model"] = String(reservation, "model", ""),
                ["pricingVersion"] = String(reservation, "pricingVersion", ""),
                ["outcome"] = outcome,
                ["inputTokens"] = usage.InputTokens,
                ["outputTokens"] = usage.OutputTokens,
                ["cachedInputTokens"] = usage.CachedInputTokens,
                ["reasoningTokens"] = usage.ReasoningTokens,
                ["imageTokens"] = usage.ImageTokens,
                ["audioTokens"] = usage.AudioTokens,
                ["totalTokens"] = usage.TotalTokens,
                ["providerRequestId"] = usage.ProviderRequestId ?? "",
                ["providerModelVersion"] = usage.ProviderModelVersion ?? "",
                ["providerCostUsdMicros"] = providerCostUsdMicros,
                ["chargedCreditMicros"] = charged,
                ["createdAt"] = Ts(now),
            });
            transaction.Create(idempotencyRef, new Dictionary<string, object>
            {
                ["scope"] = "usage-settle",
                ["userId"] = userId,
                ["resourceId"] = requestId,
                ["createdAt"] = Ts(now),
            });
            transaction.Update(reservationRef, new Dictionary<string, object>
            {
                ["status"] = "settled",
                ["chargedCreditMicros"] = charged,
                ["releasedCreditMicros"] = released,
                ["outcome"] = outcome,
                ["updatedAt"] = Ts(now),
            });
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["availableCreditMicros"] = Long(account, "availableCreditMicros") + released,
                ["reservedCreditMicros"] = Math.Max(0, Long(account, "reservedCreditMicros") - reserved),
                ["cycleUsageCreditMicros"] = Long(account, "cycleUsageCreditMicros") + charged,
                ["activeRequests"] = Math.Max(0, Long(account, "activeRequests") - 1),
                ["updatedAt"] = Ts(now),
            });
            transaction.Set(globalRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = FieldValue.Increment(-reserved),
                ["settledCreditMicros"] = FieldValue.Increment(charged),
                ["updatedAt"] = Ts(now),
            }, SetOptions.MergeAll);
            return (Settled: true, Charged: charged, Released: released);
        }, cancellationToken: cancellationToken);

        return new SettlementResult(
            result.Settled,
            result.Charged,
            result.Released,
            await GetAccountAsync(userId, cancellationToken));
    }

    public async Task MarkReservationProviderStartedAsync(
        string userId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var reservationRef = Account(userId)
            .Collection("reservations")
            .Document(SafeId(requestId));
        var now = _clock.UtcNow;
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            if (!snapshot.Exists)
                throw new InvalidOperationException("The usage reservation no longer exists.");
            var data = snapshot.ToDictionary();
            if (!string.Equals(String(data, "status", ""), "reserved", StringComparison.Ordinal))
                throw new InvalidOperationException("The usage reservation is not active.");
            if (data.ContainsKey("providerStartedAt"))
                return 0;
            transaction.Update(reservationRef, new Dictionary<string, object>
            {
                ["providerStartedAt"] = Ts(now),
                ["updatedAt"] = Ts(now),
            });
            return 0;
        }, cancellationToken: cancellationToken);
    }

    public async Task ReleaseReservationAsync(
        string userId,
        string requestId,
        string outcome,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var reservationRef = accountRef.Collection("reservations").Document(SafeId(requestId));
        var idempotencyRef = _db.Collection("idempotency")
            .Document(Crypto.Sha256($"usage-release:{userId}:{requestId}"));
        var now = _clock.UtcNow;

        await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var reservationSnapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            var idempotencySnapshot = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            if (!accountSnapshot.Exists || !reservationSnapshot.Exists || idempotencySnapshot.Exists)
                return 0;
            var reservation = reservationSnapshot.ToDictionary();
            var status = String(reservation, "status", "");
            if (!string.Equals(status, "reserved", StringComparison.Ordinal)
                && !string.Equals(status, "reconciliation_pending", StringComparison.Ordinal))
                return 0;
            var account = accountSnapshot.ToDictionary();
            var reserved = Long(reservation, "reservedCreditMicros");
            var globalRef = _db.Collection("globalUsage").Document(
                String(reservation, "globalUsageDate", now.ToString("yyyyMMdd")));
            var eventId = $"release_{SafeId(requestId)}";
            transaction.Create(accountRef.Collection("ledger").Document(eventId), LedgerMap(new LedgerEntry(
                eventId,
                LedgerTypes.ReservationRelease,
                reserved,
                -reserved,
                "usage",
                requestId,
                idempotencyRef.Id,
                "system",
                outcome,
                now)));
            transaction.Create(idempotencyRef, new Dictionary<string, object>
            {
                ["scope"] = "usage-release",
                ["userId"] = userId,
                ["resourceId"] = requestId,
                ["createdAt"] = Ts(now),
            });
            transaction.Update(reservationRef, new Dictionary<string, object>
            {
                ["status"] = "released",
                ["releasedCreditMicros"] = reserved,
                ["outcome"] = outcome,
                ["updatedAt"] = Ts(now),
            });
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["availableCreditMicros"] = Long(account, "availableCreditMicros") + reserved,
                ["reservedCreditMicros"] = Math.Max(0, Long(account, "reservedCreditMicros") - reserved),
                ["activeRequests"] = Math.Max(0, Long(account, "activeRequests") - 1),
                ["updatedAt"] = Ts(now),
            });
            transaction.Set(globalRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = FieldValue.Increment(-reserved),
                ["updatedAt"] = Ts(now),
            }, SetOptions.MergeAll);
            return 0;
        }, cancellationToken: cancellationToken);
    }

    public async Task MarkReservationForReconciliationAsync(
        string userId,
        string requestId,
        string outcome,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var reservationRef = accountRef.Collection("reservations").Document(SafeId(requestId));
        var now = _clock.UtcNow;
        var auditRef = _db.Collection("auditEvents")
            .Document(Crypto.Sha256($"reservation-reconcile:{userId}:{requestId}"));
        await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var reservationSnapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            if (!accountSnapshot.Exists || !reservationSnapshot.Exists)
                return 0;
            var reservation = reservationSnapshot.ToDictionary();
            if (!string.Equals(String(reservation, "status", ""), "reserved", StringComparison.Ordinal)
                || !reservation.ContainsKey("providerStartedAt"))
                return 0;
            var account = accountSnapshot.ToDictionary();
            transaction.Update(reservationRef, new Dictionary<string, object>
            {
                ["status"] = "reconciliation_pending",
                ["outcome"] = outcome,
                ["updatedAt"] = Ts(now),
            });
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["activeRequests"] = Math.Max(0, Long(account, "activeRequests") - 1),
                ["updatedAt"] = Ts(now),
            });
            transaction.Set(auditRef, new Dictionary<string, object>
            {
                ["event"] = "reservation_reconciliation_pending",
                ["userId"] = userId,
                ["requestId"] = requestId,
                ["outcome"] = outcome,
                ["createdAt"] = Ts(now),
            }, SetOptions.MergeAll);
            return 0;
        }, cancellationToken: cancellationToken);
    }

    private async Task RecoverExpiredReservationsAsync(
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reservations = await Account(userId)
            .Collection("reservations")
            .WhereEqualTo("status", "reserved")
            .Limit(25)
            .GetSnapshotAsync(cancellationToken);
        var fallbackLease = TimeSpan.FromSeconds(
            Math.Max(_options.MaxRequestSeconds, _options.LiveSessionMaxSeconds)
            + _options.ReservationLeaseGraceSeconds);
        foreach (var snapshot in reservations.Documents)
        {
            var data = snapshot.ToDictionary();
            var expiresAt = Date(data, "expiresAt");
            if (expiresAt == DateTimeOffset.UnixEpoch)
                expiresAt = Date(data, "createdAt").Add(fallbackLease);
            if (expiresAt > now)
                continue;

            if (!data.ContainsKey("providerStartedAt"))
            {
                await ReleaseReservationAsync(
                    userId,
                    String(data, "requestId", snapshot.Id),
                    "expired_before_provider_start",
                    cancellationToken);
                continue;
            }

            await MarkReservationForReconciliationAsync(
                userId,
                String(data, "requestId", snapshot.Id),
                "worker_lease_expired_after_provider_start",
                cancellationToken);
        }
    }

    public async Task<DesktopLoginAttempt> CreateLoginAttemptAsync(
        string stateHash,
        string deviceNonceHash,
        string pkceChallenge,
        string callbackUri,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var attemptId = Crypto.RandomToken(18);
        var attemptRef = _db.Collection("desktopLoginAttempts").Document(attemptId);
        var now = _clock.UtcNow;
        await attemptRef.CreateAsync(new Dictionary<string, object>
        {
            ["stateHash"] = stateHash,
            ["deviceNonceHash"] = deviceNonceHash,
            ["pkceChallenge"] = pkceChallenge,
            ["callbackUri"] = callbackUri,
            ["status"] = "pending",
            ["expiresAt"] = Ts(expiresAt),
            ["createdAt"] = Ts(now),
            ["updatedAt"] = Ts(now),
        }, cancellationToken);
        return new DesktopLoginAttempt(
            attemptId, stateHash, deviceNonceHash, pkceChallenge, callbackUri, "pending", expiresAt);
    }

    public async Task<(DesktopLoginAttempt Attempt, string AuthorizationCode)> AuthorizeLoginAttemptAsync(
        string attemptId,
        string stateHash,
        string deviceNonceHash,
        string userId,
        CancellationToken cancellationToken)
    {
        var attemptRef = _db.Collection("desktopLoginAttempts").Document(SafeId(attemptId));
        var authorizationCode = Crypto.RandomToken(32);
        var codeHash = Crypto.Sha256(authorizationCode);
        var now = _clock.UtcNow;
        var result = await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(attemptRef, cancellationToken);
            if (!snapshot.Exists)
                throw new InvalidOperationException("The desktop login request was not found.");
            var data = snapshot.ToDictionary();
            if (!string.Equals(String(data, "status", ""), "pending", StringComparison.Ordinal)
                || Date(data, "expiresAt") <= now
                || !FixedHash(String(data, "stateHash", ""), stateHash)
                || !FixedHash(String(data, "deviceNonceHash", ""), deviceNonceHash))
                throw new InvalidOperationException("The desktop login request is invalid or expired.");
            transaction.Update(attemptRef, new Dictionary<string, object>
            {
                ["status"] = "authorized",
                ["userId"] = userId,
                ["authorizationCodeHash"] = codeHash,
                ["authorizationCodeExpiresAt"] = Ts(now.AddMinutes(1)),
                ["updatedAt"] = Ts(now),
            });
            return AttemptFrom(snapshot, "authorized", userId, codeHash);
        }, cancellationToken: cancellationToken);
        return (result, authorizationCode);
    }

    public async Task<DesktopSessionTokens> ExchangeLoginAttemptAsync(
        string attemptId,
        string stateHash,
        string deviceNonceHash,
        string authorizationCodeHash,
        string pkceVerifier,
        string deviceName,
        string deviceInstallationId,
        TimeSpan accessLifetime,
        TimeSpan refreshLifetime,
        CancellationToken cancellationToken)
    {
        var attemptRef = _db.Collection("desktopLoginAttempts").Document(SafeId(attemptId));
        var now = _clock.UtcNow;
        var sessionId = Crypto.RandomToken(18);
        var accessToken = $"kwt_at_{sessionId}.{Crypto.RandomToken(32)}";
        var refreshToken = $"kwt_rt_{sessionId}.{Crypto.RandomToken(48)}";
        var accessExpiresAt = now.Add(accessLifetime);
        var refreshExpiresAt = now.Add(refreshLifetime);

        return await _db.RunTransactionAsync(async transaction =>
        {
            var attemptSnapshot = await transaction.GetSnapshotAsync(attemptRef, cancellationToken);
            if (!attemptSnapshot.Exists)
                throw new InvalidOperationException("The desktop login request was not found.");
            var attempt = attemptSnapshot.ToDictionary();
            var userId = String(attempt, "userId", "");
            if (string.IsNullOrWhiteSpace(userId)
                || !string.Equals(String(attempt, "status", ""), "authorized", StringComparison.Ordinal)
                || Date(attempt, "authorizationCodeExpiresAt") <= now
                || !FixedHash(String(attempt, "stateHash", ""), stateHash)
                || !FixedHash(String(attempt, "deviceNonceHash", ""), deviceNonceHash)
                || !FixedHash(String(attempt, "authorizationCodeHash", ""), authorizationCodeHash)
                || !FixedHash(String(attempt, "pkceChallenge", ""), Crypto.PkceChallenge(pkceVerifier)))
                throw new InvalidOperationException("The desktop authorization code is invalid, expired, or already used.");

            var accountRef = Account(userId);
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            if (!accountSnapshot.Exists)
                throw new InvalidOperationException("The account is unavailable.");
            var deviceId = Crypto.Sha256($"{userId}:{deviceInstallationId}")[..32];
            var sessionRef = _db.Collection("desktopSessions").Document(sessionId);
            var deviceRef = accountRef.Collection("devices").Document(deviceId);
            transaction.Create(sessionRef, new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["userId"] = userId,
                ["deviceId"] = deviceId,
                ["accessTokenHash"] = Crypto.Sha256(accessToken),
                ["refreshTokenHash"] = Crypto.Sha256(refreshToken),
                ["accessExpiresAt"] = Ts(accessExpiresAt),
                ["refreshExpiresAt"] = Ts(refreshExpiresAt),
                ["status"] = "active",
                ["createdAt"] = Ts(now),
                ["lastSeenAt"] = Ts(now),
            });
            transaction.Set(deviceRef, new Dictionary<string, object>
            {
                ["name"] = CleanDeviceName(deviceName),
                ["status"] = "active",
                ["createdAt"] = Ts(now),
                ["lastSeenAt"] = Ts(now),
            }, SetOptions.MergeAll);
            transaction.Update(attemptRef, new Dictionary<string, object>
            {
                ["status"] = "consumed",
                ["authorizationCodeHash"] = "consumed",
                ["consumedAt"] = Ts(now),
                ["updatedAt"] = Ts(now),
            });
            return new DesktopSessionTokens(
                accessToken, accessExpiresAt, refreshToken, refreshExpiresAt, sessionId, deviceId);
        }, cancellationToken: cancellationToken);
    }

    public async Task<CallerIdentity?> AuthenticateDesktopTokenAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var sessionId = TokenSessionId(accessToken, "kwt_at_");
        if (sessionId is null)
            return null;
        var sessionRef = _db.Collection("desktopSessions").Document(sessionId);
        var sessionSnapshot = await sessionRef.GetSnapshotAsync(cancellationToken);
        if (!sessionSnapshot.Exists)
            return null;
        var session = sessionSnapshot.ToDictionary();
        if (!string.Equals(String(session, "status", ""), "active", StringComparison.Ordinal)
            || Date(session, "accessExpiresAt") <= _clock.UtcNow
            || !FixedHash(String(session, "accessTokenHash", ""), Crypto.Sha256(accessToken)))
            return null;
        var userId = String(session, "userId", "");
        var accountSnapshot = await Account(userId).GetSnapshotAsync(cancellationToken);
        if (!accountSnapshot.Exists)
            return null;
        var account = accountSnapshot.ToDictionary();
        if (!string.Equals(String(account, "status", "active"), "active", StringComparison.Ordinal))
            return null;
        return new CallerIdentity(
            userId,
            String(account, "email", ""),
            String(account, "displayName", ""),
            "desktop",
            sessionId,
            String(session, "deviceId", ""));
    }

    public async Task<DesktopSessionTokens> RefreshSessionAsync(
        string refreshToken,
        TimeSpan accessLifetime,
        TimeSpan refreshLifetime,
        CancellationToken cancellationToken)
    {
        var sessionId = TokenSessionId(refreshToken, "kwt_rt_")
            ?? throw new InvalidOperationException("The refresh session is invalid.");
        var sessionRef = _db.Collection("desktopSessions").Document(sessionId);
        var now = _clock.UtcNow;
        var newAccess = $"kwt_at_{sessionId}.{Crypto.RandomToken(32)}";
        var newRefresh = $"kwt_rt_{sessionId}.{Crypto.RandomToken(48)}";
        var accessExpires = now.Add(accessLifetime);
        var refreshExpires = now.Add(refreshLifetime);
        return await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(sessionRef, cancellationToken);
            if (!snapshot.Exists)
                throw new InvalidOperationException("The refresh session was not found.");
            var data = snapshot.ToDictionary();
            if (!string.Equals(String(data, "status", ""), "active", StringComparison.Ordinal)
                || Date(data, "refreshExpiresAt") <= now
                || !FixedHash(String(data, "refreshTokenHash", ""), Crypto.Sha256(refreshToken)))
                throw new InvalidOperationException("The refresh session is expired or revoked.");
            transaction.Update(sessionRef, new Dictionary<string, object>
            {
                ["accessTokenHash"] = Crypto.Sha256(newAccess),
                ["refreshTokenHash"] = Crypto.Sha256(newRefresh),
                ["accessExpiresAt"] = Ts(accessExpires),
                ["refreshExpiresAt"] = Ts(refreshExpires),
                ["lastSeenAt"] = Ts(now),
            });
            return new DesktopSessionTokens(
                newAccess,
                accessExpires,
                newRefresh,
                refreshExpires,
                sessionId,
                String(data, "deviceId", ""));
        }, cancellationToken: cancellationToken);
    }

    public async Task RevokeSessionAsync(
        string userId,
        string? sessionId,
        bool allDevices,
        CancellationToken cancellationToken)
    {
        if (!allDevices && !string.IsNullOrWhiteSpace(sessionId))
        {
            var sessionRef = _db.Collection("desktopSessions").Document(SafeId(sessionId));
            var snapshot = await sessionRef.GetSnapshotAsync(cancellationToken);
            if (snapshot.Exists && string.Equals(snapshot.GetValue<string>("userId"), userId, StringComparison.Ordinal))
                await sessionRef.UpdateAsync("status", "revoked", cancellationToken: cancellationToken);
            return;
        }

        var sessions = await _db.Collection("desktopSessions")
            .WhereEqualTo("userId", userId)
            .WhereEqualTo("status", "active")
            .GetSnapshotAsync(cancellationToken);
        var batch = _db.StartBatch();
        foreach (var session in sessions.Documents)
            batch.Update(session.Reference, "status", "revoked");
        await batch.CommitAsync(cancellationToken);
    }

    public async Task RevokeDeviceAsync(string userId, string deviceId, CancellationToken cancellationToken)
    {
        var sessions = await _db.Collection("desktopSessions")
            .WhereEqualTo("userId", userId)
            .WhereEqualTo("deviceId", deviceId)
            .WhereEqualTo("status", "active")
            .GetSnapshotAsync(cancellationToken);
        var batch = _db.StartBatch();
        foreach (var session in sessions.Documents)
            batch.Update(session.Reference, "status", "revoked");
        batch.Set(Account(userId).Collection("devices").Document(SafeId(deviceId)),
            new Dictionary<string, object> { ["status"] = "revoked", ["updatedAt"] = Ts(_clock.UtcNow) },
            SetOptions.MergeAll);
        await batch.CommitAsync(cancellationToken);
    }

    public async Task<string?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var results = await _db.Collection("accounts")
            .WhereEqualTo("email", email.Trim().ToLowerInvariant())
            .Limit(1)
            .GetSnapshotAsync(cancellationToken);
        return results.Documents.FirstOrDefault()?.Id;
    }

    public async Task<AccountSnapshot> GrantCreditsAsync(
        string userId,
        long creditMicros,
        string reason,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken)
    {
        if (creditMicros <= 0 || creditMicros > 10_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(creditMicros));
        var accountRef = Account(userId);
        var idempotencyRef = _db.Collection("idempotency")
            .Document(Crypto.Sha256($"admin-grant:{userId}:{idempotencyKey}"));
        var now = _clock.UtcNow;
        await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var idempotencySnapshot = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            if (!accountSnapshot.Exists)
                throw new KeyNotFoundException("Recipient account not found.");
            if (idempotencySnapshot.Exists)
                return 0;
            var account = accountSnapshot.ToDictionary();
            var eventId = $"grant_{Crypto.RandomToken(12)}";
            transaction.Create(accountRef.Collection("ledger").Document(eventId), LedgerMap(new LedgerEntry(
                eventId,
                LedgerTypes.AdminTestCredit,
                creditMicros,
                0,
                "admin-grant",
                idempotencyKey,
                idempotencyRef.Id,
                actor,
                reason,
                now)));
            transaction.Create(idempotencyRef, new Dictionary<string, object>
            {
                ["scope"] = "admin-grant",
                ["userId"] = userId,
                ["actor"] = actor,
                ["resourceId"] = eventId,
                ["createdAt"] = Ts(now),
            });
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["availableCreditMicros"] = Long(account, "availableCreditMicros") + creditMicros,
                ["updatedAt"] = Ts(now),
            });
            return 0;
        }, cancellationToken: cancellationToken);
        return await GetAccountAsync(userId, cancellationToken);
    }

    public async Task<bool> BeginPurchaseAsync(
        string userId,
        string purchaseId,
        CatalogProduct product,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var purchaseRef = Account(userId).Collection("purchases").Document(SafeId(purchaseId));
        var idempotencyRef = _db.Collection("idempotency")
            .Document(Crypto.Sha256($"checkout:{userId}:{idempotencyKey}"));
        var now = _clock.UtcNow;
        return await _db.RunTransactionAsync(async transaction =>
        {
            var purchase = await transaction.GetSnapshotAsync(purchaseRef, cancellationToken);
            var idempotency = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            if (purchase.Exists || idempotency.Exists)
                return false;
            transaction.Create(purchaseRef, new Dictionary<string, object>
            {
                ["purchaseId"] = purchaseId,
                ["productSku"] = product.Sku,
                ["kind"] = product.Kind,
                ["provider"] = "razorpay",
                ["providerObjectId"] = "",
                ["providerObjectType"] = "",
                ["status"] = "creating",
                ["amountMinor"] = product.AmountMinor,
                ["currency"] = product.Currency,
                ["creditMicros"] = product.CreditMicros,
                ["credited"] = false,
                ["createdAt"] = Ts(now),
                ["updatedAt"] = Ts(now),
            });
            transaction.Create(idempotencyRef, new Dictionary<string, object>
            {
                ["scope"] = "checkout",
                ["userId"] = userId,
                ["resourceId"] = purchaseId,
                ["createdAt"] = Ts(now),
            });
            return true;
        }, cancellationToken: cancellationToken);
    }

    public Task AttachPurchaseProviderObjectAsync(
        string userId,
        string purchaseId,
        string providerObjectId,
        string providerObjectType,
        CancellationToken cancellationToken) =>
        Account(userId).Collection("purchases").Document(SafeId(purchaseId)).UpdateAsync(
            new Dictionary<string, object>
            {
                ["providerObjectId"] = providerObjectId,
                ["providerObjectType"] = providerObjectType,
                ["status"] = "pending",
                ["updatedAt"] = Ts(_clock.UtcNow),
            },
            cancellationToken: cancellationToken);

    public Task MarkPurchaseFailedAsync(
        string userId,
        string purchaseId,
        string outcome,
        CancellationToken cancellationToken) =>
        Account(userId).Collection("purchases").Document(SafeId(purchaseId)).UpdateAsync(
            new Dictionary<string, object>
            {
                ["status"] = "provider_failed",
                ["failureCategory"] = outcome.Length > 80 ? outcome[..80] : outcome,
                ["updatedAt"] = Ts(_clock.UtcNow),
            },
            cancellationToken: cancellationToken);

    public Task CancelPendingPurchaseAsync(
        string userId,
        string purchaseId,
        CancellationToken cancellationToken)
    {
        var purchaseRef = Account(userId).Collection("purchases").Document(SafeId(purchaseId));
        return _db.RunTransactionAsync(async transaction =>
        {
            var purchase = await transaction.GetSnapshotAsync(purchaseRef, cancellationToken);
            if (!purchase.Exists)
                throw new KeyNotFoundException("The checkout purchase does not exist for this account.");
            var data = purchase.ToDictionary();
            var currentStatus = String(data, "status", "pending");
            var nextStatus = StatusAfterCheckoutCancellation(
                currentStatus,
                Bool(data, "credited", false));
            if (string.Equals(currentStatus, nextStatus, StringComparison.Ordinal))
                return;
            transaction.Update(purchaseRef, new Dictionary<string, object>
            {
                ["status"] = nextStatus,
                ["updatedAt"] = Ts(_clock.UtcNow),
            });
        }, cancellationToken: cancellationToken);
    }

    internal static string StatusAfterCheckoutCancellation(string? currentStatus, bool credited)
    {
        if (credited)
            return string.IsNullOrWhiteSpace(currentStatus) ? "paid" : currentStatus;
        return currentStatus?.Trim().ToLowerInvariant() switch
        {
            null or "" or "creating" or "pending" or "verification_pending" => "cancelled",
            _ => currentStatus!,
        };
    }

    internal static string StatusAfterAbandonment(
        string? currentStatus,
        bool credited,
        DateTimeOffset createdAt,
        DateTimeOffset now)
    {
        if (credited || createdAt == DateTimeOffset.UnixEpoch || now - createdAt < TimeSpan.FromHours(24))
            return string.IsNullOrWhiteSpace(currentStatus) ? "pending" : currentStatus;
        return currentStatus?.Trim().ToLowerInvariant() switch
        {
            null or "" or "creating" or "pending" or "verification_pending" or "authorized" => "expired",
            _ => currentStatus!,
        };
    }

    public Task MarkCheckoutSignatureVerifiedAsync(
        string userId,
        string purchaseId,
        string providerObjectId,
        string providerPaymentId,
        CancellationToken cancellationToken)
    {
        var purchaseRef = Account(userId).Collection("purchases").Document(SafeId(purchaseId));
        return _db.RunTransactionAsync(async transaction =>
        {
            var purchase = await transaction.GetSnapshotAsync(purchaseRef, cancellationToken);
            if (!purchase.Exists)
                throw new KeyNotFoundException("The checkout purchase does not exist for this account.");
            var purchaseData = purchase.ToDictionary();
            if (!string.Equals(
                    String(purchaseData, "providerObjectId", ""),
                    providerObjectId,
                    StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The checkout does not match this purchase.");
            var currentStatus = String(purchaseData, "status", "pending");
            transaction.Update(purchaseRef, new Dictionary<string, object>
            {
                ["checkoutSignatureVerified"] = true,
                ["providerPaymentId"] = providerPaymentId,
                // Razorpay can deliver an authoritative webhook before Checkout.js
                // posts its signature confirmation. Never move an already advanced
                // purchase back to a waiting state in that race.
                ["status"] = StatusAfterCheckoutSignature(currentStatus),
                ["updatedAt"] = Ts(_clock.UtcNow),
            });
        }, cancellationToken: cancellationToken);
    }

    internal static string StatusAfterCheckoutSignature(string? currentStatus) =>
        currentStatus?.Trim().ToLowerInvariant() switch
        {
            null or "" or "creating" or "pending" or "verification_pending" => "verification_pending",
            _ => currentStatus!,
        };

    public async Task<PaymentWebhookResult> ApplyPaymentWebhookAsync(
        string eventId,
        string eventType,
        string providerObjectId,
        string? providerPaymentId,
        string? providerOrderId,
        string? providerSubscriptionId,
        string? purchaseId,
        string? providerEventObjectId,
        long? providerAmountMinor,
        string status,
        CancellationToken cancellationToken,
        DateTimeOffset? subscriptionCycleEndsAt = null)
    {
        var eventRef = _db.Collection("paymentEvents").Document(Crypto.Sha256($"razorpay:{eventId}"));
        var existingEventSnapshot = await eventRef.GetSnapshotAsync(cancellationToken);
        if (existingEventSnapshot.Exists
            && !string.Equals(
                String(existingEventSnapshot.ToDictionary(), "status", ""),
                "unmatched",
                StringComparison.Ordinal))
            return new PaymentWebhookResult(true, eventId, "duplicate");

        var candidateIds = new[] { providerObjectId, providerOrderId, providerSubscriptionId }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        DocumentSnapshot? purchaseSnapshot = null;
        if (!string.IsNullOrWhiteSpace(purchaseId))
        {
            var query = await _db.CollectionGroup("purchases")
                .WhereEqualTo("purchaseId", purchaseId)
                .Limit(1)
                .GetSnapshotAsync(cancellationToken);
            purchaseSnapshot = query.Documents.FirstOrDefault();
        }
        foreach (var candidate in candidateIds)
        {
            if (purchaseSnapshot is not null)
                break;
            var query = await _db.CollectionGroup("purchases")
                .WhereEqualTo("providerObjectId", candidate)
                .Limit(1)
                .GetSnapshotAsync(cancellationToken);
            purchaseSnapshot = query.Documents.FirstOrDefault();
            if (purchaseSnapshot is not null)
                break;
        }
        if (purchaseSnapshot is null && !string.IsNullOrWhiteSpace(providerPaymentId))
        {
            var query = await _db.CollectionGroup("purchases")
                .WhereEqualTo("providerPaymentId", providerPaymentId)
                .Limit(1)
                .GetSnapshotAsync(cancellationToken);
            purchaseSnapshot = query.Documents.FirstOrDefault();
        }

        if (purchaseSnapshot is null)
        {
            await eventRef.SetAsync(new Dictionary<string, object>
            {
                ["provider"] = "razorpay",
                ["eventId"] = eventId,
                ["eventType"] = eventType,
                ["status"] = "unmatched",
                ["providerObjectId"] = providerObjectId,
                ["createdAt"] = Ts(_clock.UtcNow),
                ["providerEventObjectId"] = providerEventObjectId ?? "",
                ["providerAmountMinor"] = providerAmountMinor ?? 0,
            }, SetOptions.MergeAll, cancellationToken);
            return new PaymentWebhookResult(false, eventId, "unmatched");
        }

        var accountRef = purchaseSnapshot.Reference.Parent.Parent
            ?? throw new InvalidOperationException("The payment account reference is invalid.");
        var userId = accountRef.Id;
        var purchaseRef = purchaseSnapshot.Reference;
        var recurringFulfillmentId = RecurringFulfillmentId(
            eventType,
            providerPaymentId,
            providerEventObjectId);
        var recurringFulfillmentRef = recurringFulfillmentId is null
            ? null
            : _db.Collection("paymentFulfillments").Document(recurringFulfillmentId);
        var now = _clock.UtcNow;
        var outcome = await _db.RunTransactionAsync(async transaction =>
        {
            var existingEvent = await transaction.GetSnapshotAsync(eventRef, cancellationToken);
            if (existingEvent.Exists
                && !string.Equals(
                    String(existingEvent.ToDictionary(), "status", ""),
                    "unmatched",
                    StringComparison.Ordinal))
                return "duplicate";
            var purchase = await transaction.GetSnapshotAsync(purchaseRef, cancellationToken);
            var account = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var recurringFulfillment = recurringFulfillmentRef is null
                ? null
                : await transaction.GetSnapshotAsync(recurringFulfillmentRef, cancellationToken);
            if (!purchase.Exists || !account.Exists)
                throw new InvalidOperationException("The payment account is unavailable.");
            var purchaseData = purchase.ToDictionary();
            var accountData = account.ToDictionary();
            var purchaseKind = String(purchaseData, "kind", "credits");
            var recurringCharge = string.Equals(eventType, "subscription.charged", StringComparison.Ordinal);
            var subscriptionLifecycleEvent = eventType.StartsWith("subscription.", StringComparison.Ordinal)
                && !recurringCharge;
            var recurringAlreadyFulfilled = recurringFulfillment?.Exists == true
                || IsPreviouslyFulfilledRecurringCharge(
                    eventType,
                    providerPaymentId,
                    String(purchaseData, "providerPaymentId", ""),
                    Bool(purchaseData, "credited", false));
            var shouldCredit = IsCreditEvent(eventType, purchaseKind)
                && (recurringCharge
                    ? !recurringAlreadyFulfilled
                    : !Bool(purchaseData, "credited", false));
            var purchaseCredited = Bool(purchaseData, "credited", false);
            var recurringReversalHasEvidence = purchaseKind != "subscription"
                || recurringFulfillment?.Exists == true
                || IsPreviouslyFulfilledRecurringCharge(
                    "subscription.charged",
                    providerPaymentId,
                    String(purchaseData, "providerPaymentId", ""),
                    purchaseCredited);
            var shouldReverse = IsReversalEvent(eventType)
                && purchaseCredited
                && recurringReversalHasEvidence;
            var creditMicros = Long(purchaseData, "creditMicros");
            var reversalBasisCreditMicros = purchaseKind == "subscription"
                && recurringFulfillment?.Exists == true
                    ? Long(recurringFulfillment.ToDictionary(), "creditMicros", creditMicros)
                    : creditMicros;
            var previouslyReversedCreditMicros = purchaseKind == "subscription"
                && recurringFulfillment?.Exists == true
                    ? Long(recurringFulfillment.ToDictionary(), "reversedCreditMicros")
                    : Long(purchaseData, "reversedCreditMicros");
            var purchaseAmountMinor = Long(purchaseData, "amountMinor");
            var ledgerType = eventType.Contains("refund", StringComparison.OrdinalIgnoreCase)
                ? LedgerTypes.Refund
                : eventType.Contains("dispute", StringComparison.OrdinalIgnoreCase)
                    ? LedgerTypes.Chargeback
                    : purchaseKind == "subscription"
                        ? LedgerTypes.MonthlyAllowance
                        : LedgerTypes.PurchaseCredit;
            var requestedReversalCreditMicros = shouldReverse
                ? ReversalCreditMicros(eventType, providerAmountMinor, purchaseAmountMinor, creditMicros)
                : 0;
            var balanceChange = CalculatePaymentBalanceChange(
                Long(accountData, "availableCreditMicros"),
                Long(accountData, "creditDebtMicros"),
                shouldCredit ? creditMicros : 0,
                requestedReversalCreditMicros,
                previouslyReversedCreditMicros,
                reversalBasisCreditMicros);
            var reversalCreditMicros = balanceChange.ReversalCreditMicros;
            var delta = balanceChange.AvailableDeltaMicros;
            if (shouldCredit || reversalCreditMicros > 0)
            {
                var ledgerId = $"payment_{Crypto.Sha256(eventId)[..24]}";
                transaction.Create(accountRef.Collection("ledger").Document(ledgerId), LedgerMap(new LedgerEntry(
                    ledgerId,
                    ledgerType,
                    delta,
                    0,
                    "razorpay-event",
                    eventId,
                    eventRef.Id,
                    "razorpay",
                    eventType,
                    now,
                    new Dictionary<string, object?>
                    {
                        ["purchaseId"] = purchase.Id,
                        ["providerEventObjectId"] = providerEventObjectId,
                        ["providerAmountMinor"] = providerAmountMinor,
                        ["grossCreditMicros"] = shouldCredit ? creditMicros : 0,
                        ["creditDebtMicrosAfter"] = balanceChange.NewDebtMicros,
                    })));
            }
            if (shouldCredit && recurringFulfillmentRef is not null)
                transaction.Create(recurringFulfillmentRef, new Dictionary<string, object>
                {
                    ["provider"] = "razorpay",
                    ["eventType"] = eventType,
                    ["eventId"] = eventId,
                    ["userId"] = userId,
                    ["purchaseId"] = purchase.Id,
                    ["providerPaymentId"] = providerPaymentId ?? "",
                    ["creditMicros"] = creditMicros,
                    ["reversedCreditMicros"] = 0L,
                    ["createdAt"] = Ts(now),
                });

            if (reversalCreditMicros > 0 && recurringFulfillmentRef is not null
                && recurringFulfillment?.Exists == true)
            {
                transaction.Update(recurringFulfillmentRef, new Dictionary<string, object>
                {
                    ["reversedCreditMicros"] = previouslyReversedCreditMicros + reversalCreditMicros,
                    ["updatedAt"] = Ts(now),
                });
            }

            if (shouldCredit || reversalCreditMicros > 0
                || (purchaseKind == "subscription" && subscriptionLifecycleEvent))
            {
                var accountUpdates = new Dictionary<string, object>
                {
                    ["updatedAt"] = Ts(now),
                };
                if (shouldCredit || reversalCreditMicros > 0)
                {
                    accountUpdates["availableCreditMicros"] = Math.Max(
                        0,
                        Long(accountData, "availableCreditMicros") + delta);
                    accountUpdates["creditDebtMicros"] = balanceChange.NewDebtMicros;
                }
                if (recurringCharge && shouldCredit)
                {
                    accountUpdates["cycleUsageCreditMicros"] = 0L;
                    if (subscriptionCycleEndsAt is DateTimeOffset cycleEnd)
                        accountUpdates["cycleEndsAt"] = Ts(cycleEnd);
                }
                if (purchaseKind == "subscription"
                    && (subscriptionLifecycleEvent || recurringCharge))
                {
                    accountUpdates["planId"] = PlanAfterSubscriptionStatus(
                        String(accountData, "planId", "none"),
                        String(purchaseData, "productSku", "none"),
                        status,
                        purchaseCredited || shouldCredit);
                    accountUpdates["subscriptionStatus"] = status;
                }
                transaction.Update(accountRef, accountUpdates);
            }
            var purchaseStatus = StatusAfterWebhook(
                String(purchaseData, "status", "pending"),
                status,
                purchaseKind,
                purchaseCredited,
                eventType);
            transaction.Update(purchaseRef, new Dictionary<string, object>
            {
                ["status"] = purchaseStatus,
                ["providerPaymentId"] = providerPaymentId ?? String(purchaseData, "providerPaymentId", ""),
                ["credited"] = Bool(purchaseData, "credited", false) || shouldCredit,
                ["reversedCreditMicros"] = Long(purchaseData, "reversedCreditMicros") + reversalCreditMicros,
                ["reversed"] = Bool(purchaseData, "reversed", false)
                    || (reversalCreditMicros > 0
                        && Long(purchaseData, "reversedCreditMicros") + reversalCreditMicros >= creditMicros),
                ["updatedAt"] = Ts(now),
            });
            transaction.Set(eventRef, new Dictionary<string, object>
            {
                ["provider"] = "razorpay",
                ["eventId"] = eventId,
                ["eventType"] = eventType,
                ["status"] = status,
                ["userId"] = userId,
                ["purchaseId"] = purchase.Id,
                ["providerObjectId"] = providerObjectId,
                ["providerEventObjectId"] = providerEventObjectId ?? "",
                ["providerAmountMinor"] = providerAmountMinor ?? 0,
                ["subscriptionCycleEndsAtUnix"] = subscriptionCycleEndsAt?.ToUnixTimeSeconds() ?? 0,
                ["createdAt"] = Ts(now),
            });
            return delta switch
            {
                > 0 => "credited",
                < 0 => "reversed",
                _ when recurringAlreadyFulfilled => "duplicate",
                _ => "recorded",
            };
        }, cancellationToken: cancellationToken);
        return new PaymentWebhookResult(outcome == "duplicate", eventId, outcome);
    }

    private DocumentReference Account(string userId) =>
        _db.Collection("accounts").Document(SafeId(userId));

    private static AccountSnapshot Snapshot(
        string userId,
        IDictionary<string, object> data,
        IReadOnlyList<DeviceSnapshot> devices,
        IReadOnlyList<UsageSnapshot> usage,
        IReadOnlyList<PurchaseSnapshot> purchases) => new(
            userId,
            String(data, "email", ""),
            String(data, "displayName", ""),
            String(data, "status", "active"),
            String(data, "planId", "none"),
            Long(data, "availableCreditMicros"),
            Long(data, "reservedCreditMicros"),
            Long(data, "cycleUsageCreditMicros"),
            data.TryGetValue("cycleEndsAt", out var cycle) && cycle is Timestamp cycleTimestamp
                ? new DateTimeOffset(cycleTimestamp.ToDateTime())
                : null,
            devices,
            usage,
            purchases,
            String(data, "subscriptionStatus", "none"));

    private static Reservation ReservationFrom(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists)
            throw new InvalidOperationException("The reservation was not created.");
        var data = snapshot.ToDictionary();
        return new Reservation(
            String(data, "requestId", snapshot.Id),
            String(data, "userId", ""),
            Long(data, "reservedCreditMicros"),
            String(data, "model", ""),
            String(data, "pricingVersion", ""),
            String(data, "status", ""),
            Date(data, "createdAt"),
            Date(data, "expiresAt") is var expiresAt && expiresAt != DateTimeOffset.UnixEpoch
                ? expiresAt
                : null,
            data.ContainsKey("providerStartedAt"));
    }

    private static DesktopLoginAttempt AttemptFrom(
        DocumentSnapshot snapshot,
        string? statusOverride = null,
        string? userId = null,
        string? codeHash = null)
    {
        var data = snapshot.ToDictionary();
        return new DesktopLoginAttempt(
            snapshot.Id,
            String(data, "stateHash", ""),
            String(data, "deviceNonceHash", ""),
            String(data, "pkceChallenge", ""),
            String(data, "callbackUri", ""),
            statusOverride ?? String(data, "status", ""),
            Date(data, "expiresAt"),
            userId ?? String(data, "userId", null),
            codeHash ?? String(data, "authorizationCodeHash", null));
    }

    private static Dictionary<string, object> LedgerMap(LedgerEntry entry) => new()
    {
        ["eventId"] = entry.EventId,
        ["type"] = entry.Type,
        ["availableDeltaMicros"] = entry.AvailableDeltaMicros,
        ["reservedDeltaMicros"] = entry.ReservedDeltaMicros,
        ["referenceType"] = entry.ReferenceType,
        ["referenceId"] = entry.ReferenceId,
        ["idempotencyKey"] = entry.IdempotencyKey,
        ["actor"] = entry.Actor,
        ["reason"] = entry.Reason,
        ["metadata"] = entry.Metadata?.ToDictionary(
                pair => pair.Key,
                pair => (object)(pair.Value ?? ""))
            ?? new Dictionary<string, object>(),
        ["createdAt"] = Ts(entry.CreatedAt),
    };

    internal static bool IsCreditEvent(string eventType, string purchaseKind) =>
        string.Equals(purchaseKind, "subscription", StringComparison.Ordinal)
            ? string.Equals(eventType, "subscription.charged", StringComparison.Ordinal)
            : eventType is "order.paid" or "payment.captured";

    internal static string PlanAfterSubscriptionStatus(
        string currentPlan,
        string productSku,
        string? status,
        bool fulfilled) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "active" when fulfilled => productSku,
            "cancelled" or "completed" or "expired" or "halted" => "none",
            _ => currentPlan,
        };

    internal static string? RecurringFulfillmentId(
        string eventType,
        string? providerPaymentId,
        string? providerEventObjectId)
    {
        if (!string.Equals(eventType, "subscription.charged", StringComparison.Ordinal)
            && !IsReversalEvent(eventType))
            return null;
        var providerChargeId = string.IsNullOrWhiteSpace(providerPaymentId)
            ? providerEventObjectId
            : providerPaymentId;
        return string.IsNullOrWhiteSpace(providerChargeId)
            ? null
            : Crypto.Sha256($"razorpay:subscription-charge:{providerChargeId}");
    }

    internal static bool IsPreviouslyFulfilledRecurringCharge(
        string eventType,
        string? providerPaymentId,
        string? recordedPaymentId,
        bool purchaseCredited) =>
        purchaseCredited
        && string.Equals(eventType, "subscription.charged", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(providerPaymentId)
        && string.Equals(providerPaymentId, recordedPaymentId, StringComparison.Ordinal);

    internal static string StatusAfterWebhook(
        string currentStatus,
        string incomingStatus,
        string purchaseKind,
        bool purchaseCredited,
        string eventType)
    {
        if (eventType.Contains("refund", StringComparison.OrdinalIgnoreCase))
            return "refunded";
        if (eventType.Contains("dispute", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("chargeback", StringComparison.OrdinalIgnoreCase))
            return "disputed";
        if (!string.Equals(purchaseKind, "credits", StringComparison.Ordinal)
            || !purchaseCredited
            || IsReversalEvent(eventType))
            return incomingStatus;
        return incomingStatus.Trim().ToLowerInvariant() switch
        {
            "created" or "authorized" or "pending" or "verification_pending" or "failed" => currentStatus,
            _ => incomingStatus,
        };
    }

    private static bool IsReversalEvent(string eventType) =>
        eventType.Contains("refund.processed", StringComparison.OrdinalIgnoreCase)
        || eventType.Contains("dispute.created", StringComparison.OrdinalIgnoreCase)
        || eventType.Contains("chargeback", StringComparison.OrdinalIgnoreCase);

    internal static long ReversalCreditMicros(
        string eventType,
        long? providerAmountMinor,
        long purchaseAmountMinor,
        long purchasedCreditMicros)
    {
        if (purchasedCreditMicros <= 0)
            return 0;
        if (eventType.Contains("chargeback", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("dispute", StringComparison.OrdinalIgnoreCase))
            return purchasedCreditMicros;
        if (providerAmountMinor is null || providerAmountMinor <= 0 || purchaseAmountMinor <= 0)
            return 0;
        var boundedAmount = Math.Min(providerAmountMinor.Value, purchaseAmountMinor);
        return Math.Max(1, checked(purchasedCreditMicros * boundedAmount / purchaseAmountMinor));
    }

    internal static PaymentBalanceChange CalculatePaymentBalanceChange(
        long availableCreditMicros,
        long existingDebtMicros,
        long grossCreditMicros,
        long requestedReversalMicros,
        long previouslyReversedMicros,
        long purchasedCreditMicros)
    {
        var available = Math.Max(0, availableCreditMicros);
        var debt = Math.Max(0, existingDebtMicros);
        var grossCredit = Math.Max(0, grossCreditMicros);
        var debtPaid = Math.Min(grossCredit, debt);
        var availableCredit = grossCredit - debtPaid;
        var debtAfterCredit = debt - debtPaid;

        var remainingReversible = Math.Max(0, purchasedCreditMicros - previouslyReversedMicros);
        var reversal = Math.Min(Math.Max(0, requestedReversalMicros), remainingReversible);
        var availableAfterCredit = checked(available + availableCredit);
        var removedFromAvailable = Math.Min(reversal, availableAfterCredit);
        var debtCreated = reversal - removedFromAvailable;

        return new PaymentBalanceChange(
            availableCredit - removedFromAvailable,
            checked(debtAfterCredit + debtCreated),
            reversal);
    }

    private static string? TokenSessionId(string token, string prefix)
    {
        if (!token.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var separator = token.IndexOf('.', prefix.Length);
        if (separator <= prefix.Length)
            return null;
        var sessionId = token[prefix.Length..separator];
        return sessionId.Length is >= 12 and <= 64 ? sessionId : null;
    }

    private static string CleanDeviceName(string name)
    {
        var cleaned = string.IsNullOrWhiteSpace(name) ? "Windows device" : name.Trim();
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    private static string SafeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('/'))
            throw new ArgumentException("Invalid resource identifier.", nameof(value));
        return value;
    }

    private static Timestamp Ts(DateTimeOffset value) => Timestamp.FromDateTime(value.UtcDateTime);

    private static string String(IDictionary<string, object> data, string key, string? fallback) =>
        data.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback ?? ""
            : fallback ?? "";

    private static long Long(IDictionary<string, object> data, string key, long fallback = 0) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : fallback;

    private static bool Bool(IDictionary<string, object> data, string key, bool fallback) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToBoolean(value) : fallback;

    private static DateTimeOffset Date(IDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return DateTimeOffset.UnixEpoch;
        return value switch
        {
            Timestamp timestamp => new DateTimeOffset(timestamp.ToDateTime()),
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => DateTimeOffset.TryParse(Convert.ToString(value), out var parsed)
                ? parsed
                : DateTimeOffset.UnixEpoch,
        };
    }

    private static bool FixedHash(string expected, string actual)
    {
        if (expected.Length != actual.Length)
            return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected),
            System.Text.Encoding.ASCII.GetBytes(actual));
    }
}

internal sealed record PaymentBalanceChange(
    long AvailableDeltaMicros,
    long NewDebtMicros,
    long ReversalCreditMicros);
