namespace KeyboardWtf.Services;

using KeyboardWtf.Models;

public sealed class BrowserAgentRouter
{
    private static readonly string[] BroadReasoningTerms =
    [
        "find", "search", "compare", "rank", "best", "cheapest", "highest", "lowest",
        "fill this form", "fill the form", "bookmyshow", "devpost", "amazon", "cart",
        "showtimes", "flights", "hotel", "hotels", "products", "prize pool",
    ];

    private static readonly string[] LoggedInTerms =
    [
        "gmail", "inbox", "trash", "bin", "youtube studio", "my account", "my cart",
        "portfolio", "logged in", "login", "log in",
    ];

    private readonly SettingsService _settings;

    public BrowserAgentRouter(SettingsService settings)
    {
        _settings = settings;
    }

    public BrowserAgentRouteDecision Choose(BrowserAgentRouteInput input)
    {
        input ??= new BrowserAgentRouteInput();
        var current = _settings.Current;
        var safety = BrowserAgentSafetyPolicy.EvaluateTaskIntent(
            input.Task,
            input.RiskyActionConfirmed);
        if (safety.Risk is BrowserActionRisk.Blocked or BrowserActionRisk.ManualOnly)
        {
            return new BrowserAgentRouteDecision
            {
                SelectedBackend = BrowserAgentBackendKind.DirectPlaywright,
                FallbackBackend = BrowserAgentBackendKind.DirectPlaywright,
                Safety = safety,
                RequiresBrowserExtension = ContainsAny(input.Task, LoggedInTerms),
                Reason = safety.Reason,
            };
        }

        var mode = current.SmartBrowserBackend;
        var companionUsable = current.BrowserCompanionEnabled && input.BrowserCompanionAvailable;
        var allowAutomationProfileFallback = current.AllowAutomationProfileFallback || input.AutomationProfileFallbackAllowed;
        var browserUseUsable = allowAutomationProfileFallback && current.BrowserUseEnabled && input.BrowserUseAvailable;
        var browserOsUsable = current.BrowserOSEnabled && input.BrowserOSAvailable;
        var loggedIn = input.RequiresLoggedInSession || ContainsAny(input.Task, LoggedInTerms);
        var simpleAction = input.Action is BrowserAgentActionKind.Navigate
            or BrowserAgentActionKind.Wait
            or BrowserAgentActionKind.Verify
            || IsSimpleOpen(input.Task);
        var deterministic = input.DirectDeterministicEnough
            || input.Action is BrowserAgentActionKind.Extract
            || simpleAction;
        var broad = ContainsAny(input.Task, BroadReasoningTerms) && !simpleAction;

        if (mode == SmartBrowserBackendMode.DirectPlaywright)
        {
            return Direct(safety, loggedIn, "Settings force Direct Playwright.");
        }
        if (mode == SmartBrowserBackendMode.BrowserCompanionExtension)
        {
            return companionUsable
                ? Companion(safety, loggedIn, "Settings force Browser Companion Extension.")
                : Companion(safety, loggedIn, "Browser Companion Extension is selected but unavailable. Connect the extension; no automation browser fallback will be launched.");
        }
        if (mode == SmartBrowserBackendMode.StagehandExperimental)
        {
            return new BrowserAgentRouteDecision
            {
                SelectedBackend = BrowserAgentBackendKind.StagehandExperimental,
                FallbackBackend = BrowserAgentBackendKind.DirectPlaywright,
                Safety = safety,
                RequiresBrowserExtension = loggedIn,
                Reason = "Stagehand is selected experimentally; Direct Playwright remains fallback.",
            };
        }
        if (mode == SmartBrowserBackendMode.BrowserOSExternal)
        {
            return browserOsUsable
                ? new BrowserAgentRouteDecision
                {
                    SelectedBackend = BrowserAgentBackendKind.BrowserOSExternal,
                    FallbackBackend = browserUseUsable ? BrowserAgentBackendKind.BrowserUseGemini : BrowserAgentBackendKind.DirectPlaywright,
                    Safety = safety,
                    RequiresBrowserExtension = loggedIn,
                    Reason = "Settings force BrowserOS external backend; it must run outside keyboard.wtf because BrowserOS is AGPL-3.0.",
                }
                : Direct(safety, loggedIn, "BrowserOS is selected but unavailable, so Direct Playwright is fallback.");
        }
        if (mode == SmartBrowserBackendMode.BrowserUseGemini)
        {
            return current.BrowserUseEnabled && input.BrowserUseAvailable
                ? BrowserUse(safety, loggedIn, "Settings force browser-use + Gemini.")
                : Direct(safety, loggedIn, "browser-use is selected but unavailable, so Direct Playwright is fallback.");
        }

        if (input.KnownWorkflowMatch)
            return companionUsable
                ? Companion(safety, loggedIn, "A high-confidence learned workflow is available; replay with the Browser Companion Extension.")
                : Companion(safety, loggedIn, "A high-confidence learned workflow is available, but the Browser Companion Extension is not connected. Connect it to replay without launching a sandbox browser.");
        if (companionUsable && (broad || !deterministic))
            return Companion(safety, loggedIn, "Browser Companion Extension is the main fast browser executor for graph-based tasks.");
        if (current.BrowserCompanionEnabled && !companionUsable && !allowAutomationProfileFallback && (broad || loggedIn || !deterministic))
            return Companion(safety, loggedIn, "Browser Companion Extension is unavailable. Connect it to use the active browser; automation profile fallback is disabled.");
        if (browserOsUsable && broad && ContainsAny(input.Task, "active browser", "logged in", "gmail", "youtube studio"))
        {
            return new BrowserAgentRouteDecision
            {
                SelectedBackend = BrowserAgentBackendKind.BrowserOSExternal,
                FallbackBackend = browserUseUsable ? BrowserAgentBackendKind.BrowserUseGemini : BrowserAgentBackendKind.DirectPlaywright,
                Safety = safety,
                RequiresBrowserExtension = loggedIn,
                Reason = "BrowserOS is available for browser-native external control, but remains experimental and license-isolated.",
            };
        }
        if (browserUseUsable && broad)
            return BrowserUse(safety, loggedIn, "Browser Companion Extension is unavailable; using experimental browser-use + Gemini fallback.");
        if (browserUseUsable && !deterministic)
            return BrowserUse(safety, loggedIn, "Browser Companion Extension is unavailable and task is not deterministic enough for direct DOM actions.");
        return Direct(safety, loggedIn, deterministic
            ? "Direct Playwright is enough for this deterministic action."
            : "browser-use is disabled or unavailable.");
    }

