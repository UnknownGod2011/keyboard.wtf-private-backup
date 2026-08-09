using KeyboardWtf.Staging.Api.Infrastructure;

namespace KeyboardWtf.Staging.Api.Services;

internal sealed record AllowanceAllocation(string BucketId, long CreditMicros);

internal sealed record AllowanceBucket(
    string Id,
    string Kind,
    string SourceType,
    string SourceId,
    long OriginalCreditMicros,
    long AvailableCreditMicros,
    long ReservedCreditMicros,
    long ConsumedCreditMicros,
    long ReversedCreditMicros,
    long ExpiresAtUnix,
    long CreatedAtUnix,
    bool Expired,
    string RulesVersion = AllowanceWallet.CurrentRulesVersion);

/// <summary>
/// Server-owned entitlement wallet. Active balances are split by payment source so
/// expiring subscription allowance, durable top-ups, usage, refunds and disputes
/// remain independently auditable. The immutable ledger remains the financial
/// source of truth; this compact map is the transaction-friendly materialized view.
/// </summary>
internal sealed class AllowanceWallet
{
    internal const string CurrentRulesVersion = "allowance-v1";
    private readonly Dictionary<string, AllowanceBucket> _buckets;
    private readonly long _nowUnix;
    private readonly List<(string BucketId, long CreditMicros)> _newlyExpired = [];

    private AllowanceWallet(Dictionary<string, AllowanceBucket> buckets, long nowUnix)
    {
        _buckets = buckets;
        _nowUnix = nowUnix;
        ExpireDueBuckets();
    }

    public long AvailableCreditMicros => _buckets.Values.Sum(bucket => bucket.AvailableCreditMicros);
    public long ReservedCreditMicros => _buckets.Values.Sum(bucket => bucket.ReservedCreditMicros);
    public IReadOnlyCollection<AllowanceBucket> Buckets => _buckets.Values;
    public IReadOnlyList<(string BucketId, long CreditMicros)> NewlyExpired => _newlyExpired;

    public static AllowanceWallet FromAccount(IDictionary<string, object> account, DateTimeOffset now)
    {
        var buckets = ReadBuckets(account);
        var representedAvailable = buckets.Values.Sum(bucket => bucket.AvailableCreditMicros);
        var representedReserved = buckets.Values.Sum(bucket => bucket.ReservedCreditMicros);
        var legacyAvailable = Math.Max(0, ReadLong(account, "availableCreditMicros") - representedAvailable);
        var legacyReserved = Math.Max(0, ReadLong(account, "reservedCreditMicros") - representedReserved);
        if (legacyAvailable > 0 || legacyReserved > 0)
        {
            const string legacyId = "legacy-migrated-balance";
            buckets[legacyId] = new AllowanceBucket(
                legacyId,
                "legacy",
                "migration",
                "pre-allowance-v1",
                checked(legacyAvailable + legacyReserved),
                legacyAvailable,
                legacyReserved,
                0,
                0,
                0,
                now.ToUnixTimeSeconds(),
                false);
        }
        return new AllowanceWallet(buckets, now.ToUnixTimeSeconds());
    }

    public string AddCredit(
        string kind,
        string sourceType,
        string sourceId,
        long grossCreditMicros,
        long expiresAtUnix,
        DateTimeOffset createdAt,
        ref long creditDebtMicros)
    {
        if (grossCreditMicros <= 0)
            throw new ArgumentOutOfRangeException(nameof(grossCreditMicros));
        var id = BucketId(sourceType, sourceId);
        if (_buckets.ContainsKey(id))
            return id;
        var debtPaid = Math.Min(Math.Max(0, creditDebtMicros), grossCreditMicros);
        creditDebtMicros -= debtPaid;
        _buckets[id] = new AllowanceBucket(
            id,
            kind,
            sourceType,
            sourceId,
            grossCreditMicros,
            grossCreditMicros - debtPaid,
            0,
            debtPaid,
            0,
            expiresAtUnix,
            createdAt.ToUnixTimeSeconds(),
            expiresAtUnix > 0 && expiresAtUnix <= _nowUnix);
        ExpireDueBuckets();
        return id;
    }

    public IReadOnlyList<AllowanceAllocation> Reserve(long creditMicros)
    {
        if (creditMicros <= 0)
            throw new ArgumentOutOfRangeException(nameof(creditMicros));
        if (AvailableCreditMicros < creditMicros)
            throw new InvalidOperationException("No usable credits are available. Add credits to continue.");
        var remaining = creditMicros;
        var allocations = new List<AllowanceAllocation>();
        foreach (var bucket in OrderedAvailableBuckets())
        {
            if (remaining == 0)
                break;
            var amount = Math.Min(bucket.AvailableCreditMicros, remaining);
            _buckets[bucket.Id] = bucket with
            {
                AvailableCreditMicros = bucket.AvailableCreditMicros - amount,
                ReservedCreditMicros = bucket.ReservedCreditMicros + amount,
            };
            allocations.Add(new AllowanceAllocation(bucket.Id, amount));
            remaining -= amount;
        }
        if (remaining != 0)
            throw new InvalidOperationException("The entitlement wallet could not reserve the requested balance.");
        return allocations;
    }

