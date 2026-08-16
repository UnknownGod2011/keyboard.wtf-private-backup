using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class FirestoreReviewIntegrationTests
{
    [Fact]
    [Trait("Category", "FirestoreIntegration")]
    public async Task Firestore_reviews_are_one_per_uid_paginated_and_server_moderated_when_enabled()
    {
        var accessToken = Environment.GetEnvironmentVariable("KEYBOARD_WTF_FIRESTORE_TEST_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        var db = new FirestoreDbBuilder
        {
            ProjectId = "keyboard-wtf-staging",
            Credential = GoogleCredential.FromAccessToken(accessToken),
        }.Build();
        var suffix = Guid.NewGuid().ToString("N");
        var owner = new CallerIdentity(
            $"review-owner-{suffix}",
            $"owner-{suffix}@example.invalid",
            "Review Owner",
            "test");
        var user = new CallerIdentity(
            $"review-user-{suffix}",
            $"user-{suffix}@example.invalid",
            "Review User",
            "test");
        var stranger = new CallerIdentity(
            $"review-stranger-{suffix}",
            $"stranger-{suffix}@example.invalid",
            "Review Stranger",
            "test");
        var options = Options.Create(new PaidSystemOptions
        {
            ProjectId = "keyboard-wtf-staging",
            ReviewAdminUserIds = [owner.UserId],
        });
        var service = new FirestoreReviewService(db, new SystemClock(), options);
        ReviewSnapshot? ownerReview = null;
        ReviewSnapshot? userReview = null;
        var before = await service.GetPageAsync(1, null, CancellationToken.None);
        try
        {
            ownerReview = await service.UpsertAsync(
                owner, 5, "Excellent daily desktop assistant.", CancellationToken.None);
            userReview = await service.UpsertAsync(
                user, 4, "Fast, useful, and easy to understand.", CancellationToken.None);
            var updated = await service.UpsertAsync(
                user, 5, "Fast, useful, and now even more reliable.", CancellationToken.None);

            Assert.Equal(userReview.ReviewId, updated.ReviewId);
            Assert.Equal(5, (await service.GetOwnAsync(user.UserId, CancellationToken.None))?.Rating);
            var firstPage = await service.GetPageAsync(1, null, CancellationToken.None);
            Assert.Equal(before.ReviewCount + 2, firstPage.ReviewCount);
            Assert.Single(firstPage.Items);
            Assert.NotNull(firstPage.NextCursor);
            Assert.Single((await service.GetPageAsync(
                1, firstPage.NextCursor, CancellationToken.None)).Items);
            Assert.False(service.IsModerator(stranger));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                service.DeleteAsModeratorAsync(
                    stranger, userReview.ReviewId, CancellationToken.None));
            Assert.True(service.IsModerator(owner));
            Assert.True(await service.DeleteAsModeratorAsync(
                owner, userReview.ReviewId, CancellationToken.None));
            Assert.True(await service.DeleteOwnAsync(owner.UserId, CancellationToken.None));
            Assert.Equal(before.ReviewCount,
                (await service.GetPageAsync(1, null, CancellationToken.None)).ReviewCount);
        }
        finally
        {
            await service.DeleteOwnAsync(owner.UserId, CancellationToken.None);
            await service.DeleteOwnAsync(user.UserId, CancellationToken.None);
            foreach (var reviewId in new[] { ownerReview?.ReviewId, userReview?.ReviewId }
                         .Where(value => value is not null))
            {
                var audit = await db.Collection("reviewAudit")
                    .WhereEqualTo("targetReviewId", reviewId)
                    .GetSnapshotAsync();
                var batch = db.StartBatch();
                foreach (var document in audit.Documents)
                    batch.Delete(document.Reference);
                if (audit.Count > 0)
                    await batch.CommitAsync();
            }
        }
    }
}
