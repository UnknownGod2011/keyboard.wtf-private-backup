using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

/// <summary>
/// Resolves desktop capabilities to server-owned Vertex routes.  The desktop
/// never supplies a provider model or price.  A small Firestore document can
/// canary, stop, or replace a route without requiring a desktop update.
/// </summary>
public sealed class ModelRoutingService : IModelRoutingService
{
    private const string RoutingDocument = "config/modelRouting";
    private readonly FirestoreDb _db;
    private readonly PaidSystemOptions _options;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, ModelRouteSnapshot>? _cached;
    private DateTimeOffset _cachedUntil;

    public ModelRoutingService(
        FirestoreDb db,
        IOptions<PaidSystemOptions> options,
        IClock clock)
    {
        _db = db;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<ModelRouteSnapshot> ResolveAsync(
        string? taskKind,
        string routingKey,
        CancellationToken cancellationToken)
    {
        var routeName = ResolveRouteName(taskKind);
        var routes = await GetRoutesAsync(cancellationToken);
        if (!routes.TryGetValue(routeName, out var route))
            throw new InvalidOperationException($"The server model route '{routeName}' is not configured.");
        if (!route.Enabled || route.KillSwitch)
            throw new InvalidOperationException($"The server model route '{routeName}' is temporarily unavailable.");
        if (route.Candidates.Count == 0)
            throw new InvalidOperationException($"The server model route '{routeName}' has no candidates.");
        return ApplyCanary(route, routingKey);
    }

    internal static ModelRouteSnapshot ApplyCanary(
        ModelRouteSnapshot route,
        string routingKey)
    {
        if (route.CanaryBasisPoints <= 0 || route.Candidates.Count < 2)
            return route;
        if (string.IsNullOrWhiteSpace(routingKey))
            throw new ArgumentException("A stable server routing key is required for canary selection.");
        var bucket = Convert.ToUInt32(Crypto.Sha256(routingKey)[..8], 16) % 10_000;
        if (bucket >= route.CanaryBasisPoints)
            return route;

        // The configured fallback candidate is the canary. Canary users try it
        // first and fall back to the stable primary; all other users retain the
        // stable-primary then fallback order.
        var candidates = route.Candidates.ToArray();
        (candidates[0], candidates[1]) = (candidates[1], candidates[0]);
        return route with { Candidates = candidates };
    }

    internal static string ResolveRouteName(string? taskKind) =>
        (taskKind ?? "").Trim().ToLowerInvariant() switch
        {
            "browser-plan" or "browser-vision-recovery" => "browser_planner",
            "screen-guidance" => "screen_reasoning",
            "desktop-structured" => "agent_planner",
            "desktop-reformat" => "selected_text",
            "desktop-health" => "fast_text",
            _ => "fast_text",
        };

    private async Task<IReadOnlyDictionary<string, ModelRouteSnapshot>> GetRoutesAsync(
        CancellationToken cancellationToken)
    {
        if (_cached is not null && _cachedUntil > _clock.UtcNow)
            return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is not null && _cachedUntil > _clock.UtcNow)
                return _cached;

            var defaults = BootstrapRoutes();
            var snapshot = await _db.Document(RoutingDocument).GetSnapshotAsync(cancellationToken);
            _cached = snapshot.Exists ? MergeConfiguredRoutes(snapshot, defaults) : defaults;
            _cachedUntil = _clock.UtcNow.AddSeconds(_options.ModelRoutingCacheSeconds);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyDictionary<string, ModelRouteSnapshot> MergeConfiguredRoutes(
        DocumentSnapshot document,
        IReadOnlyDictionary<string, ModelRouteSnapshot> defaults)
    {
        var data = document.ToDictionary();
        var documentVersion = Text(data, "version", "routes-2026-08-01");
        if (!data.TryGetValue("routes", out var value)
            || value is not IDictionary<string, object> configured)
            throw new InvalidOperationException("The server model-routing document has no valid routes map.");

        var merged = defaults.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in configured)
        {
            if (!defaults.TryGetValue(pair.Key, out var fallback)
                || pair.Value is not IDictionary<string, object> row)
                throw new InvalidOperationException($"Unknown or invalid server model route '{pair.Key}'.");
            merged[pair.Key] = ParseRoute(pair.Key, documentVersion, row, fallback);
        }
        return merged;
    }

