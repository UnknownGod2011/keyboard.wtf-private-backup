using System.ComponentModel.DataAnnotations;

namespace KeyboardWtf.Staging.Api.Configuration;

public sealed class PaidSystemOptions
{
    public const string SectionName = "PaidSystem";

    public bool Enabled { get; init; } = true;

    [Required]
    public string ProjectId { get; init; } = "keyboard-wtf-staging";

    [Required]
    public string Region { get; init; } = "asia-south1";

    [Required]
    public string VertexModel { get; init; } = "gemini-2.5-flash";

    [Required]
    public string VertexLiveRegion { get; init; } = "us-central1";

    [Required]
    public string VertexLiveModel { get; init; } = "gemini-live-2.5-flash-native-audio";

    [Required, Url]
    public string WebBaseUrl { get; init; } = "https://keyboard-wtf-paid-staging.vercel.app";

    public string[] AllowedOrigins { get; init; } = [];
    public string[] AdminEmails { get; init; } = [];
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 30;
    public int LoginAttemptMinutes { get; init; } = 5;
    public int MaxRequestSeconds { get; init; } = 90;
    public int MaxRetries { get; init; } = 1;
    public int MaxConcurrentRequestsPerUser { get; init; } = 1;
    public int MaxRequestsPerMinutePerUser { get; init; } = 12;
    public long GlobalDailyCreditMicrosCap { get; init; } = 1_000_000_000;
    public int GlobalUsageShardCount { get; init; } = 32;
    public long DefaultTaskCreditMicrosCap { get; init; } = 25_000_000;
    public long LiveSessionCreditMicrosCap { get; init; } = 5_000_000;
    public int LiveSessionMaxSeconds { get; init; } = 600;
    public long LiveSessionMaxTrafficBytes { get; init; } = 64 * 1024 * 1024;
    public int LiveUsageCheckpointSeconds { get; init; } = 30;
    public int LiveEstimateSafetyBasisPoints { get; init; } = 12_500;
    public int ReservationLeaseGraceSeconds { get; init; } = 120;
    public int ReconciliationBatchSize { get; init; } = 100;
    public int ReconciliationMaxRecordsPerRun { get; init; } = 5_000;
    public int ReconciliationLeaseSeconds { get; init; } = 600;
    public int PaymentReconciliationLookbackDays { get; init; } = 180;
    public long PerUserSpendAlertCreditMicros { get; init; } = 250_000_000;
    public int StuckPaymentMinutes { get; init; } = 30;
    public bool AllowBootstrapCatalog { get; init; } = true;
}
