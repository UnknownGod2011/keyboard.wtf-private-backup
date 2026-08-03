namespace KeyboardWtf.Staging.Api.Domain;

public static class LedgerTypes
{
    public const string PurchaseCredit = "PURCHASE_CREDIT";
    public const string MonthlyAllowance = "MONTHLY_ALLOWANCE";
    public const string PromotionalCredit = "PROMOTIONAL_CREDIT";
    public const string AdminTestCredit = "ADMIN_TEST_CREDIT";
    public const string UsageReservation = "USAGE_RESERVATION";
    public const string UsageSettlement = "USAGE_SETTLEMENT";
    public const string ReservationRelease = "RESERVATION_RELEASE";
    public const string Refund = "REFUND";
    public const string Chargeback = "CHARGEBACK";
    public const string Expiration = "EXPIRATION";
    public const string ManualAdjustment = "MANUAL_ADJUSTMENT";
}

public sealed record CallerIdentity(
    string UserId,
    string Email,
    string DisplayName,
    string AuthenticationKind,
    string? SessionId = null,
    string? DeviceId = null);

public sealed record AccountSnapshot(
    string UserId,
    string Email,
    string DisplayName,
    string Status,
    string PlanId,
    long AvailableCreditMicros,
    long ReservedCreditMicros,
    long CycleUsageCreditMicros,
    DateTimeOffset? CycleEndsAt,
    IReadOnlyList<DeviceSnapshot> Devices,
    IReadOnlyList<UsageSnapshot> RecentUsage,
    IReadOnlyList<PurchaseSnapshot> RecentPurchases,
    string SubscriptionStatus = "none");

public sealed record DeviceSnapshot(
    string DeviceId,
    string Name,
    string Status,
    DateTimeOffset LastSeenAt,
    DateTimeOffset CreatedAt);

public sealed record UsageSnapshot(
    string RequestId,
    string Model,
    string Outcome,
    long ChargedCreditMicros,
    long InputTokens,
    long OutputTokens,
    DateTimeOffset CreatedAt);

public sealed record PurchaseSnapshot(
    string PurchaseId,
    string ProductSku,
    string Provider,
    string Status,
    long AmountMinor,
    string Currency,
    DateTimeOffset CreatedAt,
    bool Fulfilled = false);

public sealed record LedgerEntry(
    string EventId,
    string Type,
    long AvailableDeltaMicros,
    long ReservedDeltaMicros,
    string ReferenceType,
    string ReferenceId,
    string IdempotencyKey,
    string Actor,
    string Reason,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record Reservation(
    string RequestId,
    string UserId,
    long ReservedCreditMicros,
    string Model,
    string PricingVersion,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null,
    bool ProviderStarted = false);

public sealed record ProviderUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ReasoningTokens,
    long ImageTokens,
    long AudioTokens,
    long TotalTokens,
    string? ProviderRequestId,
    string? ProviderModelVersion = null);

public sealed record PricingSnapshot(
    string Version,
    string Model,
    long InputUsdMicrosPerMillionTokens,
    long OutputUsdMicrosPerMillionTokens,
    long CachedInputUsdMicrosPerMillionTokens,
    long ReasoningUsdMicrosPerMillionTokens,
    long UsdToInrPaise,
    long CreditMicrosPerInrPaise,
    DateTimeOffset EffectiveAt);

public sealed record CatalogProduct(
    string Sku,
    string Kind,
    string DisplayName,
    long AmountMinor,
    string Currency,
    long CreditMicros,
    string? RazorpayPlanId,
    bool Enabled);

public sealed record CatalogSnapshot(
    string Version,
    IReadOnlyList<CatalogProduct> Products,
    PricingSnapshot Pricing,
    bool KillSwitch,
    string? KillSwitchMessage,
    bool PaymentsKillSwitch = false,
    string? PaymentsKillSwitchMessage = null);

public sealed record ReservationResult(bool Created, Reservation Reservation, AccountSnapshot Account);

public sealed record SettlementResult(
    bool Settled,
    long ChargedCreditMicros,
    long ReleasedCreditMicros,
    AccountSnapshot Account);

public sealed record DesktopLoginAttempt(
    string AttemptId,
    string StateHash,
    string DeviceNonceHash,
    string PkceChallenge,
    string CallbackUri,
    string Status,
    DateTimeOffset ExpiresAt,
    string? UserId = null,
    string? AuthorizationCodeHash = null);

public sealed record DesktopSessionTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string SessionId,
    string DeviceId);

public sealed record PaymentOrder(
    string PurchaseId,
    string ProviderOrderId,
    string PublicKeyId,
    long AmountMinor,
    string Currency,
    string ProductSku,
    string Status);

public sealed record PaymentSubscription(
    string PurchaseId,
    string ProviderSubscriptionId,
    string PublicKeyId,
    string ProductSku,
    string Status);

public sealed record PaymentWebhookResult(bool Duplicate, string EventId, string Outcome);
