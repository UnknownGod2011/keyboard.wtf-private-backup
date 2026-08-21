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
            "desktop-reformat" or "smart-writing-audio" => "selected_text",
            "desktop-health" => "fast_text",
            "live-audio" or "live_audio" => "live_audio",
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
            if (!snapshot.Exists && !_options.AllowBootstrapModelRouting)
                throw new InvalidOperationException(
                    "The production model-routing document is missing; bootstrap routing is disabled.");
            _cached = snapshot.Exists
                ? MergeConfiguredRoutes(snapshot, defaults, !_options.AllowBootstrapModelRouting)
                : defaults;
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
        IReadOnlyDictionary<string, ModelRouteSnapshot> defaults,
        bool requireCompleteConfiguration)
    {
        var data = document.ToDictionary();
        if (requireCompleteConfiguration && !data.ContainsKey("version"))
            throw new InvalidOperationException("Production model routing has no explicit version.");
        var documentVersion = Text(data, "version", "routes-2026-08-01");
        if (!data.TryGetValue("routes", out var value)
            || value is not IDictionary<string, object> configured)
            throw new InvalidOperationException("The server model-routing document has no valid routes map.");

        if (requireCompleteConfiguration)
        {
            var missing = defaults.Keys.Where(key => !configured.ContainsKey(key)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    $"Production model routing is incomplete: {string.Join(", ", missing)}.");
        }

        var merged = requireCompleteConfiguration
            ? new Dictionary<string, ModelRouteSnapshot>(StringComparer.Ordinal)
            : defaults.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var pair in configured)
        {
            if (!defaults.TryGetValue(pair.Key, out var fallback)
                || pair.Value is not IDictionary<string, object> row)
                throw new InvalidOperationException($"Unknown or invalid server model route '{pair.Key}'.");
            merged[pair.Key] = ParseRoute(
                pair.Key,
                documentVersion,
                row,
                fallback,
                requireCompleteConfiguration);
        }
        return merged;
    }

    private ModelRouteSnapshot ParseRoute(
        string name,
        string documentVersion,
        IDictionary<string, object> row,
        ModelRouteSnapshot fallback,
        bool requireCompleteConfiguration)
    {
        if (requireCompleteConfiguration)
            RequireExplicitRouteConfiguration(name, row, fallback.LivePolicy is not null);
        var liveRoute = fallback.LivePolicy is not null;
        var primary = ParseCandidate(
            "primary",
            row,
            fallback.Candidates[0],
            liveRoute,
            requireCompleteConfiguration);
        var candidates = new List<ModelCandidateSnapshot> { primary };
        var configuredFallback = Text(row, "fallbackModel", "");
        if (!string.IsNullOrWhiteSpace(configuredFallback))
        {
            var defaultFallback = fallback.Candidates.Count > 1
                ? fallback.Candidates[1]
                : fallback.Candidates[0] with { Model = configuredFallback };
            candidates.Add(ParseCandidate(
                "fallback",
                row,
                defaultFallback,
                liveRoute,
                requireCompleteConfiguration));
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
            Text(row, "lifecycleNote", fallback.LifecycleNote),
            fallback.LivePolicy is null
                ? null
                : new LiveRoutePolicySnapshot(
                    Integer(row, "maxSessionSeconds", fallback.LivePolicy.MaxSessionSeconds),
                    Number(row, "reservationWindowCreditMicros", fallback.LivePolicy.ReservationWindowCreditMicros),
                    Number(row, "maxSessionCostCreditMicros", fallback.LivePolicy.MaxSessionCostCreditMicros)));
        Validate(route);
        return route;
    }

    private static ModelCandidateSnapshot ParseCandidate(
        string prefix,
        IDictionary<string, object> row,
        ModelCandidateSnapshot fallback,
        bool requireLivePricing,
        bool requireExplicitPricing)
    {
        var model = Text(row, prefix + "Model", fallback.Model);
        var location = Text(row, prefix + "Location", fallback.Location);
        var modelChanged = !string.Equals(model, fallback.Model, StringComparison.Ordinal);
        if (modelChanged || requireExplicitPricing)
            RequireExplicitPricing(prefix, row, requireLivePricing);
        var pricing = fallback.Pricing with
        {
            Version = Text(row, prefix + "PricingVersion", fallback.Pricing.Version),
            Model = model,
            InputUsdMicrosPerMillionTokens = Number(row, prefix + "InputUsdMicrosPerMillionTokens", fallback.Pricing.InputUsdMicrosPerMillionTokens),
            OutputUsdMicrosPerMillionTokens = Number(row, prefix + "OutputUsdMicrosPerMillionTokens", fallback.Pricing.OutputUsdMicrosPerMillionTokens),
            CachedInputUsdMicrosPerMillionTokens = Number(row, prefix + "CachedInputUsdMicrosPerMillionTokens", fallback.Pricing.CachedInputUsdMicrosPerMillionTokens),
            ReasoningUsdMicrosPerMillionTokens = Number(row, prefix + "ReasoningUsdMicrosPerMillionTokens", fallback.Pricing.ReasoningUsdMicrosPerMillionTokens),
            InputAudioUsdMicrosPerMillionTokens = Number(row, prefix + "InputAudioUsdMicrosPerMillionTokens", fallback.Pricing.InputAudioUsdMicrosPerMillionTokens),
            UsdToInrPaise = Number(row, prefix + "UsdToInrPaise", fallback.Pricing.UsdToInrPaise),
            CreditMicrosPerInrPaise = Number(row, prefix + "CreditMicrosPerInrPaise", fallback.Pricing.CreditMicrosPerInrPaise),
            LiveInputTextUsdMicrosPerMillionTokens = Number(row, prefix + "LiveInputTextUsdMicrosPerMillionTokens", fallback.Pricing.LiveInputTextUsdMicrosPerMillionTokens),
            LiveInputAudioUsdMicrosPerMillionTokens = Number(row, prefix + "LiveInputAudioUsdMicrosPerMillionTokens", fallback.Pricing.LiveInputAudioUsdMicrosPerMillionTokens),
            LiveInputImageUsdMicrosPerMillionTokens = Number(row, prefix + "LiveInputImageUsdMicrosPerMillionTokens", fallback.Pricing.LiveInputImageUsdMicrosPerMillionTokens),
            LiveOutputTextUsdMicrosPerMillionTokens = Number(row, prefix + "LiveOutputTextUsdMicrosPerMillionTokens", fallback.Pricing.LiveOutputTextUsdMicrosPerMillionTokens),
            LiveOutputAudioUsdMicrosPerMillionTokens = Number(row, prefix + "LiveOutputAudioUsdMicrosPerMillionTokens", fallback.Pricing.LiveOutputAudioUsdMicrosPerMillionTokens),
        };
        return new ModelCandidateSnapshot(model, location, pricing);
    }

    private static void RequireExplicitRouteConfiguration(
        string routeName,
        IDictionary<string, object> row,
        bool live)
    {
        var required = new List<string>
        {
            "version",
            "provider",
            "enabled",
            "killSwitch",
            "maxInputCharacters",
            "maxOutputTokens",
            "timeoutSeconds",
            "thinkingBudget",
            "canaryBasisPoints",
            "minimumDesktopVersion",
            "lifecycleNote",
            "primaryModel",
            "primaryLocation",
        };
        if (live)
        {
            required.AddRange([
                "maxSessionSeconds",
                "reservationWindowCreditMicros",
                "maxSessionCostCreditMicros",
            ]);
        }
        var missing = required.Where(key => !row.ContainsKey(key)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"Production route '{routeName}' is incomplete: {string.Join(", ", missing)}.");
        if (row.ContainsKey("fallbackModel") && !row.ContainsKey("fallbackLocation"))
            throw new InvalidOperationException(
                $"Production route '{routeName}' has a fallback model without a location.");
    }

    internal static void RequireExplicitPricing(
        string prefix,
        IDictionary<string, object> row,
        bool requireLivePricing = false)
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
            prefix + "UsdToInrPaise",
            prefix + "CreditMicrosPerInrPaise",
        };
        if (requireLivePricing)
        {
            required =
            [
                .. required,
                prefix + "LiveInputTextUsdMicrosPerMillionTokens",
                prefix + "LiveInputAudioUsdMicrosPerMillionTokens",
                prefix + "LiveInputImageUsdMicrosPerMillionTokens",
                prefix + "LiveOutputTextUsdMicrosPerMillionTokens",
                prefix + "LiveOutputAudioUsdMicrosPerMillionTokens",
            ];
        }
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
            "global",
            "vertex-gemini-3.5-flash-lite-standard-global-fx100-2026-08-10",
            300_000,
            2_500_000,
            30_000,
            300_000,
            now);
        var flash = Candidate(
            "gemini-3.5-flash",
            "global",
            "vertex-gemini-3.5-flash-standard-global-fx100-2026-08-10",
            1_500_000,
            9_000_000,
            150_000,
            1_500_000,
            now);
        var olderLite = Candidate(
            "gemini-3.1-flash-lite",
            "global",
            "vertex-gemini-3.1-flash-lite-standard-global-fx100-2026-08-10",
            250_000,
            1_500_000,
            25_000,
            500_000,
            now);
        var live = LiveCandidate(now);

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
            ["live_audio"] = Route("live_audio", [live], 20_000, 1, 100, 0,
                "Gemini Live 2.5 Flash Native Audio GA; retirement 2026-12-13.") with
                {
                    LivePolicy = new LiveRoutePolicySnapshot(
                        _options.LiveSessionMaxSeconds,
                        _options.LiveReservationWindowCreditMicros,
                        _options.LiveSessionCreditMicrosCap),
                },
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
            "routes-2026-08-10",
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

    private static ModelCandidateSnapshot LiveCandidate(DateTimeOffset effectiveAt) =>
        new(
            "gemini-live-2.5-flash-native-audio",
            "us-central1",
            new PricingSnapshot(
                "vertex-gemini-live-2.5-flash-native-audio-standard-fx100-2026-08-10",
                "gemini-live-2.5-flash-native-audio",
                500_000,
                2_000_000,
                500_000,
                2_000_000,
                CatalogService.ConservativeUsdToInrPaise,
                10_000,
                effectiveAt,
                3_000_000,
                500_000,
                3_000_000,
                3_000_000,
                2_000_000,
                12_000_000));

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
        if (route.Name == "live_audio")
        {
            var live = route.LivePolicy
                ?? throw new InvalidOperationException("The Live route has no server-owned session policy.");
            if (live.MaxSessionSeconds is < 30 or > 900
                || live.ReservationWindowCreditMicros <= 0
                || live.MaxSessionCostCreditMicros <= 0
                || live.ReservationWindowCreditMicros > live.MaxSessionCostCreditMicros)
                throw new InvalidOperationException("The Live route has unsafe session limits.");
        }
        else if (route.LivePolicy is not null)
        {
            throw new InvalidOperationException($"Non-Live route '{route.Name}' has a Live session policy.");
        }
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
            if (route.Name == "live_audio"
                && (pricing.LiveInputTextUsdMicrosPerMillionTokens <= 0
                    || pricing.LiveInputAudioUsdMicrosPerMillionTokens <= 0
                    || pricing.LiveInputImageUsdMicrosPerMillionTokens <= 0
                    || pricing.LiveOutputTextUsdMicrosPerMillionTokens <= 0
                    || pricing.LiveOutputAudioUsdMicrosPerMillionTokens <= 0))
                throw new InvalidOperationException("The Live route has incomplete modality pricing.");
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
