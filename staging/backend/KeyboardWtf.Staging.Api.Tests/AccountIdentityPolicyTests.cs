using KeyboardWtf.Staging.Api.Services;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Configuration;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class AccountIdentityPolicyTests
{
    [Fact]
    public void Unchanged_account_identity_requires_no_write()
    {
        var account = new Dictionary<string, object>
        {
            ["email"] = "user@example.com",
            ["displayName"] = "Example User",
        };

        Assert.False(FirestoreAccountStore.AccountIdentityNeedsUpdate(
            account,
            "user@example.com",
            "Example User"));
    }

    [Theory]
    [InlineData("changed@example.com", "Example User")]
    [InlineData("user@example.com", "Changed Name")]
    public void Changed_account_identity_is_updated(string email, string displayName)
    {
        var account = new Dictionary<string, object>
        {
            ["email"] = "user@example.com",
            ["displayName"] = "Example User",
        };

        Assert.True(FirestoreAccountStore.AccountIdentityNeedsUpdate(
            account,
            email,
            displayName));
    }

    [Fact]
    [Trait("Category", "FirestoreIntegration")]
    public async Task Repeated_unchanged_identity_does_not_rewrite_account_when_enabled()
    {
        var accessToken = Environment.GetEnvironmentVariable(
            "KEYBOARD_WTF_FIRESTORE_TEST_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(accessToken))
            return;

        var db = new FirestoreDbBuilder
        {
            ProjectId = "keyboard-wtf-staging",
            Credential = GoogleCredential.FromAccessToken(accessToken),
        }.Build();
        var options = Options.Create(new PaidSystemOptions
        {
            ProjectId = "keyboard-wtf-staging",
        });
        var store = new FirestoreAccountStore(db, new SystemClock(), options);
        var suffix = Guid.NewGuid().ToString("N");
        var caller = new CallerIdentity(
            $"integration-identity-{suffix}",
            $"identity-{suffix}@example.invalid",
            "Identity Test",
            "test");
        var account = db.Collection("accounts").Document(caller.UserId);

        try
        {
            await store.EnsureAccountAsync(caller, CancellationToken.None);
            var first = await account.GetSnapshotAsync();
            var firstUpdatedAt = first.GetValue<Timestamp>("updatedAt");

            await Task.Delay(25);
            await store.EnsureAccountAsync(caller, CancellationToken.None);
            var repeated = await account.GetSnapshotAsync();

            Assert.Equal(firstUpdatedAt, repeated.GetValue<Timestamp>("updatedAt"));
        }
        finally
        {
            await account.DeleteAsync();
        }
    }
}
