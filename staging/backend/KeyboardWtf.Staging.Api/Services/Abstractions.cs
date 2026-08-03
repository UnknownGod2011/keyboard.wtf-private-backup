using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Contracts;
using System.Text.Json;

namespace KeyboardWtf.Staging.Api.Services;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IIdentityService
{
    Task<CallerIdentity?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken);
    Task<CallerIdentity> VerifyFirebaseAsync(string idToken, CancellationToken cancellationToken);
}

public interface IAccountStore
{
    Task EnsureAccountAsync(CallerIdentity caller, CancellationToken cancellationToken);
    Task<AccountSnapshot> GetAccountAsync(string userId, CancellationToken cancellationToken);
    Task<ReservationResult> ReserveAsync(
        CallerIdentity caller,
        string requestId,
        string model,
        string pricingVersion,
        long reserveCreditMicros,
        long taskCapCreditMicros,
        int requestsPerMinute,
        int maxConcurrent,
        long globalDailyCap,
        CancellationToken cancellationToken);
    Task<SettlementResult> SettleAsync(
        string userId,
        string requestId,
        ProviderUsage usage,
        long actualCreditMicros,
        long providerCostUsdMicros,
        string outcome,
        CancellationToken cancellationToken);
    Task MarkReservationProviderStartedAsync(
        string userId,
        string requestId,
        CancellationToken cancellationToken);
    Task MarkReservationForReconciliationAsync(
        string userId,
        string requestId,
        string outcome,
        CancellationToken cancellationToken);
    Task ReleaseReservationAsync(
        string userId,
        string requestId,
        string outcome,
        CancellationToken cancellationToken);
    Task<DesktopLoginAttempt> CreateLoginAttemptAsync(
        string stateHash,
        string deviceNonceHash,
        string pkceChallenge,
        string callbackUri,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
    Task<(DesktopLoginAttempt Attempt, string AuthorizationCode)> AuthorizeLoginAttemptAsync(
        string attemptId,
        string stateHash,
        string deviceNonceHash,
        string userId,
        CancellationToken cancellationToken);
    Task<DesktopSessionTokens> ExchangeLoginAttemptAsync(
        string attemptId,
        string stateHash,
        string deviceNonceHash,
        string authorizationCodeHash,
        string pkceVerifier,
        string deviceName,
        string deviceInstallationId,
        TimeSpan accessLifetime,
        TimeSpan refreshLifetime,
        CancellationToken cancellationToken);
    Task<CallerIdentity?> AuthenticateDesktopTokenAsync(string accessToken, CancellationToken cancellationToken);
    Task<DesktopSessionTokens> RefreshSessionAsync(
        string refreshToken,
        TimeSpan accessLifetime,
        TimeSpan refreshLifetime,
        CancellationToken cancellationToken);
    Task RevokeSessionAsync(string userId, string? sessionId, bool allDevices, CancellationToken cancellationToken);
    Task RevokeDeviceAsync(string userId, string deviceId, CancellationToken cancellationToken);
    Task<string?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken);
    Task<AccountSnapshot> GrantCreditsAsync(
        string userId,
        long creditMicros,
        string reason,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken);
    Task<bool> BeginPurchaseAsync(
        string userId,
        string purchaseId,
        CatalogProduct product,
        string idempotencyKey,
        CancellationToken cancellationToken);
    Task AttachPurchaseProviderObjectAsync(
        string userId,
        string purchaseId,
        string providerObjectId,
        string providerObjectType,
        CancellationToken cancellationToken);
    Task MarkPurchaseFailedAsync(
        string userId,
        string purchaseId,
        string outcome,
        CancellationToken cancellationToken);
    Task CancelPendingPurchaseAsync(
        string userId,
        string purchaseId,
        CancellationToken cancellationToken);
    Task MarkCheckoutSignatureVerifiedAsync(
        string userId,
        string purchaseId,
        string providerObjectId,
        string providerPaymentId,
        CancellationToken cancellationToken);
    Task<PaymentWebhookResult> ApplyPaymentWebhookAsync(
        string eventId,
        string eventType,
        string providerObjectId,
        string? providerPaymentId,
        string? providerOrderId,
        string? providerSubscriptionId,
        string? purchaseId,
        string? providerEventObjectId,
        long? providerAmountMinor,
        string status,
        CancellationToken cancellationToken,
        DateTimeOffset? subscriptionCycleEndsAt = null);
}

public interface ICatalogService
{
    Task<CatalogSnapshot> GetAsync(CancellationToken cancellationToken);
    Task<CatalogProduct> GetProductAsync(string sku, CancellationToken cancellationToken);
    long EstimateReservationCreditMicros(
        PricingSnapshot pricing,
        string prompt,
        int maxOutputTokens,
        long taskCapCreditMicros);
    (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateActualCost(
        PricingSnapshot pricing,
        ProviderUsage usage);
}

public sealed record VertexGenerateResult(string Text, ProviderUsage Usage);

internal sealed class VertexRequestRejectedException : InvalidOperationException
{
    public VertexRequestRejectedException(int statusCode)
        : base($"Vertex AI rejected the request ({statusCode}).")
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

public interface IVertexAiClient
{
    Task<VertexGenerateResult> GenerateAsync(
        string model,
        string prompt,
        string? systemInstruction,
        int maxOutputTokens,
        JsonElement? responseJsonSchema,
        IReadOnlyList<InlineImageRequest>? images,
        CancellationToken cancellationToken);
}

public interface IPaymentProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<PaymentOrder> CreateOrderAsync(
        string purchaseId,
        CatalogProduct product,
        CancellationToken cancellationToken);
    Task<PaymentSubscription> CreateSubscriptionAsync(
        string purchaseId,
        CatalogProduct product,
        CancellationToken cancellationToken);
    bool VerifyOrderCheckoutSignature(string orderId, string paymentId, string signature);
    bool VerifySubscriptionCheckoutSignature(string subscriptionId, string paymentId, string signature);
    bool VerifyWebhookSignature(string rawBody, string signature);
}
