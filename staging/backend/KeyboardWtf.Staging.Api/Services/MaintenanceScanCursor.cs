using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Infrastructure;

namespace KeyboardWtf.Staging.Api.Services;

internal sealed class MaintenanceScanCursor
{
    private readonly FirestoreDb _db;

    public MaintenanceScanCursor(FirestoreDb db) => _db = db;

    public async Task<DocumentSnapshot?> LoadAsync(
        string job,
        CancellationToken cancellationToken)
    {
        var state = await State(job).GetSnapshotAsync(cancellationToken);
        if (!state.Exists)
            return null;
        var data = state.ToDictionary();
        var path = data.TryGetValue("lastDocumentPath", out var value)
            ? Convert.ToString(value)
            : null;
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var cursor = await _db.Document(path).GetSnapshotAsync(cancellationToken);
        if (cursor.Exists)
            return cursor;
        await ResetAsync(job, cancellationToken);
        return null;
    }

    public Task SaveAsync(
        string job,
        DocumentSnapshot? lastDocument,
        bool completedScan,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        State(job).SetAsync(new Dictionary<string, object>
        {
            ["lastDocumentPath"] = completedScan || lastDocument is null
                ? ""
                : lastDocument.Reference.Path,
            ["completedScan"] = completedScan,
            ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
        }, SetOptions.MergeAll, cancellationToken);

    private Task ResetAsync(string job, CancellationToken cancellationToken) =>
        State(job).SetAsync(new Dictionary<string, object>
        {
            ["lastDocumentPath"] = "",
            ["completedScan"] = true,
            ["updatedAt"] = Timestamp.FromDateTime(DateTime.UtcNow),
        }, SetOptions.MergeAll, cancellationToken);

    private DocumentReference State(string job) =>
        _db.Collection("maintenanceCursors").Document(Crypto.Sha256(job));
}
