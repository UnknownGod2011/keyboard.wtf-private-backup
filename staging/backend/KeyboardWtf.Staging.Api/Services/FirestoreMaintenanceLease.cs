using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Infrastructure;

namespace KeyboardWtf.Staging.Api.Services;

internal sealed class FirestoreMaintenanceLease
{
    private readonly FirestoreDb _db;
    private readonly IClock _clock;

    public FirestoreMaintenanceLease(FirestoreDb db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<MaintenanceLeaseHandle?> TryAcquireAsync(
        string job,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var owner = Crypto.RandomToken(24);
        var reference = _db.Collection("maintenanceLeases").Document(SafeJob(job));
        var acquired = await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reference, cancellationToken);
            if (snapshot.Exists)
            {
                var data = snapshot.ToDictionary();
                var expiresAt = data.TryGetValue("expiresAt", out var value) && value is Timestamp timestamp
                    ? new DateTimeOffset(timestamp.ToDateTime())
                    : DateTimeOffset.UnixEpoch;
                if (!CanAcquire(expiresAt, now))
                    return false;
            }

            transaction.Set(reference, new Dictionary<string, object>
            {
                ["job"] = SafeJob(job),
                ["owner"] = owner,
                ["status"] = "running",
                ["acquiredAt"] = Timestamp.FromDateTime(now.UtcDateTime),
                ["expiresAt"] = Timestamp.FromDateTime(now.Add(duration).UtcDateTime),
                ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
            }, SetOptions.Overwrite);
            return true;
        }, cancellationToken: cancellationToken);

        return acquired
            ? new MaintenanceLeaseHandle(_db, reference, owner, _clock)
            : null;
    }

    internal static bool CanAcquire(DateTimeOffset expiresAt, DateTimeOffset now) =>
        expiresAt <= now;

    private static string SafeJob(string job)
    {
        var safe = new string((job ?? "maintenance")
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(80)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "maintenance" : safe;
    }
}

internal sealed class MaintenanceLeaseHandle : IAsyncDisposable
{
    private readonly FirestoreDb _db;
    private readonly DocumentReference _reference;
    private readonly string _owner;
    private readonly IClock _clock;
    private int _released;

    public MaintenanceLeaseHandle(
        FirestoreDb db,
        DocumentReference reference,
        string owner,
        IClock clock)
    {
        _db = db;
        _reference = reference;
        _owner = owner;
        _clock = clock;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(_reference);
            if (!snapshot.Exists
                || !string.Equals(snapshot.GetValue<string>("owner"), _owner, StringComparison.Ordinal))
                return;
            transaction.Update(_reference, new Dictionary<string, object>
            {
                ["status"] = "idle",
                ["releasedAt"] = Timestamp.FromDateTime(_clock.UtcNow.UtcDateTime),
                ["expiresAt"] = Timestamp.FromDateTime(_clock.UtcNow.UtcDateTime),
                ["updatedAt"] = Timestamp.FromDateTime(_clock.UtcNow.UtcDateTime),
            });
        });
    }
}
