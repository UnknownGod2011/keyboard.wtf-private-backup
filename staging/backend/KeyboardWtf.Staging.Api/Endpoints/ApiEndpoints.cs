using System.Text.Json;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Contracts;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Endpoints;

public static class ApiEndpoints
{
    public static void MapKeyboardWtfApi(this WebApplication app)
    {
        app.MapGet("/health/live", () => TypedResults.Ok(new { status = "live" }));
        app.MapGet("/health/ready", (IOptions<PaidSystemOptions> options) =>
            TypedResults.Ok(new
            {
                status = options.Value.Enabled ? "ready" : "disabled",
                environment = "staging",
                project = options.Value.ProjectId,
            }));

        var api = app.MapGroup("/v1");
        api.MapGet("/catalog", async (ICatalogService catalog, CancellationToken token) =>
        {
            var snapshot = await catalog.GetAsync(token);
            return TypedResults.Ok(new
            {
                snapshot.Version,
                snapshot.KillSwitch,
                snapshot.PaymentsKillSwitch,
                Products = snapshot.Products.Where(product => product.Enabled).Select(product => new
                {
                    product.Sku,
                    product.Kind,
                    product.DisplayName,
                    product.AmountMinor,
                    product.Currency,
                    product.CreditMicros,
                }),
            });
        });

        api.MapPost("/desktop-login/start", async (
            DesktopLoginStartRequest request,
            DesktopLoginService login,
            CancellationToken token) =>
            TypedResults.Ok(await login.StartAsync(request, token)));

        api.MapPost("/desktop-login/authorize", async (
            DesktopLoginAuthorizeRequest request,
            HttpContext context,
            IIdentityService identities,
            DesktopLoginService login,
            CancellationToken token) =>
        {
            var caller = await RequireCallerAsync(context, identities, token);
            if (!string.Equals(caller.AuthenticationKind, "firebase", StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Desktop login approval requires a current Google sign-in.");
            return TypedResults.Ok(await login.AuthorizeAsync(caller, request, token));
        });

        api.MapPost("/desktop-login/exchange", async (
            DesktopLoginExchangeRequest request,
            DesktopLoginService login,
            CancellationToken token) =>
            TypedResults.Ok(await login.ExchangeAsync(request, token)));

        api.MapPost("/sessions/refresh", async (
            SessionRefreshRequest request,
            DesktopLoginService login,
            CancellationToken token) =>
            TypedResults.Ok(await login.RefreshAsync(request, token)));

        api.MapGet("/account", async (
            HttpContext context,
            IIdentityService identities,
            IAccountStore store,
            IOptions<PaidSystemOptions> options,
            CancellationToken token) =>
        {
            var caller = await RequireCallerAsync(context, identities, token);
            await store.EnsureAccountAsync(caller, token);
            var account = await store.GetAccountAsync(caller.UserId, token);
            return TypedResults.Ok(new
            {
                account.UserId,
                account.Email,
                account.DisplayName,
                account.Status,
                account.PlanId,
                account.AvailableCreditMicros,
                account.ReservedCreditMicros,
                account.CycleUsageCreditMicros,
                account.CycleEndsAt,
                account.Devices,
                account.RecentUsage,
                account.RecentPurchases,
                account.SubscriptionStatus,
                IsAdmin = options.Value.AdminEmails.Contains(
                    caller.Email,
                    StringComparer.OrdinalIgnoreCase),
            });
        });

        api.MapPost("/sessions/logout", async (
            LogoutRequest request,
            HttpContext context,
            IIdentityService identities,
            IAccountStore store,
            CancellationToken token) =>
        {
            var caller = await RequireCallerAsync(context, identities, token);
            await store.RevokeSessionAsync(caller.UserId, request.SessionId ?? caller.SessionId, request.AllDevices, token);
            return TypedResults.Ok(new { status = "signed_out" });
        });

        api.MapPost("/devices/revoke", async (
            RevokeDeviceRequest request,
            HttpContext context,
            IIdentityService identities,
            IAccountStore store,
            CancellationToken token) =>
        {
            var caller = await RequireCallerAsync(context, identities, token);
            await store.RevokeDeviceAsync(caller.UserId, request.DeviceId, token);
            return TypedResults.Ok(new { status = "revoked", request.DeviceId });
        });

        api.MapPost("/ai/generate", async (
            GenerateRequest request,
            HttpContext context,
            IIdentityService identities,
            AiUsageService ai,
            CancellationToken token) =>
        {
            var caller = await RequireCallerAsync(context, identities, token);
            return TypedResults.Ok(await ai.GenerateAsync(caller, request, token));
        });
        api.Map("/ai/live", async (
            HttpContext context,
            IIdentityService identities,
            VertexLiveProxyService live,
            CancellationToken token) =>
        {
            var caller = await RequireCallerAsync(context, identities, token);
            await live.RunAsync(context, caller, token);
        }).DisableRequestTimeout();

        api.MapPost("/billing/checkout", CreateCheckoutAsync);
        api.MapPost("/billing/cancel", CancelCheckoutAsync);
        api.MapPost("/billing/confirm", ConfirmCheckoutAsync);
        api.MapPost("/billing/razorpay/webhook", HandleWebhookAsync);
        api.MapPost("/admin/grants", GrantCreditsAsync);
    }

    private static async Task<IResult> CreateCheckoutAsync(
        CreateCheckoutRequest request,
        HttpContext context,
        IIdentityService identities,
        IAccountStore store,
        ICatalogService catalog,
        IPaymentProvider payments,
        CancellationToken token)
    {
        var caller = await RequireCallerAsync(context, identities, token);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("A checkout idempotency key is required.");
        var product = await catalog.GetProductAsync(request.ProductSku, token);
        var purchaseId = $"pur_{Crypto.Sha256($"{caller.UserId}:{request.IdempotencyKey}")[..24]}";
        if (!await store.BeginPurchaseAsync(
                caller.UserId, purchaseId, product, request.IdempotencyKey, token))
            throw new InvalidOperationException("This checkout request was already started. Refresh the account before retrying.");
        try
        {
            if (string.Equals(product.Kind, "subscription", StringComparison.Ordinal))
            {
                var subscription = await payments.CreateSubscriptionAsync(purchaseId, product, token);
                await store.AttachPurchaseProviderObjectAsync(
                    caller.UserId, purchaseId, subscription.ProviderSubscriptionId, "subscription", token);
                return TypedResults.Ok(subscription);
            }

            var order = await payments.CreateOrderAsync(purchaseId, product, token);
            await store.AttachPurchaseProviderObjectAsync(
                caller.UserId, purchaseId, order.ProviderOrderId, "order", token);
            return TypedResults.Ok(order);
        }
        catch (Exception exception)
        {
            await store.MarkPurchaseFailedAsync(
                caller.UserId,
                purchaseId,
                exception is OperationCanceledException ? "cancelled" : "provider_error",
                CancellationToken.None);
            throw;
        }
    }

    private static async Task<IResult> ConfirmCheckoutAsync(
        CheckoutConfirmationRequest request,
        HttpContext context,
        IIdentityService identities,
        IAccountStore store,
        IPaymentProvider payments,
        CancellationToken token)
    {
        var caller = await RequireCallerAsync(context, identities, token);
        var isOrder = request.ProviderOrderOrSubscriptionId.StartsWith("order_", StringComparison.Ordinal);
        var valid = isOrder
            ? payments.VerifyOrderCheckoutSignature(
                request.ProviderOrderOrSubscriptionId,
                request.ProviderPaymentId,
                request.Signature)
            : payments.VerifySubscriptionCheckoutSignature(
                request.ProviderOrderOrSubscriptionId,
                request.ProviderPaymentId,
                request.Signature);
        if (!valid)
            throw new UnauthorizedAccessException("The checkout signature is invalid.");
        await store.MarkCheckoutSignatureVerifiedAsync(
            caller.UserId,
            request.PurchaseId,
            request.ProviderOrderOrSubscriptionId,
            request.ProviderPaymentId,
            token);
        return TypedResults.Accepted(
            $"/v1/account",
            new { status = "verification_pending", message = "Waiting for authoritative payment confirmation." });
    }

    private static async Task<IResult> CancelCheckoutAsync(
        CancelCheckoutRequest request,
        HttpContext context,
        IIdentityService identities,
        IAccountStore store,
        CancellationToken token)
    {
        var caller = await RequireCallerAsync(context, identities, token);
        if (string.IsNullOrWhiteSpace(request.PurchaseId) || request.PurchaseId.Length > 80)
            throw new ArgumentException("A valid checkout purchase ID is required.");
        await store.CancelPendingPurchaseAsync(caller.UserId, request.PurchaseId, token);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> HandleWebhookAsync(
        HttpContext context,
        IPaymentProvider payments,
        IAccountStore store,
        CancellationToken token)
    {
        const int maxWebhookBytes = 1024 * 1024;
        if (context.Request.ContentLength > maxWebhookBytes)
            throw new ArgumentException("The payment event is too large.");
        context.Request.EnableBuffering(64 * 1024, maxWebhookBytes);
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        string rawBody;
        try
        {
            rawBody = await reader.ReadToEndAsync(token);
        }
        catch (IOException)
        {
            throw new ArgumentException("The payment event is too large.");
        }
        if (System.Text.Encoding.UTF8.GetByteCount(rawBody) > maxWebhookBytes)
            throw new ArgumentException("The payment event is too large.");
        context.Request.Body.Position = 0;
        var signature = context.Request.Headers["X-Razorpay-Signature"].ToString();
        if (!payments.VerifyWebhookSignature(rawBody, signature))
            return TypedResults.Unauthorized();

        using var document = JsonDocument.Parse(rawBody);
        var root = document.RootElement;
        var eventType = root.TryGetProperty("event", out var eventValue)
            ? eventValue.GetString() ?? "unknown"
            : "unknown";
        var headerEventId = context.Request.Headers["X-Razorpay-Event-Id"].ToString();
        var eventId = string.IsNullOrWhiteSpace(headerEventId)
            ? Crypto.Sha256(rawBody)
            : headerEventId;
        var parsed = ParseWebhook(root, eventType);
        var result = await store.ApplyPaymentWebhookAsync(
            eventId,
            eventType,
            parsed.ProviderObjectId,
            parsed.PaymentId,
            parsed.OrderId,
            parsed.SubscriptionId,
            parsed.PurchaseId,
            parsed.ProviderEventObjectId,
            parsed.AmountMinor,
            parsed.Status,
            token,
            parsed.SubscriptionCycleEndsAt);
        return result.Outcome == "unmatched"
            ? Results.Conflict(new
            {
                status = "unmatched",
                message = "The payment object is not attached yet; retry this event.",
            })
            : TypedResults.Ok(result);
    }

    private static async Task<IResult> GrantCreditsAsync(
        AdminGrantRequest request,
        HttpContext context,
        IIdentityService identities,
        IAccountStore store,
        IOptions<PaidSystemOptions> options,
        CancellationToken token)
    {
        var caller = await RequireCallerAsync(context, identities, token);
        if (!options.Value.AdminEmails.Contains(caller.Email, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("This endpoint is restricted to staging administrators.");
        if (!System.Net.Mail.MailAddress.TryCreate(request.RecipientEmail, out _))
            throw new ArgumentException("A valid recipient email is required.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 300)
            throw new ArgumentException("A concise grant reason is required.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            throw new ArgumentException("A grant idempotency key is required.");
        var recipientId = await store.FindUserIdByEmailAsync(request.RecipientEmail, token)
            ?? throw new KeyNotFoundException("The staging recipient account does not exist yet.");
        var account = await store.GrantCreditsAsync(
            recipientId,
            request.CreditMicros,
            request.Reason,
            request.IdempotencyKey,
            caller.Email,
            token);
        return TypedResults.Ok(new
        {
            status = "granted",
            account.UserId,
            account.AvailableCreditMicros,
        });
    }

    private static async Task<CallerIdentity> RequireCallerAsync(
        HttpContext context,
        IIdentityService identities,
        CancellationToken cancellationToken) =>
        await identities.AuthenticateAsync(context, cancellationToken)
        ?? throw new AuthenticationRequiredException("A valid sign-in is required.");

    private static (
        string ProviderObjectId,
        string? PaymentId,
        string? OrderId,
        string? SubscriptionId,
        string? PurchaseId,
        string? ProviderEventObjectId,
        long? AmountMinor,
        string Status,
        DateTimeOffset? SubscriptionCycleEndsAt)
        ParseWebhook(JsonElement root, string eventType)
    {
        var payload = root.TryGetProperty("payload", out var value) ? value : default;
        var payment = Entity(payload, "payment");
        var order = Entity(payload, "order");
        var subscription = Entity(payload, "subscription");
        var refund = Entity(payload, "refund");
        var dispute = Entity(payload, "dispute");
        var paymentId = Property(payment, "id")
            ?? Property(refund, "payment_id")
            ?? Property(dispute, "payment_id");
        var orderId = Property(order, "id") ?? Property(payment, "order_id");
        var subscriptionId = Property(subscription, "id") ?? Property(payment, "subscription_id");
        var purchaseId = Note(order, "purchase_id")
            ?? Note(subscription, "purchase_id")
            ?? Note(payment, "purchase_id");
        var providerEventObjectId = Property(refund, "id")
            ?? Property(dispute, "id")
            ?? paymentId;
        var amountMinor = Number(refund, "amount")
            ?? Number(dispute, "amount")
            ?? Number(payment, "amount");
        var providerObjectId = subscriptionId
            ?? orderId
            ?? Property(refund, "payment_id")
            ?? Property(dispute, "payment_id")
            ?? paymentId
            ?? throw new ArgumentException("The payment event has no supported provider object.");
        var status = Property(subscription, "status")
            ?? Property(order, "status")
            ?? Property(payment, "status")
            ?? Property(refund, "status")
            ?? Property(dispute, "status")
            ?? eventType;
        var subscriptionCycleEndsAt = Number(subscription, "current_end") is long currentEnd
            && currentEnd > 0
                ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeSeconds(currentEnd)
                : null;
        return (
            providerObjectId,
            paymentId,
            orderId,
            subscriptionId,
            purchaseId,
            providerEventObjectId,
            amountMinor,
            status,
            subscriptionCycleEndsAt);
    }

    private static JsonElement Entity(JsonElement payload, string name)
    {
        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(name, out var wrapper)
            && wrapper.TryGetProperty("entity", out var entity))
            return entity;
        return default;
    }

    private static string? Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static string? Note(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("notes", out var notes)
        && notes.ValueKind == JsonValueKind.Object
        && notes.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