    public IReadOnlyList<AllowanceAllocation> Extend(
        IReadOnlyList<AllowanceAllocation> existing,
        long additionalCreditMicros)
    {
        if (additionalCreditMicros <= 0)
            return existing;
        return MergeAllocations(existing.Concat(Reserve(additionalCreditMicros)));
    }

    public (long Released, long DebtAdded) Settle(
        IReadOnlyList<AllowanceAllocation> allocations,
        long actualCreditMicros)
    {
        var charged = Math.Max(0, actualCreditMicros);
        var allocated = allocations.Sum(item => item.CreditMicros);
        var remainingCharge = charged;
        var released = 0L;
        foreach (var allocation in allocations)
        {
            if (!_buckets.TryGetValue(allocation.BucketId, out var bucket))
                throw new InvalidOperationException("A reserved allowance bucket is missing.");
            var reservedFromBucket = Math.Min(allocation.CreditMicros, bucket.ReservedCreditMicros);
            var consumed = Math.Min(reservedFromBucket, remainingCharge);
            var release = reservedFromBucket - consumed;
            var canReturn = !IsExpired(bucket);
            _buckets[bucket.Id] = bucket with
            {
                ReservedCreditMicros = bucket.ReservedCreditMicros - reservedFromBucket,
                AvailableCreditMicros = bucket.AvailableCreditMicros + (canReturn ? release : 0),
                ConsumedCreditMicros = bucket.ConsumedCreditMicros + consumed,
                Expired = bucket.Expired || !canReturn,
            };
            released += canReturn ? release : 0;
            remainingCharge -= consumed;
        }

        if (remainingCharge > 0)
        {
            foreach (var bucket in OrderedAvailableBuckets())
            {
                if (remainingCharge == 0)
                    break;
                var consumed = Math.Min(bucket.AvailableCreditMicros, remainingCharge);
                _buckets[bucket.Id] = bucket with
                {
                    AvailableCreditMicros = bucket.AvailableCreditMicros - consumed,
                    ConsumedCreditMicros = bucket.ConsumedCreditMicros + consumed,
                };
                remainingCharge -= consumed;
            }
        }
        return (released, remainingCharge);
    }

    public long Release(IReadOnlyList<AllowanceAllocation> allocations)
    {
        var released = 0L;
        foreach (var allocation in allocations)
        {
            if (!_buckets.TryGetValue(allocation.BucketId, out var bucket))
                throw new InvalidOperationException("A reserved allowance bucket is missing.");
            var amount = Math.Min(allocation.CreditMicros, bucket.ReservedCreditMicros);
            var canReturn = !IsExpired(bucket);
            _buckets[bucket.Id] = bucket with
            {
                ReservedCreditMicros = bucket.ReservedCreditMicros - amount,
                AvailableCreditMicros = bucket.AvailableCreditMicros + (canReturn ? amount : 0),
                Expired = bucket.Expired || !canReturn,
            };
            if (canReturn)
                released += amount;
        }
        return released;
    }

    public long Reverse(string sourceType, string sourceId, long requestedCreditMicros, ref long creditDebtMicros)
    {
        var id = BucketId(sourceType, sourceId);
        if (!_buckets.TryGetValue(id, out var bucket))
            return 0;
        var remainingReversible = Math.Max(0, bucket.OriginalCreditMicros - bucket.ReversedCreditMicros);
        var reversal = Math.Min(Math.Max(0, requestedCreditMicros), remainingReversible);
        var removedAvailable = Math.Min(reversal, bucket.AvailableCreditMicros);
        var debtAdded = reversal - removedAvailable;
        creditDebtMicros = checked(Math.Max(0, creditDebtMicros) + debtAdded);
        _buckets[id] = bucket with
        {
            AvailableCreditMicros = bucket.AvailableCreditMicros - removedAvailable,
            ReversedCreditMicros = bucket.ReversedCreditMicros + reversal,
        };
        return reversal;
    }

    public Dictionary<string, object> ToFirestoreMap() => _buckets.ToDictionary(
        pair => pair.Key,
        pair => (object)new Dictionary<string, object>
        {
            ["id"] = pair.Value.Id,
            ["kind"] = pair.Value.Kind,
            ["sourceType"] = pair.Value.SourceType,
            ["sourceId"] = pair.Value.SourceId,
            ["originalCreditMicros"] = pair.Value.OriginalCreditMicros,
            ["availableCreditMicros"] = pair.Value.AvailableCreditMicros,
            ["reservedCreditMicros"] = pair.Value.ReservedCreditMicros,
            ["consumedCreditMicros"] = pair.Value.ConsumedCreditMicros,
            ["reversedCreditMicros"] = pair.Value.ReversedCreditMicros,
            ["expiresAtUnix"] = pair.Value.ExpiresAtUnix,
            ["createdAtUnix"] = pair.Value.CreatedAtUnix,
            ["expired"] = pair.Value.Expired,
            ["rulesVersion"] = pair.Value.RulesVersion,
        }, StringComparer.Ordinal);

