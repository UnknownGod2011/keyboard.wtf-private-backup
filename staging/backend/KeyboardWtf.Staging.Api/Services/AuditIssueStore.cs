using Google.Cloud.Firestore;

namespace KeyboardWtf.Staging.Api.Services;

internal static class AuditIssueStore
{
    public static Task UpsertOpenAsync(
        FirestoreDb db,
        string collection,
        string id,
        IDictionary<string, object> fields,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reference = db.Collection(collection).Document(id);
        return db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reference, cancellationToken);
            var data = snapshot.Exists ? snapshot.ToDictionary() : [];
            var update = new Dictionary<string, object>(fields)
            {
                ["status"] = "open",
                ["occurrences"] = ReadLong(data, "occurrences") + 1,
                ["firstObservedAt"] = snapshot.Exists && data.TryGetValue("firstObservedAt", out var first)
                    ? first
                    : Timestamp.FromDateTime(now.UtcDateTime),
                ["lastObservedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
            };
            transaction.Set(reference, update, SetOptions.MergeAll);
        }, cancellationToken: cancellationToken);
    }

    private static long ReadLong(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : 0;
}
