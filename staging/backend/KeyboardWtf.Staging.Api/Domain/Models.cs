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
    string? DeviceId = null,
    DateTimeOffset? AuthenticatedAt = null);

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
    string SubscriptionStatus = "none",
    long SubscriptionAllowanceCreditMicros = 0,
    long TopUpCreditMicros = 0,
    long OtherAllowanceCreditMicros = 0,
    string AllowanceRulesVersion = "allowance-v1");

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
    DateTimeOffset CreatedAt,
    string ActivityType = "ai_request",
    string PricingVersion = "",
    long CachedInputTokens = 0,
    long ReasoningTokens = 0,
    long InputAudioTokens = 0,
    long OutputAudioTokens = 0,
    long InputImageTokens = 0,
    long OutputImageTokens = 0,
    bool UsageEstimated = false,
    string ProviderModelVersion = "",
    string AccountingStatus = "settled");

public sealed record UsagePageSnapshot(
    IReadOnlyList<UsageSnapshot> Items,
    string? NextCursor);

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
    string? ProviderModelVersion = null,
    long InputAudioTokens = 0,
    long OutputAudioTokens = 0,
    long InputImageTokens = 0,
    long OutputImageTokens = 0,
    bool Estimated = false);

public sealed record PricingSnapshot(
    string Version,
    string Model,
    long InputUsdMicrosPerMillionTokens,
    long OutputUsdMicrosPerMillionTokens,
    long CachedInputUsdMicrosPerMillionTokens,
    long ReasoningUsdMicrosPerMillionTokens,
    long UsdToInrPaise,
    long CreditMicrosPerInrPaise,
    DateTimeOffset EffectiveAt,
    long InputAudioUsdMicrosPerMillionTokens = 1_000_000,
    long LiveInputTextUsdMicrosPerMillionTokens = 500_000,
    long LiveInputAudioUsdMicrosPerMillionTokens = 3_000_000,
    long LiveInputImageUsdMicrosPerMillionTokens = 3_000_000,
    long LiveOutputTextUsdMicrosPerMillionTokens = 2_000_000,
    long LiveOutputAudioUsdMicrosPerMillionTokens = 12_000_000);

public sealed record ModelCandidateSnapshot(
    string Model,
    string Location,
    PricingSnapshot Pricing);

public sealed record LiveRoutePolicySnapshot(
    int MaxSessionSeconds,
    long ReservationWindowCreditMicros,
    long MaxSessionCostCreditMicros);

public sealed record ModelRouteSnapshot(
    string Name,
    string Version,
    string Provider,
    IReadOnlyList<ModelCandidateSnapshot> Candidates,
    bool Enabled,
    bool KillSwitch,
    int MaxInputCharacters,
    int MaxOutputTokens,
    int TimeoutSeconds,
    int ThinkingBudget,
    int CanaryBasisPoints,
    string MinimumDesktopVersion,
    string LifecycleNote,
    LiveRoutePolicySnapshot? LivePolicy = null);

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

public sealed record ProviderPayment(
    string Id,
    string Status,
    long AmountMinor,
    string Currency,
    string? OrderId,
    string? SubscriptionId,
    bool Captured,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? InvoiceIssuedAt = null,
    DateTimeOffset? InvoicePaidAt = null);

public sealed record ProviderRefund(
    string Id,
    string PaymentId,
    string Status,
    long AmountMinor,
    DateTimeOffset? CreatedAt);

public sealed record ProviderDispute(
    string Id,
    string PaymentId,
    string Status,
    long AmountMinor,
    long AmountDeductedMinor,
    DateTimeOffset? CreatedAt);

public sealed record ProviderOrderInspection(
    string Id,
    string Status,
    long AmountMinor,
    string Currency,
    string? PurchaseId,
    string? ProductSku,
    IReadOnlyList<ProviderPayment> Payments);

public sealed record ProviderSubscriptionInspection(
    string Id,
    string Status,
    string? PurchaseId,
    string? ProductSku,
    DateTimeOffset? CurrentCycleStartsAt,
    DateTimeOffset? CurrentCycleEndsAt,
    IReadOnlyList<ProviderPayment> Charges);

public sealed record ReconciliationRunResult(
    string Job,
    string RunId,
    int Examined,
    int Repaired,
    int Quarantined,
    int Unchanged,
    bool LeaseAcquired,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record InvariantValidationResult(
    string RunId,
    int AccountsExamined,
    int Violations,
    IReadOnlyList<string> ViolationCategories,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);