    public static IReadOnlyList<Dictionary<string, object>> AllocationMap(
        IReadOnlyList<AllowanceAllocation> allocations) => allocations.Select(item =>
            new Dictionary<string, object>
            {
                ["bucketId"] = item.BucketId,
                ["creditMicros"] = item.CreditMicros,
            }).ToArray();

    public static IReadOnlyList<AllowanceAllocation> ReadAllocations(
        IDictionary<string, object> data)
    {
        if (!data.TryGetValue("allowanceAllocations", out var raw) || raw is not IEnumerable<object> items)
            return [];
        var allocations = new List<AllowanceAllocation>();
        foreach (var item in items)
        {
            if (item is not IDictionary<string, object> map)
                continue;
            var id = ReadString(map, "bucketId");
            var amount = ReadLong(map, "creditMicros");
            if (!string.IsNullOrWhiteSpace(id) && amount > 0)
                allocations.Add(new AllowanceAllocation(id, amount));
        }
        return MergeAllocations(allocations);
    }

    private IEnumerable<AllowanceBucket> OrderedAvailableBuckets() => _buckets.Values
        .Where(bucket => bucket.AvailableCreditMicros > 0 && !IsExpired(bucket))
        .OrderBy(bucket => bucket.ExpiresAtUnix == 0 ? long.MaxValue : bucket.ExpiresAtUnix)
        .ThenBy(bucket => bucket.CreatedAtUnix)
        .ThenBy(bucket => bucket.Id, StringComparer.Ordinal);

    private void ExpireDueBuckets()
    {
        foreach (var pair in _buckets.ToArray())
        {
            var bucket = pair.Value;
            if (!IsExpired(bucket))
                continue;
            if (!bucket.Expired && bucket.AvailableCreditMicros > 0)
                _newlyExpired.Add((bucket.Id, bucket.AvailableCreditMicros));
            _buckets[pair.Key] = bucket with { AvailableCreditMicros = 0, Expired = true };
        }
    }

    private bool IsExpired(AllowanceBucket bucket) =>
        bucket.Expired || (bucket.ExpiresAtUnix > 0 && bucket.ExpiresAtUnix <= _nowUnix);

    private static Dictionary<string, AllowanceBucket> ReadBuckets(IDictionary<string, object> account)
    {
        var result = new Dictionary<string, AllowanceBucket>(StringComparer.Ordinal);
        if (!account.TryGetValue("allowanceBuckets", out var raw)
            || raw is not IDictionary<string, object> map)
            return result;
        foreach (var pair in map)
        {
            if (pair.Value is not IDictionary<string, object> value)
                continue;
            var id = ReadString(value, "id");
            if (string.IsNullOrWhiteSpace(id))
                id = pair.Key;
            result[pair.Key] = new AllowanceBucket(
                id,
                ReadString(value, "kind", "legacy"),
                ReadString(value, "sourceType", "unknown"),
                ReadString(value, "sourceId", pair.Key),
                ReadLong(value, "originalCreditMicros"),
                Math.Max(0, ReadLong(value, "availableCreditMicros")),
                Math.Max(0, ReadLong(value, "reservedCreditMicros")),
                Math.Max(0, ReadLong(value, "consumedCreditMicros")),
                Math.Max(0, ReadLong(value, "reversedCreditMicros")),
                ReadLong(value, "expiresAtUnix"),
                ReadLong(value, "createdAtUnix"),
                ReadBool(value, "expired"),
                ReadString(value, "rulesVersion", CurrentRulesVersion));
        }
        return result;
    }

    private static IReadOnlyList<AllowanceAllocation> MergeAllocations(IEnumerable<AllowanceAllocation> allocations) =>
        allocations.GroupBy(item => item.BucketId, StringComparer.Ordinal)
            .Select(group => new AllowanceAllocation(group.Key, group.Sum(item => item.CreditMicros)))
            .OrderBy(item => item.BucketId, StringComparer.Ordinal)
            .ToArray();

    private static string BucketId(string sourceType, string sourceId) =>
        $"b_{Crypto.Sha256($"{sourceType.Trim().ToLowerInvariant()}:{sourceId.Trim()}")[..32]}";

    private static string ReadString(IDictionary<string, object> data, string key, string fallback = "") =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToString(value) ?? fallback : fallback;

    private static long ReadLong(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : 0;

    private static bool ReadBool(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is not null && Convert.ToBoolean(value);
}
