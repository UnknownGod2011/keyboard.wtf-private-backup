using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

public sealed class CatalogService : ICatalogService
{
    private readonly FirestoreDb _db;
    private readonly PaidSystemOptions _options;
    private readonly IClock _clock;
    private CatalogSnapshot? _cached;
    private DateTimeOffset _cachedUntil;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CatalogService(FirestoreDb db, IOptions<PaidSystemOptions> options, IClock clock)
    {
        _db = db;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<CatalogSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null && _cachedUntil > _clock.UtcNow)
            return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && _cachedUntil > _clock.UtcNow)
                return _cached;

            var snapshot = await _db.Document("config/catalog").GetSnapshotAsync(cancellationToken);
            _cached = snapshot.Exists ? FromDocument(snapshot) : BootstrapCatalog();
            _cachedUntil = _clock.UtcNow.AddMinutes(2);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CatalogProduct> GetProductAsync(string sku, CancellationToken cancellationToken)
    {
        var catalog = await GetAsync(cancellationToken);
        EnsurePaymentsEnabled(catalog);
        return catalog.Products.FirstOrDefault(product =>
                product.Enabled && string.Equals(product.Sku, sku, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The requested product is unavailable.");
    }

    internal static void EnsurePaymentsEnabled(CatalogSnapshot catalog)
    {
        if (catalog.PaymentsKillSwitch)
            throw new InvalidOperationException(
                catalog.PaymentsKillSwitchMessage ?? "Payments are temporarily unavailable.");
    }

    public long EstimateReservationCreditMicros(
        PricingSnapshot pricing,
        string prompt,
        int maxOutputTokens,
        long taskCapCreditMicros)
    {
        var estimatedInput = Math.Max(1, (prompt.Length + 3L) / 4L);
        var assumedUsage = new ProviderUsage(
            estimatedInput,
            Math.Clamp(maxOutputTokens, 1, 8192),
            0,
            0,
            0,
            0,
            estimatedInput + Math.Clamp(maxOutputTokens, 1, 8192),
            null);
        var (_, charge) = CalculateActualCost(pricing, assumedUsage);
        var withSafetyMargin = checked(charge + Math.Max(10_000, charge / 4));
        return Math.Clamp(withSafetyMargin, 1, taskCapCreditMicros);
    }

    public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateActualCost(
        PricingSnapshot pricing,
        ProviderUsage usage)
    {
        static decimal Cost(long tokens, long microsPerMillion) =>
            tokens <= 0 ? 0 : (decimal)tokens * microsPerMillion / 1_000_000m;

        // Vertex promptTokenCount includes cachedContentTokenCount. Charge the
        // cached portion at its discounted rate instead of charging it twice.
        var cachedInput = Math.Clamp(usage.CachedInputTokens, 0, usage.InputTokens);
        var regularInput = Math.Max(0, usage.InputTokens - cachedInput);
        var providerMicros = decimal.Ceiling(
            Cost(regularInput, pricing.InputUsdMicrosPerMillionTokens)
            + Cost(usage.OutputTokens, pricing.OutputUsdMicrosPerMillionTokens)
            + Cost(cachedInput, pricing.CachedInputUsdMicrosPerMillionTokens)
            + Cost(usage.ReasoningTokens, pricing.ReasoningUsdMicrosPerMillionTokens));
        var providerCost = checked((long)providerMicros);
        var inrPaise = decimal.Ceiling(
            providerCost * (decimal)pricing.UsdToInrPaise / 1_000_000m);
        var creditMicros = decimal.Ceiling(inrPaise * pricing.CreditMicrosPerInrPaise);
        return (providerCost, Math.Max(1, checked((long)creditMicros)));
    }

    private CatalogSnapshot FromDocument(DocumentSnapshot document)
    {
        var data = document.ToDictionary();
        var pricingMap = data.TryGetValue("pricing", out var pricingValue)
            ? pricingValue as IDictionary<string, object>
            : null;
        var pricing = pricingMap is null
            ? BootstrapPricing()
            : new PricingSnapshot(
                GetString(pricingMap, "version", "vertex-gemini-2.5-flash-2026-08"),
                GetString(pricingMap, "model", _options.VertexModel),
                GetLong(pricingMap, "inputUsdMicrosPerMillionTokens", 150_000),
                GetLong(pricingMap, "outputUsdMicrosPerMillionTokens", 3_500_000),
                GetLong(pricingMap, "cachedInputUsdMicrosPerMillionTokens", 37_500),
                GetLong(pricingMap, "reasoningUsdMicrosPerMillionTokens", 3_500_000),
                GetLong(pricingMap, "usdToInrPaise", 8_700),
                GetLong(pricingMap, "creditMicrosPerInrPaise", 10_000),
                _clock.UtcNow);

        var products = new List<CatalogProduct>();
        if (data.TryGetValue("products", out var productsValue)
            && productsValue is IEnumerable<object> rows)
        {
            foreach (var row in rows.OfType<IDictionary<string, object>>())
            {
                products.Add(new CatalogProduct(
                    GetString(row, "sku", ""),
                    GetString(row, "kind", "credits"),
                    GetString(row, "displayName", ""),
                    GetLong(row, "amountMinor", 0),
                    GetString(row, "currency", "INR"),
                    GetLong(row, "creditMicros", 0),
                    GetString(row, "razorpayPlanId", null),
                    GetBool(row, "enabled", false)));
            }
        }

        if (products.Count == 0 && _options.AllowBootstrapCatalog)
            products.AddRange(BootstrapProducts());

        return new CatalogSnapshot(
            GetString(data, "version", "bootstrap-2026-08"),
            products,
            pricing,
            GetBool(data, "killSwitch", false),
            GetString(data, "killSwitchMessage", null),
            GetBool(data, "paymentsKillSwitch", false),
            GetString(data, "paymentsKillSwitchMessage", null));
    }

    private CatalogSnapshot BootstrapCatalog()
    {
        if (!_options.AllowBootstrapCatalog)
            throw new InvalidOperationException("The server catalog is not configured.");
        return new CatalogSnapshot(
            "vertex-gemini-2.5-flash-2026-08",
            BootstrapProducts(),
            BootstrapPricing(),
            false,
            null);
    }

    private IReadOnlyList<CatalogProduct> BootstrapProducts()
    {
        var monthlyPlanId = Environment.GetEnvironmentVariable("RAZORPAY_TEST_MONTHLY_PLAN_ID");
        return
        [
            new("credits_test_100", "credits", "100 staging credits", 4_900, "INR", 100_000_000, null, true),
            new("monthly_test", "subscription", "Monthly staging plan", 9_900, "INR", 500_000_000,
                monthlyPlanId, !string.IsNullOrWhiteSpace(monthlyPlanId)),
        ];
    }

    private PricingSnapshot BootstrapPricing() => new(
        "vertex-gemini-2.5-flash-2026-08",
        _options.VertexModel,
        150_000,
        3_500_000,
        37_500,
        3_500_000,
        8_700,
        10_000,
        _clock.UtcNow);

    private static string GetString(IDictionary<string, object> map, string key, string? fallback) =>
        map.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback ?? ""
            : fallback ?? "";

    private static long GetLong(IDictionary<string, object> map, string key, long fallback) =>
        map.TryGetValue(key, out var value) && value is not null
            ? Convert.ToInt64(value)
            : fallback;

    private static bool GetBool(IDictionary<string, object> map, string key, bool fallback) =>
        map.TryGetValue(key, out var value) && value is not null
            ? Convert.ToBoolean(value)
            : fallback;
}
