using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Endpoints;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    // Reject oversized bodies before JSON binding or authentication work.
    // Image requests remain possible within the explicit 8 MB decoded limit.
    options.Limits.MaxRequestBodySize = 12 * 1024 * 1024;
});
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddRequestTimeouts(options =>
{
    options.DefaultPolicy = new RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(100),
        TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
    };
});
builder.Services.AddOptions<PaidSystemOptions>()
    .Bind(builder.Configuration.GetSection(PaidSystemOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(options => options.AccessTokenMinutes is >= 5 and <= 60, "Access token lifetime is unsafe.")
    .Validate(options => options.RefreshTokenDays is >= 1 and <= 90, "Refresh token lifetime is unsafe.")
    .Validate(options => options.MaxRetries is >= 0 and <= 3, "Retry count is unsafe.")
    .Validate(options => options.ModelRoutingCacheSeconds is >= 30 and <= 900, "Model-routing cache lifetime is unsafe.")
    .Validate(options => options.LiveSessionMaxSeconds is >= 30 and <= 900, "Live session lifetime is unsafe.")
    .Validate(options => options.LiveSessionIdleSeconds is >= 15 and <= 120, "Live idle lifetime is unsafe.")
    .Validate(options => options.LiveSessionCreditMicrosCap > 0, "Live session budget must be positive.")
    .Validate(options => options.LiveReservationWindowCreditMicros > 0
        && options.LiveReservationWindowCreditMicros <= options.LiveSessionCreditMicrosCap,
        "Live rolling reservation window is unsafe.")
    .Validate(options => options.LiveUsageCheckpointSeconds is >= 10 and <= 120, "Live usage checkpoint interval is unsafe.")
    .Validate(options => options.LiveEstimateSafetyBasisPoints is >= 10_000 and <= 20_000, "Live estimate safety margin is unsafe.")
    .Validate(options => options.GlobalUsageShardCount is >= 8 and <= 128, "Global usage shard count is unsafe.")
    .Validate(options => options.ReservationLeaseGraceSeconds is >= 30 and <= 600, "Reservation lease grace period is unsafe.")
    .Validate(options => options.ReconciliationBatchSize is >= 10 and <= 500, "Reconciliation batch size is unsafe.")
    .Validate(options => options.ReconciliationMaxRecordsPerRun is >= 100 and <= 100_000, "Reconciliation run limit is unsafe.")
    .Validate(options => options.ReconciliationLeaseSeconds is >= 60 and <= 3600, "Reconciliation lease is unsafe.")
    .Validate(options => options.PerUserSpendAlertCreditMicros > 0, "Per-user spend alert threshold must be positive.")
    .Validate(options => options.PerUserDailyCreditMicrosCap > 0, "Per-user daily spending cap must be positive.")
    .Validate(options => options.MaxAccountCreditMicros > 0, "Account balance cap must be positive.")
    .Validate(options => options.StuckPaymentMinutes is >= 5 and <= 1440, "Stuck payment threshold is unsafe.")
    .Validate(options => DeploymentSafety.Validate(
        options,
        Environment.GetEnvironmentVariable("RAZORPAY_KEY_ID")),
        "The paid-system deployment environment is inconsistent.")
    .ValidateOnStart();
var paidOptions = builder.Configuration
    .GetSection(PaidSystemOptions.SectionName)
    .Get<PaidSystemOptions>() ?? new PaidSystemOptions();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(paidOptions.AllowedOrigins)
            .WithMethods("GET", "POST", "OPTIONS")
            .WithHeaders("Authorization", "Content-Type", "X-Request-Id", ClientCompatibility.DesktopVersionHeader)
            .SetPreflightMaxAge(TimeSpan.FromHours(1));
    });
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 600,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
});
builder.Services.AddSingleton(_ => FirestoreDb.Create(paidOptions.ProjectId));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IAccountStore, FirestoreAccountStore>();
builder.Services.AddSingleton<ICatalogService, CatalogService>();
builder.Services.AddSingleton<IModelRoutingService, ModelRoutingService>();
builder.Services.AddSingleton<IIdentityService, FirebaseIdentityService>();
builder.Services.AddSingleton<DesktopLoginService>();
builder.Services.AddSingleton<AccountDeletionService>();
builder.Services.AddSingleton<AiUsageService>();
builder.Services.AddHttpClient<IVertexAiClient, VertexAiClient>();
builder.Services.AddHttpClient<IPaymentProvider, RazorpayPaymentProvider>();
builder.Services.AddSingleton<IVertexAccessTokenProvider, VertexAccessTokenProvider>();
builder.Services.AddSingleton<VertexLiveProxyService>();
builder.Services.AddSingleton<PaymentReconciliationService>();
builder.Services.AddSingleton<UsageReconciliationService>();
builder.Services.AddSingleton<LedgerInvariantService>();
builder.Services.AddSingleton<MaintenanceRunner>();
builder.Services.AddSingleton<OperationalHealthService>();

var app = builder.Build();
var maintenanceJob = MaintenanceRunner.ParseJob(args);
if (!string.IsNullOrWhiteSpace(maintenanceJob))
{
    try
    {
        await app.Services.GetRequiredService<MaintenanceRunner>()
            .RunAsync(maintenanceJob, app.Lifetime.ApplicationStopping);
    }
    catch (Exception exception)
    {
        // Jobs must fail visibly and non-zero.  An unhandled background-process
        // failure can be reported as a container signal and makes the run hard
        // to audit or alert on.
        app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MaintenanceJob")
            .LogError(exception, "Maintenance command failed job={Job}", maintenanceJob);
        Environment.ExitCode = 1;
    }
    return;
}
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseRequestTimeouts();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20),
});
app.MapKeyboardWtfApi();
app.Run();

public partial class Program;