    private ModelRouteSnapshot ParseRoute(
        string name,
        string documentVersion,
        IDictionary<string, object> row,
        ModelRouteSnapshot fallback)
    {
        var primary = ParseCandidate("primary", row, fallback.Candidates[0]);
        var candidates = new List<ModelCandidateSnapshot> { primary };
        var configuredFallback = Text(row, "fallbackModel", "");
        if (!string.IsNullOrWhiteSpace(configuredFallback))
        {
            var defaultFallback = fallback.Candidates.Count > 1
                ? fallback.Candidates[1]
                : fallback.Candidates[0] with { Model = configuredFallback };
            candidates.Add(ParseCandidate("fallback", row, defaultFallback));
        }

        var route = new ModelRouteSnapshot(
            name,
            Text(row, "version", documentVersion),
            Text(row, "provider", fallback.Provider),
            candidates,
            Boolean(row, "enabled", fallback.Enabled),
            Boolean(row, "killSwitch", fallback.KillSwitch),
            Integer(row, "maxInputCharacters", fallback.MaxInputCharacters),
            Integer(row, "maxOutputTokens", fallback.MaxOutputTokens),
            Integer(row, "timeoutSeconds", fallback.TimeoutSeconds),
            Integer(row, "thinkingBudget", fallback.ThinkingBudget),
            Integer(row, "canaryBasisPoints", fallback.CanaryBasisPoints),
            Text(row, "minimumDesktopVersion", fallback.MinimumDesktopVersion),
            Text(row, "lifecycleNote", fallback.LifecycleNote));
        Validate(route);
        return route;
    }

    private static ModelCandidateSnapshot ParseCandidate(
        string prefix,
        IDictionary<string, object> row,
        ModelCandidateSnapshot fallback)
    {
        var model = Text(row, prefix + "Model", fallback.Model);
        var location = Text(row, prefix + "Location", fallback.Location);
        var modelChanged = !string.Equals(model, fallback.Model, StringComparison.Ordinal);
        if (modelChanged)
            RequireExplicitPricing(prefix, row);
        var pricing = fallback.Pricing with
        {
            Version = Text(row, prefix + "PricingVersion", fallback.Pricing.Version),
            Model = model,
            InputUsdMicrosPerMillionTokens = Number(row, prefix + "InputUsdMicrosPerMillionTokens", fallback.Pricing.InputUsdMicrosPerMillionTokens),
            OutputUsdMicrosPerMillionTokens = Number(row, prefix + "OutputUsdMicrosPerMillionTokens", fallback.Pricing.OutputUsdMicrosPerMillionTokens),
            CachedInputUsdMicrosPerMillionTokens = Number(row, prefix + "CachedInputUsdMicrosPerMillionTokens", fallback.Pricing.CachedInputUsdMicrosPerMillionTokens),
            ReasoningUsdMicrosPerMillionTokens = Number(row, prefix + "ReasoningUsdMicrosPerMillionTokens", fallback.Pricing.ReasoningUsdMicrosPerMillionTokens),
            InputAudioUsdMicrosPerMillionTokens = Number(row, prefix + "InputAudioUsdMicrosPerMillionTokens", fallback.Pricing.InputAudioUsdMicrosPerMillionTokens),
        };
        return new ModelCandidateSnapshot(model, location, pricing);
    }

    internal static void RequireExplicitPricing(
        string prefix,
        IDictionary<string, object> row)
    {
        // A newly configured model must never inherit another model's price.
        // Under-reserving provider work is a financial correctness failure, so
        // routing fails closed until an explicit immutable pricing version and
        // every billable rate are supplied by the server operator.
        var required = new[]
        {
            prefix + "PricingVersion",
            prefix + "InputUsdMicrosPerMillionTokens",
            prefix + "OutputUsdMicrosPerMillionTokens",
            prefix + "CachedInputUsdMicrosPerMillionTokens",
            prefix + "ReasoningUsdMicrosPerMillionTokens",
            prefix + "InputAudioUsdMicrosPerMillionTokens",
        };
        var missing = required.Where(key => !row.ContainsKey(key)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"A changed {prefix} model requires explicit pricing: {string.Join(", ", missing)}.");
    }

