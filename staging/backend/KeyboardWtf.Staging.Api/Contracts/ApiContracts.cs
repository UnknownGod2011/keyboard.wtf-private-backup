using System.Text.Json;

namespace KeyboardWtf.Staging.Api.Contracts;

public sealed record DesktopLoginStartRequest(
    string State,
    string DeviceNonce,
    string PkceChallenge,
    string CallbackUri);

public sealed record DesktopLoginStartResponse(
    string AttemptId,
    string BrowserUrl,
    DateTimeOffset ExpiresAt);

public sealed record DesktopLoginAuthorizeRequest(
    string AttemptId,
    string State,
    string DeviceNonce);

public sealed record DesktopLoginAuthorizeResponse(
    string CallbackUrl,
    DateTimeOffset ExpiresAt);

public sealed record DesktopLoginExchangeRequest(
    string AttemptId,
    string State,
    string DeviceNonce,
    string AuthorizationCode,
    string PkceVerifier,
    string DeviceName,
    string DeviceInstallationId);

public sealed record SessionRefreshRequest(string RefreshToken);
public sealed record LogoutRequest(string? SessionId, bool AllDevices = false);
public sealed record RevokeDeviceRequest(string DeviceId);
public sealed record DeleteAccountRequest(bool ConfirmPermanentDeletion);

public sealed record GenerateRequest(
    string RequestId,
    string Prompt,
    string? SystemInstruction,
    int MaxOutputTokens = 1024,
    long? MaxCreditMicros = null,
    string? TaskKind = null,
    JsonElement? ResponseJsonSchema = null,
    IReadOnlyList<InlineImageRequest>? Images = null);

public sealed record InlineImageRequest(string MimeType, string Data);

public sealed record GenerateResponse(
    string RequestId,
    string Text,
    string Model,
    long ChargedCreditMicros,
    long RemainingCreditMicros,
    object Usage,
    bool Verified);

public sealed record CreateCheckoutRequest(string ProductSku, string IdempotencyKey);
public sealed record CancelCheckoutRequest(string PurchaseId);
public sealed record CancelSubscriptionRequest(bool AtCycleEnd = true);
public sealed record CheckoutConfirmationRequest(
    string PurchaseId,
    string ProviderPaymentId,
    string ProviderOrderOrSubscriptionId,
    string Signature);

public sealed record AdminGrantRequest(
    string RecipientEmail,
    long CreditMicros,
    string Reason,
    string IdempotencyKey);
