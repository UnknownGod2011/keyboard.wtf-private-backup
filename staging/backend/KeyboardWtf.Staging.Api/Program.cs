using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Endpoints;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.AspNetCore.Http.Timeouts;

var builder = WebApplication.CreateBuilder(args);
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
    .Validate(options => options.LiveSessionMaxSeconds is >= 30 and <= 900, "Live session lifetime is unsafe.")
    .Validate(options => options.LiveSessionCreditMicrosCap > 0, "Live session budget must be positive.")
    .Validate(options => options.ReservationLeaseGraceSeconds is >= 30 and <= 600, "Reservation lease grace period is unsafe.")
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
            .WithHeaders("Authorization", "Content-Type", "X-Request-Id")
            .SetPreflightMaxAge(TimeSpan.FromHours(1));
    });
});
builder.Services.AddSingleton(_ => FirestoreDb.Create(paidOptions.ProjectId));
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IAccountStore, FirestoreAccountStore>();
builder.Services.AddSingleton<ICatalogService, CatalogService>();
builder.Services.AddSingleton<IIdentityService, FirebaseIdentityService>();
builder.Services.AddSingleton<DesktopLoginService>();
builder.Services.AddSingleton<AiUsageService>();
builder.Services.AddHttpClient<IVertexAiClient, VertexAiClient>();
builder.Services.AddHttpClient<IPaymentProvider, RazorpayPaymentProvider>();
builder.Services.AddSingleton<IVertexAccessTokenProvider, VertexAccessTokenProvider>();
builder.Services.AddSingleton<VertexLiveProxyService>();

var app = builder.Build();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseCors();
app.UseRequestTimeouts();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20),
});
app.MapKeyboardWtfApi();
app.Run();

public partial class Program;
