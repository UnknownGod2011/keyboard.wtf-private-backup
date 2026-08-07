using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class RazorpayPaymentProvider : IPaymentProvider
{
    // Razorpay caps collection pages at 100 records.  Do not silently accept a
    // truncated financial history: a reconciliation run must quarantine the
    // purchase rather than decide it is complete from an incomplete page set.
    private const int InspectionPageSize = 100;
    private const int MaxInspectionPages = 50;
    private readonly HttpClient _httpClient;
    private readonly string _keyId;
    private readonly string _keySecret;
    private readonly string _webhookSecret;
    private readonly SemaphoreSlim _disputesGate = new(1, 1);
    private Task<IReadOnlyList<ProviderDispute>>? _disputesSnapshotTask;

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

    public async Task<ProviderOrderInspection> InspectOrderAsync(
        string orderId,
        CancellationToken cancellationToken)
    {
        EnsureProviderId(orderId, "order_");
        using var orderDocument = await SendAsync(HttpMethod.Get, $"orders/{orderId}", null, cancellationToken);
        using var paymentsDocument = await SendAsync(
            HttpMethod.Get,
            $"orders/{orderId}/payments",
            null,
            cancellationToken);
        var root = orderDocument.RootElement;
        var payments = Items(paymentsDocument.RootElement)
            .Select(ParsePayment)
            .Where(payment => string.Equals(payment.OrderId, orderId, StringComparison.Ordinal))
            .ToArray();
        return new ProviderOrderInspection(
            Text(root, "id"),
            Text(root, "status"),
            Number(root, "amount"),
            Text(root, "currency").ToUpperInvariant(),
            TextOrNull(root, "receipt") ?? Note(root, "purchase_id"),
            Note(root, "product_sku"),
            payments);
    }

    public async Task<ProviderSubscriptionInspection> InspectSubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        EnsureProviderId(subscriptionId, "sub_");
        using var subscriptionDocument = await SendAsync(
            HttpMethod.Get,
            $"subscriptions/{subscriptionId}",
            null,
            cancellationToken);
        using var invoiceDocument = await SendAsync(
            HttpMethod.Get,
            // Razorpay documents this subscription-scoped endpoint as returning
            // all invoices.  Do not add unsupported generic pagination options.
            $"invoices?subscription_id={Uri.EscapeDataString(subscriptionId)}",
            null,
            cancellationToken);
        var root = subscriptionDocument.RootElement;
        var paymentIds = Items(invoiceDocument.RootElement)
            .Select(invoice => TextOrNull(invoice, "payment_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var charges = new List<ProviderPayment>(paymentIds.Length);
        foreach (var paymentId in paymentIds)
            charges.Add(await InspectPaymentAsync(paymentId!, cancellationToken));
        return new ProviderSubscriptionInspection(
            Text(root, "id"),
            Text(root, "status"),
            Note(root, "purchase_id"),
            Note(root, "product_sku"),
            UnixTime(root, "current_end"),
            charges.OrderBy(charge => charge.CreatedAt).ToArray());
    }

    public async Task<IReadOnlyList<ProviderRefund>> InspectRefundsAsync(
        string paymentId,
        CancellationToken cancellationToken)
    {
        EnsureProviderId(paymentId, "pay_");
        var refunds = new List<ProviderRefund>();
        foreach (var item in await ReadPagedItemsAsync(
                     $"payments/{paymentId}/refunds",
                     cancellationToken))
        {
            var refund = new ProviderRefund(
                Text(item, "id"),
                TextOrNull(item, "payment_id") ?? paymentId,
                Text(item, "status"),
                Number(item, "amount"),
                UnixTime(item, "created_at"));
            if (string.Equals(refund.PaymentId, paymentId, StringComparison.Ordinal))
                refunds.Add(refund);
        }
        return refunds;
    }

    public async Task<IReadOnlyList<ProviderDispute>> InspectDisputesAsync(
        string paymentId,
        CancellationToken cancellationToken)
    {
        EnsureProviderId(paymentId, "pay_");
        // Razorpay documents /disputes as a global "fetch all" endpoint, not
        // as a payment-scoped/paginated endpoint.  Fetch it once per provider
        // instance (one Cloud Run maintenance execution) and filter locally;
        // scanning it once per purchase would become O(purchases × disputes).
        var disputes = await GetDisputesSnapshotAsync(cancellationToken);
        return disputes
            .Where(dispute => string.Equals(dispute.PaymentId, paymentId, StringComparison.Ordinal))
            .ToArray();
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
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUri);
        var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_keyId}:{_keySecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Razorpay returned {(int)response.StatusCode}.");
        return JsonDocument.Parse(body);
    }

    private async Task<ProviderPayment> InspectPaymentAsync(
        string paymentId,
        CancellationToken cancellationToken)
    {
        EnsureProviderId(paymentId, "pay_");
        using var document = await SendAsync(
            HttpMethod.Get,
            $"payments/{paymentId}",
            null,
            cancellationToken);
        return ParsePayment(document.RootElement);
    }

    private async Task<IReadOnlyList<JsonElement>> ReadPagedItemsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var result = new List<JsonElement>();
        for (var page = 0; page < MaxInspectionPages; page++)
        {
            var separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            using var document = await SendAsync(
                HttpMethod.Get,
                $"{path}{separator}count={InspectionPageSize}&skip={page * InspectionPageSize}",
                null,
                cancellationToken);
            var items = Items(document.RootElement).ToArray();
            // JsonElement is backed by its JsonDocument.  Each page document is
            // deliberately disposed before the next authenticated request, so
            // retain independent values rather than leaking disposed handles to
            // the reconciliation logic.
            result.AddRange(items.Select(item => item.Clone()));
            if (items.Length < InspectionPageSize)
                return result;
        }

        throw new InvalidOperationException(
            "Razorpay inspection exceeded the bounded pagination limit; reconciliation requires review.");
    }

    private async Task<IReadOnlyList<ProviderDispute>> GetDisputesSnapshotAsync(
        CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<ProviderDispute>> snapshotTask;
        await _disputesGate.WaitAsync(cancellationToken);
        try
        {
            snapshotTask = _disputesSnapshotTask ??= ReadDisputesSnapshotAsync(cancellationToken);
        }
        finally
        {
            _disputesGate.Release();
        }

        try
        {
            return await snapshotTask;
        }
        catch
        {
            // A cancelled or failed first attempt must not poison a later
            // reconciliation run in the same process.
            await _disputesGate.WaitAsync(CancellationToken.None);
            try
            {
                if (ReferenceEquals(_disputesSnapshotTask, snapshotTask))
                    _disputesSnapshotTask = null;
            }
            finally
            {
                _disputesGate.Release();
            }
            throw;
        }
    }

    private async Task<IReadOnlyList<ProviderDispute>> ReadDisputesSnapshotAsync(
        CancellationToken cancellationToken)
    {
        using var document = await SendAsync(HttpMethod.Get, "disputes", null, cancellationToken);
        return Items(document.RootElement)
            .Select(item => new ProviderDispute(
                Text(item, "id"),
                TextOrNull(item, "payment_id") ?? "",
                Text(item, "status"),
                Number(item, "amount"),
                UnixTime(item, "created_at")))
            .Where(dispute => !string.IsNullOrWhiteSpace(dispute.PaymentId))
            .ToArray();
    }

    private static ProviderPayment ParsePayment(JsonElement item) => new(
        Text(item, "id"),
        Text(item, "status"),
        Number(item, "amount"),
        Text(item, "currency").ToUpperInvariant(),
        TextOrNull(item, "order_id"),
        TextOrNull(item, "subscription_id"),
        item.TryGetProperty("captured", out var captured)
            && captured.ValueKind == JsonValueKind.True,
        UnixTime(item, "created_at"));

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("items", out var items)
        && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().ToArray()
            : [];

    private static string Text(JsonElement element, string name) =>
        TextOrNull(element, name) ?? "";

    private static string? TextOrNull(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static DateTimeOffset? UnixTime(JsonElement element, string name) =>
        Number(element, name) is var value && value > 0
            ? DateTimeOffset.FromUnixTimeSeconds(value)
            : null;

    private static string? Note(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("notes", out var notes)
        && notes.ValueKind == JsonValueKind.Object
        && notes.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void EnsureProviderId(string value, string prefix)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || value.Length > 100
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("The Razorpay object ID is invalid.");
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
