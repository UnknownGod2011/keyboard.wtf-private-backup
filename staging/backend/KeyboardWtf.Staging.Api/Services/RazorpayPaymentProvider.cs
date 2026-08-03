using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class RazorpayPaymentProvider : IPaymentProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _keyId;
    private readonly string _keySecret;
    private readonly string _webhookSecret;

    public RazorpayPaymentProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.razorpay.com/v1/");
        _keyId = Environment.GetEnvironmentVariable("RAZORPAY_KEY_ID") ?? "";
        _keySecret = Environment.GetEnvironmentVariable("RAZORPAY_KEY_SECRET") ?? "";
        _webhookSecret = Environment.GetEnvironmentVariable("RAZORPAY_WEBHOOK_SECRET") ?? "";
    }

    public string Name => "razorpay";
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_keyId) && !string.IsNullOrWhiteSpace(_keySecret);

    public async Task<PaymentOrder> CreateOrderAsync(
        string purchaseId,
        CatalogProduct product,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var payload = new
        {
            amount = product.AmountMinor,
            currency = product.Currency,
            receipt = purchaseId,
            notes = new Dictionary<string, string>
            {
                ["purchase_id"] = purchaseId,
                ["product_sku"] = product.Sku,
                ["environment"] = "staging",
            },
        };
        using var document = await SendAsync(HttpMethod.Post, "orders", payload, cancellationToken);
        var root = document.RootElement;
        return new PaymentOrder(
            purchaseId,
            root.GetProperty("id").GetString() ?? throw new InvalidOperationException("Razorpay returned no order ID."),
            _keyId,
            product.AmountMinor,
            product.Currency,
            product.Sku,
            root.TryGetProperty("status", out var status) ? status.GetString() ?? "created" : "created");
    }

    public async Task<PaymentSubscription> CreateSubscriptionAsync(
        string purchaseId,
        CatalogProduct product,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        if (string.IsNullOrWhiteSpace(product.RazorpayPlanId))
            throw new InvalidOperationException("The staging Razorpay subscription plan is not configured.");
        var payload = new
        {
            plan_id = product.RazorpayPlanId,
            total_count = 120,
            quantity = 1,
            customer_notify = 1,
            notes = new Dictionary<string, string>
            {
                ["purchase_id"] = purchaseId,
                ["product_sku"] = product.Sku,
                ["environment"] = "staging",
            },
        };
        using var document = await SendAsync(HttpMethod.Post, "subscriptions", payload, cancellationToken);
        var root = document.RootElement;
        return new PaymentSubscription(
            purchaseId,
            root.GetProperty("id").GetString() ?? throw new InvalidOperationException("Razorpay returned no subscription ID."),
            _keyId,
            product.Sku,
            root.TryGetProperty("status", out var status) ? status.GetString() ?? "created" : "created");
    }

    public bool VerifyOrderCheckoutSignature(string orderId, string paymentId, string signature) =>
        VerifyHmac($"{orderId}|{paymentId}", signature, _keySecret);

    public bool VerifySubscriptionCheckoutSignature(string subscriptionId, string paymentId, string signature) =>
        VerifyHmac($"{paymentId}|{subscriptionId}", signature, _keySecret);

    public bool VerifyWebhookSignature(string rawBody, string signature) =>
        VerifyHmac(rawBody, signature, _webhookSecret);

    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string relativeUri,
        object payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUri);
        var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_keyId}:{_keySecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Razorpay returned {(int)response.StatusCode}.");
        return JsonDocument.Parse(body);
    }

    private static bool VerifyHmac(string message, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature))
            return false;
        return Crypto.FixedTimeHexEquals(Crypto.HmacSha256Hex(secret, message), signature.Trim());
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Razorpay Test Mode is not configured.");
    }
}
