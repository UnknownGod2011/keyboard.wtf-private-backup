using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using Microsoft.Extensions.Options;
using System.Collections;
using System.Text.Json;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class FirebaseIdentityService : IIdentityService
{
    private readonly FirebaseAuth _firebaseAuth;
    private readonly IAccountStore _store;
    private readonly ILogger<FirebaseIdentityService> _logger;

    public FirebaseIdentityService(
        IOptions<PaidSystemOptions> options,
        IAccountStore store,
        ILogger<FirebaseIdentityService> logger)
    {
        _store = store;
        _logger = logger;
        var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
        {
            ProjectId = options.Value.ProjectId,
            Credential = GoogleCredential.GetApplicationDefault(),
        });
        _firebaseAuth = FirebaseAuth.GetAuth(app);
    }

    public async Task<CallerIdentity?> AuthenticateAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var token = header[7..].Trim();
        if (token.StartsWith("kwt_at_", StringComparison.Ordinal))
            return await _store.AuthenticateDesktopTokenAsync(token, cancellationToken);

        try
        {
            return await VerifyFirebaseAsync(token, cancellationToken);
        }
        catch (FirebaseAuthException exception)
        {
            _logger.LogWarning(
                "Firebase ID token rejected: {AuthErrorCode}",
                exception.AuthErrorCode?.ToString() ?? "unknown");
            return null;
        }
    }

    public async Task<CallerIdentity> VerifyFirebaseAsync(
        string idToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var token = await _firebaseAuth.VerifyIdTokenAsync(idToken, checkRevoked: true);
        var email = GetString(token.Claims, "email");
        var emailVerified = GetBoolean(token.Claims, "email_verified");
        var signInProvider = GetSignInProvider(token.Claims);
        if (string.IsNullOrWhiteSpace(email)
            || !emailVerified
            || !string.Equals(signInProvider, "google.com", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Firebase identity policy rejected. HasEmail={HasEmail}, EmailVerified={EmailVerified}, SignInProvider={SignInProvider}",
                !string.IsNullOrWhiteSpace(email),
                emailVerified,
                string.IsNullOrWhiteSpace(signInProvider) ? "missing" : signInProvider);
            throw new UnauthorizedAccessException(
                "A verified Google sign-in is required for this keyboard.wtf account.");
        }
        var name = GetString(token.Claims, "name");
        var authenticatedAtUnix = GetLong(token.Claims, "auth_time");
        var authenticatedAt = authenticatedAtUnix > 0
            ? DateTimeOffset.FromUnixTimeSeconds(authenticatedAtUnix)
            : (DateTimeOffset?)null;
        var caller = new CallerIdentity(
            token.Uid,
            email,
            name,
            "firebase",
            AuthenticatedAt: authenticatedAt);
        await _store.EnsureAccountAsync(caller, cancellationToken);
        return caller;
    }

    public async Task DeleteFirebaseUserAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _firebaseAuth.RevokeRefreshTokensAsync(userId);
        cancellationToken.ThrowIfCancellationRequested();
        await _firebaseAuth.DeleteUserAsync(userId);
    }

    internal static bool IsAllowedGoogleIdentity(IReadOnlyDictionary<string, object> claims) =>
        !string.IsNullOrWhiteSpace(GetString(claims, "email"))
        && GetBoolean(claims, "email_verified")
        && string.Equals(GetSignInProvider(claims), "google.com", StringComparison.Ordinal);

    private static string GetString(IReadOnlyDictionary<string, object> claims, string key) =>
        claims.TryGetValue(key, out var value) ? GetScalarString(value) : "";

    private static bool GetBoolean(IReadOnlyDictionary<string, object> claims, string key) =>
        claims.TryGetValue(key, out var value)
        && value is not null
        && (value is bool boolean
            ? boolean
            : value is JsonElement element
                ? element.ValueKind is JsonValueKind.True
                    || (element.ValueKind is JsonValueKind.String
                        && bool.TryParse(element.GetString(), out var jsonParsed)
                        && jsonParsed)
                : bool.TryParse(Convert.ToString(value), out var parsed) && parsed);

    private static long GetLong(IReadOnlyDictionary<string, object> claims, string key)
    {
        if (!claims.TryGetValue(key, out var value) || value is null)
            return 0;
        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsed)
                ? parsed
                : 0;
        try { return Convert.ToInt64(value); }
        catch (FormatException) { return 0; }
        catch (OverflowException) { return 0; }
    }

    private static string GetSignInProvider(IReadOnlyDictionary<string, object> claims)
    {
        if (!claims.TryGetValue("firebase", out var firebase) || firebase is null)
            return "";
        if (firebase is IReadOnlyDictionary<string, object> readOnly)
            return GetString(readOnly, "sign_in_provider");
        if (firebase is IDictionary<string, object> dictionary
            && dictionary.TryGetValue("sign_in_provider", out var provider))
            return GetScalarString(provider);
        if (firebase is IDictionary legacy
            && legacy.Contains("sign_in_provider"))
            return GetScalarString(legacy["sign_in_provider"]);
        if (firebase is JsonElement element
            && element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty("sign_in_provider", out var jsonProvider))
            return GetScalarString(jsonProvider);

        // Firebase Admin currently returns JSON-backed nested claim objects. Keep
        // this fallback shape-tolerant without trusting or logging claim content.
        try
        {
            using var document = JsonDocument.Parse(Convert.ToString(firebase) ?? "");
            if (document.RootElement.ValueKind is JsonValueKind.Object
                && document.RootElement.TryGetProperty("sign_in_provider", out var parsedProvider))
                return GetScalarString(parsedProvider);
        }
        catch (JsonException)
        {
            // An unrecognised claim shape is rejected by the caller.
        }
        return "";
    }

    private static string GetScalarString(object? value) => value switch
    {
        null => "",
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
        JsonElement element => element.ToString(),
        _ => Convert.ToString(value) ?? "",
    };
}
