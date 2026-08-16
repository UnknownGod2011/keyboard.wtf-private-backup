using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class FirestoreReviewService : IReviewService
{
    private const int MaximumPageSize = 20;
    private readonly FirestoreDb _db;
    private readonly IClock _clock;
    private readonly PaidSystemOptions _options;

    public FirestoreReviewService(
        FirestoreDb db,
        IClock clock,
        IOptions<PaidSystemOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<ReviewPageSnapshot> GetPageAsync(
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaximumPageSize);
        Query query = _db.Collection("reviews").OrderByDescending("updatedAt");
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var cursorReference = _db.Collection("reviews").Document(SafeReviewId(cursor));
            var cursorSnapshot = await cursorReference.GetSnapshotAsync(cancellationToken);
            if (!cursorSnapshot.Exists)
                throw new ArgumentException("The review cursor is invalid.", nameof(cursor));
            query = query.StartAfter(cursorSnapshot);
        }

        var pageTask = query.Limit(limit + 1).GetSnapshotAsync(cancellationToken);
        var statsTask = _db.Collection("reviewMetadata").Document("public")
            .GetSnapshotAsync(cancellationToken);
        await Task.WhenAll(pageTask, statsTask);

        var documents = pageTask.Result.Documents;
        var hasMore = documents.Count > limit;
        var visible = documents.Take(limit).Select(FromSnapshot).ToArray();
        var stats = statsTask.Result.Exists
            ? statsTask.Result.ToDictionary()
            : new Dictionary<string, object>();
        var count = Math.Max(0, Long(stats, "reviewCount"));
        var totalRating = Math.Max(0, Long(stats, "totalRating"));

        return new ReviewPageSnapshot(
            visible,
            hasMore && visible.Length > 0 ? visible[^1].ReviewId : null,
            count,
            count == 0 ? 0 : Math.Round((double)totalRating / count, 2));
    }

    public async Task<ReviewSnapshot?> GetOwnAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var snapshot = await Review(userId).GetSnapshotAsync(cancellationToken);
        return snapshot.Exists ? FromSnapshot(snapshot) : null;
    }

    public async Task<ReviewSnapshot> UpsertAsync(
        CallerIdentity caller,
        int rating,
        string text,
        CancellationToken cancellationToken)
    {
        var normalizedText = ReviewPolicy.NormalizeText(text);
        var displayName = ReviewPolicy.NormalizeDisplayName(caller.DisplayName);
        ReviewPolicy.ValidateRating(rating);
        var reviewReference = Review(caller.UserId);
        var statsReference = _db.Collection("reviewMetadata").Document("public");
        var now = _clock.UtcNow;

        await _db.RunTransactionAsync(async transaction =>
        {
            var reviewSnapshot = await transaction.GetSnapshotAsync(reviewReference, cancellationToken);
            var statsSnapshot = await transaction.GetSnapshotAsync(statsReference, cancellationToken);
            var previousRating = reviewSnapshot.Exists
                ? checked((int)Long(reviewSnapshot.ToDictionary(), "rating"))
                : 0;
            var stats = statsSnapshot.Exists
                ? statsSnapshot.ToDictionary()
                : new Dictionary<string, object>();
            var reviewCount = Math.Max(0, Long(stats, "reviewCount"));
            var totalRating = Math.Max(0, Long(stats, "totalRating"));
            var createdAt = reviewSnapshot.Exists
                ? Date(reviewSnapshot.ToDictionary(), "createdAt")
                : now;

            transaction.Set(reviewReference, new Dictionary<string, object>
            {
                ["ownerUserId"] = caller.UserId,
                ["rating"] = rating,
                ["text"] = normalizedText,
                ["reviewerDisplayName"] = displayName,
                ["createdAt"] = Ts(createdAt),
                ["updatedAt"] = Ts(now),
            });
            transaction.Set(statsReference, new Dictionary<string, object>
            {
                ["reviewCount"] = reviewSnapshot.Exists ? reviewCount : checked(reviewCount + 1),
                ["totalRating"] = checked(totalRating - previousRating + rating),
                ["updatedAt"] = Ts(now),
            });
            return 0;
        }, cancellationToken: cancellationToken);

        return new ReviewSnapshot(
            reviewReference.Id,
            rating,
            normalizedText,
            displayName,
            (await reviewReference.GetSnapshotAsync(cancellationToken)).CreateTime is Timestamp created
                ? new DateTimeOffset(created.ToDateTime())
                : now,
            now);
    }

    public Task<bool> DeleteOwnAsync(string userId, CancellationToken cancellationToken) =>
        DeleteAsync(Review(userId).Id, userId, "owner", cancellationToken);

    public Task<bool> DeleteAsModeratorAsync(
        CallerIdentity caller,
        string reviewId,
        CancellationToken cancellationToken)
    {
        if (!IsModerator(caller))
            throw new UnauthorizedAccessException("Review moderation requires the owner account.");
        return DeleteAsync(SafeReviewId(reviewId), caller.UserId, "moderator", cancellationToken);
    }

    public bool IsModerator(CallerIdentity caller) =>
        _options.ReviewAdminUserIds.Contains(caller.UserId, StringComparer.Ordinal);

    private Task<bool> DeleteAsync(
        string reviewId,
        string actorUserId,
        string actorKind,
        CancellationToken cancellationToken)
    {
        var reviewReference = _db.Collection("reviews").Document(SafeReviewId(reviewId));
        var statsReference = _db.Collection("reviewMetadata").Document("public");
        var auditReference = _db.Collection("reviewAudit").Document(Guid.NewGuid().ToString("N"));
        var now = _clock.UtcNow;
        return _db.RunTransactionAsync(async transaction =>
        {
            var reviewSnapshot = await transaction.GetSnapshotAsync(reviewReference, cancellationToken);
            if (!reviewSnapshot.Exists)
                return false;
            var review = reviewSnapshot.ToDictionary();
            var ownerUserId = String(review, "ownerUserId", "");
            if (actorKind == "owner" && !string.Equals(ownerUserId, actorUserId, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("A review can only be deleted by its owner.");

            var statsSnapshot = await transaction.GetSnapshotAsync(statsReference, cancellationToken);
            var stats = statsSnapshot.Exists
                ? statsSnapshot.ToDictionary()
                : new Dictionary<string, object>();
            var reviewCount = Math.Max(0, Long(stats, "reviewCount"));
            var totalRating = Math.Max(0, Long(stats, "totalRating"));
            var rating = Math.Max(0, Long(review, "rating"));

            transaction.Delete(reviewReference);
            transaction.Set(statsReference, new Dictionary<string, object>
            {
                ["reviewCount"] = Math.Max(0, reviewCount - 1),
                ["totalRating"] = Math.Max(0, totalRating - rating),
                ["updatedAt"] = Ts(now),
            });
            transaction.Create(auditReference, new Dictionary<string, object>
            {
                ["action"] = "review_deleted",
                ["targetReviewId"] = reviewId,
                ["targetOwnerUserId"] = ownerUserId,
                ["actorUserId"] = actorUserId,
                ["actorKind"] = actorKind,
                ["createdAt"] = Ts(now),
            });
            return true;
        }, cancellationToken: cancellationToken);
    }

    private DocumentReference Review(string userId) =>
        _db.Collection("reviews").Document($"review_{Crypto.Sha256($"review:{userId}")[..32]}");

    private static ReviewSnapshot FromSnapshot(DocumentSnapshot snapshot)
    {
        var data = snapshot.ToDictionary();
        return new ReviewSnapshot(
            snapshot.Id,
            checked((int)Long(data, "rating")),
            String(data, "text", ""),
            String(data, "reviewerDisplayName", "keyboard.wtf user"),
            Date(data, "createdAt"),
            Date(data, "updatedAt"));
    }

    private static string SafeReviewId(string value)
    {
        if (string.IsNullOrEmpty(value)
            || !Regex.IsMatch(value, "^review_[a-f0-9]{32}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("The review identifier is invalid.", nameof(value));
        return value;
    }

    private static Timestamp Ts(DateTimeOffset value) => Timestamp.FromDateTime(value.UtcDateTime);

    private static string String(IDictionary<string, object> data, string key, string fallback) =>
        data.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback
            : fallback;

    private static long Long(IDictionary<string, object> data, string key) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : 0;

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
}

internal static partial class ReviewPolicy
{
    private const int MinimumTextLength = 5;
    private const int MaximumTextLength = 1_000;

    public static void ValidateRating(int rating)
    {
        if (rating is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(rating), "Rating must be between 1 and 5.");
    }

    public static string NormalizeText(string? text)
    {
        var normalized = (text ?? "").Normalize(NormalizationForm.FormKC)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if ((character == '\n' || character == '\t' || !char.IsControl(character))
                && char.GetUnicodeCategory(character) != UnicodeCategory.Format)
                builder.Append(character);
        }
        normalized = RepeatedWhitespace().Replace(builder.ToString(), " ");
        normalized = RepeatedBlankLines().Replace(normalized, "\n\n").Trim();
        if (normalized.Length is < MinimumTextLength or > MaximumTextLength)
            throw new ArgumentException(
                $"Review text must be between {MinimumTextLength} and {MaximumTextLength} characters.",
                nameof(text));
        return normalized;
    }

    public static string NormalizeDisplayName(string? displayName)
    {
        var normalized = (displayName ?? "").Normalize(NormalizationForm.FormKC);
        normalized = new string(normalized.Where(character => !char.IsControl(character)).ToArray());
        normalized = RepeatedWhitespace().Replace(normalized, " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains('@', StringComparison.Ordinal))
            return "keyboard.wtf user";
        return normalized.Length <= 60 ? normalized : normalized[..60].TrimEnd();
    }

    [GeneratedRegex("[ \\t]+", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedWhitespace();

    [GeneratedRegex("\\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedBlankLines();
}
