using System.Web;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Contracts;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class DesktopLoginService
{
    private readonly IAccountStore _store;
    private readonly PaidSystemOptions _options;
    private readonly IClock _clock;

    public DesktopLoginService(
        IAccountStore store,
        IOptions<PaidSystemOptions> options,
        IClock clock)
    {
        _store = store;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<DesktopLoginStartResponse> StartAsync(
        DesktopLoginStartRequest request,
        CancellationToken cancellationToken)
    {
        ValidateEntropy(request.State, nameof(request.State));
        ValidateEntropy(request.DeviceNonce, nameof(request.DeviceNonce));
        ValidateEntropy(request.PkceChallenge, nameof(request.PkceChallenge));
        if (!Crypto.IsSafeLoopbackCallback(request.CallbackUri))
            throw new ArgumentException("The desktop callback must be a loopback HTTP callback on an unprivileged port.");

        var expiresAt = _clock.UtcNow.AddMinutes(_options.LoginAttemptMinutes);
        var attempt = await _store.CreateLoginAttemptAsync(
            Crypto.Sha256(request.State),
            Crypto.Sha256(request.DeviceNonce),
            request.PkceChallenge,
            request.CallbackUri,
            expiresAt,
            cancellationToken);
        var query = HttpUtility.ParseQueryString("");
        query["attempt"] = attempt.AttemptId;
        query["state"] = request.State;
        query["nonce"] = request.DeviceNonce;
        var browserUrl = $"{_options.WebBaseUrl.TrimEnd('/')}/desktop-login.html?{query}";
        return new DesktopLoginStartResponse(attempt.AttemptId, browserUrl, expiresAt);
    }

    public async Task<DesktopLoginAuthorizeResponse> AuthorizeAsync(
        CallerIdentity caller,
        DesktopLoginAuthorizeRequest request,
        CancellationToken cancellationToken)
    {
        var (attempt, code) = await _store.AuthorizeLoginAttemptAsync(
            request.AttemptId,
            Crypto.Sha256(request.State),
            Crypto.Sha256(request.DeviceNonce),
            caller.UserId,
            cancellationToken);
        var callback = new UriBuilder(attempt.CallbackUri);
        var query = HttpUtility.ParseQueryString(callback.Query);
        query["attempt"] = attempt.AttemptId;
        query["code"] = code;
        query["state"] = request.State;
        callback.Query = query.ToString();
        return new DesktopLoginAuthorizeResponse(callback.Uri.ToString(), _clock.UtcNow.AddMinutes(1));
    }

    public Task<DesktopSessionTokens> ExchangeAsync(
        DesktopLoginExchangeRequest request,
        CancellationToken cancellationToken)
    {
        ValidateEntropy(request.State, nameof(request.State));
        ValidateEntropy(request.DeviceNonce, nameof(request.DeviceNonce));
        ValidateEntropy(request.AuthorizationCode, nameof(request.AuthorizationCode));
        ValidateEntropy(request.PkceVerifier, nameof(request.PkceVerifier), minimumLength: 43);
        if (string.IsNullOrWhiteSpace(request.DeviceInstallationId)
            || request.DeviceInstallationId.Length > 200)
            throw new ArgumentException("A stable device installation identifier is required.");
        return _store.ExchangeLoginAttemptAsync(
            request.AttemptId,
            Crypto.Sha256(request.State),
            Crypto.Sha256(request.DeviceNonce),
            Crypto.Sha256(request.AuthorizationCode),
            request.PkceVerifier,
            request.DeviceName,
            request.DeviceInstallationId,
            TimeSpan.FromMinutes(_options.AccessTokenMinutes),
            TimeSpan.FromDays(_options.RefreshTokenDays),
            cancellationToken);
    }

    public Task<DesktopSessionTokens> RefreshAsync(
        SessionRefreshRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            throw new ArgumentException("A refresh token is required.");
        return _store.RefreshSessionAsync(
            request.RefreshToken,
            TimeSpan.FromMinutes(_options.AccessTokenMinutes),
            TimeSpan.FromDays(_options.RefreshTokenDays),
            cancellationToken);
    }

    private static void ValidateEntropy(string value, string name, int minimumLength = 32)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < minimumLength || value.Length > 512)
            throw new ArgumentException($"{name} is invalid.");
    }
}
