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
        // One UTF-16 code unit per token is intentionally conservative across
        // languages and structured payloads. Reservation is a budget guard,
        // not a client-visible estimate; unused credit is released at settle.
        var estimatedInput = Math.Max(1, prompt.Length);
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
        if (withSafetyMargin > taskCapCreditMicros)
            throw new InvalidOperationException(
                "The request's conservative maximum cost exceeds its approved task budget.");

        // Reserve the complete caller-approved/server-bounded task budget. This
        // prevents a manipulated low estimate or unusual tokenisation from
        // creating an unreserved provider charge; unused credit is released by
        // the authoritative settlement transaction.
        return taskCapCreditMicros;
    }

    public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateActualCost(
        PricingSnapshot pricing,
        ProviderUsage usage)
    {
        // Vertex promptTokenCount includes cachedContentTokenCount. Charge the
        // cached portion at its discounted rate instead of charging it twice.
        var cachedInput = Math.Clamp(usage.CachedInputTokens, 0, usage.InputTokens);
        var inputAudio = Math.Clamp(usage.InputAudioTokens, 0, usage.InputTokens - cachedInput);
        var regularInput = Math.Max(0, usage.InputTokens - cachedInput - inputAudio);
        var providerMicros = decimal.Ceiling(
            Cost(regularInput, pricing.InputUsdMicrosPerMillionTokens)
            + Cost(usage.OutputTokens, pricing.OutputUsdMicrosPerMillionTokens)
            + Cost(cachedInput, pricing.CachedInputUsdMicrosPerMillionTokens)
            + Cost(inputAudio, pricing.InputAudioUsdMicrosPerMillionTokens)
            + Cost(usage.ReasoningTokens, pricing.ReasoningUsdMicrosPerMillionTokens));
        var providerCost = checked((long)providerMicros);
        return (providerCost, CreditsForProviderCost(pricing, providerCost));
    }

    public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateLiveActualCost(
        PricingSnapshot pricing,
        ProviderUsage usage,
        int estimateSafetyBasisPoints)
    {
        var inputAudio = Math.Clamp(usage.InputAudioTokens, 0, usage.InputTokens);
        var inputImage = Math.Clamp(
            usage.InputImageTokens,
            0,
            Math.Max(0, usage.InputTokens - inputAudio));
        var inputText = Math.Max(0, usage.InputTokens - inputAudio - inputImage);
        var outputAudio = Math.Clamp(usage.OutputAudioTokens, 0, usage.OutputTokens);
        var outputText = Math.Max(0, usage.OutputTokens - outputAudio);
        var providerCost = checked((long)decimal.Ceiling(
            Cost(inputText, pricing.LiveInputTextUsdMicrosPerMillionTokens)
            + Cost(inputAudio, pricing.LiveInputAudioUsdMicrosPerMillionTokens)
            + Cost(inputImage, pricing.LiveInputImageUsdMicrosPerMillionTokens)
            + Cost(outputText, pricing.LiveOutputTextUsdMicrosPerMillionTokens)
            + Cost(outputAudio, pricing.LiveOutputAudioUsdMicrosPerMillionTokens)
            + Cost(usage.ReasoningTokens, pricing.LiveOutputTextUsdMicrosPerMillionTokens)));
        var baseCharge = CreditsForProviderCost(pricing, providerCost);
        var charge = usage.Estimated
            ? checked((long)decimal.Ceiling(
                (decimal)baseCharge * Math.Clamp(estimateSafetyBasisPoints, 10_000, 20_000) / 10_000m))
            : baseCharge;
        return (providerCost, Math.Max(1, charge));
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
                GetLong(pricingMap, "inputUsdMicrosPerMillionTokens", 300_000),
                GetLong(pricingMap, "outputUsdMicrosPerMillionTokens", 2_500_000),
                GetLong(pricingMap, "cachedInputUsdMicrosPerMillionTokens", 30_000),
                GetLong(pricingMap, "reasoningUsdMicrosPerMillionTokens", 2_500_000),
                GetLong(pricingMap, "usdToInrPaise", 8_700),
                GetLong(pricingMap, "creditMicrosPerInrPaise", 10_000),
                _clock.UtcNow,
                GetLong(pricingMap, "inputAudioUsdMicrosPerMillionTokens", 1_000_000),
                GetLong(pricingMap, "liveInputTextUsdMicrosPerMillionTokens", 500_000),
                GetLong(pricingMap, "liveInputAudioUsdMicrosPerMillionTokens", 3_000_000),
                GetLong(pricingMap, "liveInputImageUsdMicrosPerMillionTokens", 3_000_000),
                GetLong(pricingMap, "liveOutputTextUsdMicrosPerMillionTokens", 2_000_000),
                GetLong(pricingMap, "liveOutputAudioUsdMicrosPerMillionTokens", 12_000_000));

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
        300_000,
        2_500_000,
        30_000,
        2_500_000,
        8_700,
        10_000,
        _clock.UtcNow,
        1_000_000,
        500_000,
        3_000_000,
        3_000_000,
        2_000_000,
        12_000_000);

    private static decimal Cost(long tokens, long microsPerMillion) =>
        tokens <= 0 ? 0 : (decimal)tokens * microsPerMillion / 1_000_000m;

    private static long CreditsForProviderCost(PricingSnapshot pricing, long providerCostUsdMicros)
    {
        var inrPaise = decimal.Ceiling(
            providerCostUsdMicros * (decimal)pricing.UsdToInrPaise / 1_000_000m);
        var creditMicros = decimal.Ceiling(inrPaise * pricing.CreditMicrosPerInrPaise);
        return Math.Max(1, checked((long)creditMicros));
    }

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