    private IReadOnlyDictionary<string, ModelRouteSnapshot> BootstrapRoutes()
    {
        var now = _clock.UtcNow;
        var flashLite = Candidate(
            "gemini-3.5-flash-lite",
            "asia-south1",
            "vertex-gemini-3.5-flash-lite-standard-nonglobal-fx100-2026-08-09",
            330_000,
            2_750_000,
            33_000,
            330_000,
            now);
        var flash = Candidate(
            "gemini-3.5-flash",
            "asia-south1",
            "vertex-gemini-3.5-flash-standard-nonglobal-fx100-2026-08-09",
            1_650_000,
            9_900_000,
            165_000,
            1_650_000,
            now);
        var olderLite = Candidate(
            "gemini-3.1-flash-lite",
            "asia-south1",
            "vertex-gemini-3.1-flash-lite-standard-nonglobal-fx100-2026-08-09",
            275_000,
            1_650_000,
            27_500,
            550_000,
            now);

        return new Dictionary<string, ModelRouteSnapshot>(StringComparer.Ordinal)
        {
            ["fast_text"] = Route("fast_text", [flashLite, olderLite], 200_000, 2_048, 60, 0,
                "Gemini 3.5 Flash-Lite GA; retirement 2027-07-21 or later."),
            ["selected_text"] = Route("selected_text", [flashLite, olderLite], 200_000, 2_048, 60, 0,
                "Gemini 3.5 Flash-Lite GA; retirement 2027-07-21 or later."),
            ["agent_planner"] = Route("agent_planner", [flash, flashLite], 200_000, 4_096, 90, 1_024,
                "Gemini 3.5 Flash GA; retirement 2027-05-19 or later."),
            ["browser_planner"] = Route("browser_planner", [flashLite, flash], 200_000, 2_048, 75, 512,
                "Fast GA planner with a stronger compatible GA fallback."),
            ["screen_reasoning"] = Route("screen_reasoning", [flash, flashLite], 200_000, 2_048, 90, 1_024,
                "Gemini 3.5 Flash GA multimodal reasoning route."),
        };
    }

    private static ModelRouteSnapshot Route(
        string name,
        IReadOnlyList<ModelCandidateSnapshot> candidates,
        int maxInputCharacters,
        int maxOutputTokens,
        int timeoutSeconds,
        int thinkingBudget,
        string lifecycle) =>
        new(
            name,
            "routes-2026-08-08",
            "vertex",
            candidates,
            true,
            false,
            maxInputCharacters,
            maxOutputTokens,
            timeoutSeconds,
            thinkingBudget,
            0,
            "1.3.0",
            lifecycle);

    private static ModelCandidateSnapshot Candidate(
        string model,
        string location,
        string pricingVersion,
        long input,
        long output,
        long cachedInput,
        long audioInput,
        DateTimeOffset effectiveAt) =>
        new(
            model,
            location,
            new PricingSnapshot(
                pricingVersion,
                model,
                input,
                output,
                cachedInput,
                output,
                CatalogService.ConservativeUsdToInrPaise,
                10_000,
                effectiveAt,
                audioInput));

    private static void Validate(ModelRouteSnapshot route)
    {
        if (!string.Equals(route.Provider, "vertex", StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported provider on route '{route.Name}'.");
        if (route.MaxInputCharacters is < 1 or > 200_000
            || route.MaxOutputTokens is < 1 or > 8_192
            || route.TimeoutSeconds is < 5 or > 100
            || route.ThinkingBudget is < 0 or > 8_192
            || route.CanaryBasisPoints is < 0 or > 10_000)
            throw new InvalidOperationException($"Unsafe limits on server model route '{route.Name}'.");
        foreach (var candidate in route.Candidates)
        {
            if (!IsSafeIdentifier(candidate.Model) || !IsSafeIdentifier(candidate.Location))
                throw new InvalidOperationException($"Unsafe model identity on route '{route.Name}'.");
            var pricing = candidate.Pricing;
            if (pricing.InputUsdMicrosPerMillionTokens <= 0
                || pricing.OutputUsdMicrosPerMillionTokens <= 0
                || pricing.CachedInputUsdMicrosPerMillionTokens <= 0
                || pricing.ReasoningUsdMicrosPerMillionTokens <= 0)
                throw new InvalidOperationException($"Invalid pricing on server model route '{route.Name}'.");
        }
    }

    private static bool IsSafeIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 100
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string Text(IDictionary<string, object> map, string key, string fallback) =>
        map.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback
            : fallback;

    private static long Number(IDictionary<string, object> map, string key, long fallback) =>
        map.TryGetValue(key, out var value) && value is not null
            ? Convert.ToInt64(value)
            : fallback;

    private static int Integer(IDictionary<string, object> map, string key, int fallback) =>
        checked((int)Number(map, key, fallback));

    private static bool Boolean(IDictionary<string, object> map, string key, bool fallback) =>
        map.TryGetValue(key, out var value) && value is not null
            ? Convert.ToBoolean(value)
            : fallback;
}