    private static BrowserAgentRouteDecision BrowserUse(
        BrowserSafetyDecision safety,
        bool loggedIn,
        string reason) => new()
    {
        SelectedBackend = BrowserAgentBackendKind.BrowserUseGemini,
        FallbackBackend = BrowserAgentBackendKind.DirectPlaywright,
        Safety = safety,
        RequiresBrowserExtension = loggedIn,
        Reason = loggedIn
            ? reason + " Active logged-in browser control still needs a browser extension bridge."
            : reason,
    };

    private static BrowserAgentRouteDecision Companion(
        BrowserSafetyDecision safety,
        bool loggedIn,
        string reason) => new()
    {
        SelectedBackend = BrowserAgentBackendKind.BrowserCompanionExtension,
        FallbackBackend = BrowserAgentBackendKind.DirectPlaywright,
        Safety = safety,
        RequiresBrowserExtension = loggedIn,
        Reason = loggedIn
            ? reason + " The extension uses the active browser tab but still needs manual login/OTP/CAPTCHA handling."
            : reason,
    };

    private static BrowserAgentRouteDecision Direct(
        BrowserSafetyDecision safety,
        bool loggedIn,
        string reason) => new()
    {
        SelectedBackend = BrowserAgentBackendKind.DirectPlaywright,
        FallbackBackend = BrowserAgentBackendKind.DirectPlaywright,
        Safety = safety,
        RequiresBrowserExtension = loggedIn,
        Reason = loggedIn
            ? reason + " Active logged-in browser control still needs a browser extension bridge."
            : reason,
    };

    private static bool IsSimpleOpen(string task)
    {
        var normalized = FuzzyMatcher.Normalize(task);
        return normalized.StartsWith("open ", StringComparison.Ordinal)
            && !ContainsAny(normalized, BroadReasoningTerms.Where(term => term is not "search").ToArray());
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        var normalized = FuzzyMatcher.Normalize(value ?? "");
        return terms.Any(term => normalized.Contains(FuzzyMatcher.Normalize(term), StringComparison.Ordinal));
    }
}
