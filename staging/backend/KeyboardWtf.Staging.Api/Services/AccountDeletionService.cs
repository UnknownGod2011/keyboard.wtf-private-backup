using KeyboardWtf.Staging.Api.Domain;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class AccountDeletionService
{
    internal static readonly TimeSpan RecentAuthenticationWindow = TimeSpan.FromMinutes(5);
    private readonly IAccountStore _store;
    private readonly IIdentityService _identities;
    private readonly IClock _clock;

    public AccountDeletionService(
        IAccountStore store,
        IIdentityService identities,
        IClock clock)
    {
        _store = store;
        _identities = identities;
        _clock = clock;
    }

    public async Task DeleteAsync(
        CallerIdentity caller,
        bool confirmed,
        CancellationToken cancellationToken)
    {
        if (!confirmed)
            throw new ArgumentException("Permanent account deletion must be explicitly confirmed.");
        if (!string.Equals(caller.AuthenticationKind, "firebase", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Account deletion requires a fresh Google sign-in.");
        if (!IsRecentAuthentication(caller.AuthenticatedAt, _clock.UtcNow))
            throw new UnauthorizedAccessException(
                "For your security, sign in with Google again before deleting this account.");

        // Operational data is pseudonymized first while the caller still has a
        // verifiable identity. Financial ledger, purchases and usage metadata are
        // retained under the pseudonymous UID for accounting/audit obligations.
        await _store.DeleteAccountOperationalDataAsync(caller.UserId, cancellationToken);
        await _identities.DeleteFirebaseUserAsync(caller.UserId, cancellationToken);
    }

    internal static bool IsRecentAuthentication(
        DateTimeOffset? authenticatedAt,
        DateTimeOffset now) =>
        authenticatedAt is DateTimeOffset value
        && value <= now.AddMinutes(1)
        && now - value <= RecentAuthenticationWindow;
}
