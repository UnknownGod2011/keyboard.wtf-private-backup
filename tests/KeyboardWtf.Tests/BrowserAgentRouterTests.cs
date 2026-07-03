namespace KeyboardWtf.Tests;

using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

public sealed class BrowserAgentRouterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-browser-router-tests",
        Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;

    public BrowserAgentRouterTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new SettingsService(Path.Combine(_root, "settings.json"));
        _settings.Load();
    }

    [Fact]
    public void BroadRealSiteTaskUsesCompanionWhenAvailable()
    {
        var decision = Router().Choose(new BrowserAgentRouteInput
        {
            Task = "Find the hackathon with the highest prize pool on Devpost.",
            StartUrl = "https://devpost.com/hackathons",
            BrowserCompanionAvailable = true,
            BrowserUseAvailable = true,
        });

        Assert.Equal(BrowserAgentBackendKind.BrowserCompanionExtension, decision.SelectedBackend);
        Assert.Equal(BrowserAgentBackendKind.DirectPlaywright, decision.FallbackBackend);
        Assert.Equal(BrowserActionRisk.Safe, decision.Safety.Risk);
    }

    [Fact]
    public void CompanionUnavailableDoesNotUseSandboxFallbackByDefault()
    {
        var decision = Router().Choose(new BrowserAgentRouteInput
        {
            Task = "Find the hackathon with the highest prize pool on Devpost.",
            StartUrl = "https://devpost.com/hackathons",
            BrowserCompanionAvailable = false,
            BrowserUseAvailable = true,
        });

        Assert.Equal(BrowserAgentBackendKind.BrowserCompanionExtension, decision.SelectedBackend);
        Assert.Contains("fallback is disabled", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowserUseIsFallbackOnlyWhenAutomationProfileFallbackIsExplicitlyAllowed()
    {
        _settings.SaveAllowAutomationProfileFallback(true);

        var decision = Router().Choose(new BrowserAgentRouteInput
        {
            Task = "Find the hackathon with the highest prize pool on Devpost.",
            StartUrl = "https://devpost.com/hackathons",
            BrowserCompanionAvailable = false,
            BrowserUseAvailable = true,
        });

        Assert.Equal(BrowserAgentBackendKind.BrowserUseGemini, decision.SelectedBackend);
        Assert.Contains("fallback", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SimpleNavigationUsesDirectPlaywright()
    {
        var decision = Router().Choose(new BrowserAgentRouteInput
        {
            Task = "Open example.com",
            StartUrl = "https://example.com/",
            BrowserUseAvailable = true,
            DirectDeterministicEnough = true,
        });

        Assert.Equal(BrowserAgentBackendKind.DirectPlaywright, decision.SelectedBackend);
    }

    [Fact]
    public void BrowserUseDisabledFallsBackToDirect()
    {
        _settings.SaveBrowserUseEnabled(false);
        _settings.SaveAllowAutomationProfileFallback(true);

        var decision = Router().Choose(new BrowserAgentRouteInput
        {
            Task = "Compare products on Amazon.",
            StartUrl = "https://www.amazon.com/",
            BrowserUseAvailable = true,
        });

        Assert.Equal(BrowserAgentBackendKind.DirectPlaywright, decision.SelectedBackend);
    }

    [Fact]
    public void CartMutationRequiresOneUpfrontConfirmation()
    {
        var decision = Router().Choose(new BrowserAgentRouteInput
        {
            Task = "Open Amazon and add a Logitech mouse to my cart. Stop before checkout.",
            StartUrl = "https://www.amazon.com/",
            BrowserCompanionAvailable = true,
            BrowserUseAvailable = true,
        });

        Assert.Equal(BrowserAgentBackendKind.BrowserCompanionExtension, decision.SelectedBackend);
        Assert.Equal(BrowserActionRisk.ConfirmationRequired, decision.Safety.Risk);
        Assert.True(decision.Safety.ChangesExternalState);
    }

    [Fact]
    public void PaymentAndCheckoutAreManualOnly()
    {
        var decision = Router().Choose(new BrowserAgentRouteInput
        {
            Task = "Checkout and pay for the cart.",
            StartUrl = "https://www.amazon.com/",
            BrowserUseAvailable = true,
        });

        Assert.Equal(BrowserActionRisk.ManualOnly, decision.Safety.Risk);
        Assert.Equal(BrowserAgentBackendKind.DirectPlaywright, decision.SelectedBackend);
    }

    [Fact]
    public void LoggedInWorkflowMarksExtensionBridgeNeeded()
    {
        var decision = Router().Choose(new BrowserAgentRouteInput
        {
            Task = "Search Gmail trash for this email.",
            StartUrl = "https://mail.google.com/",
            BrowserUseAvailable = true,
        });

        Assert.True(decision.RequiresBrowserExtension);
    }

    [Fact]
    public void BrowserOsExternalCanBeSelectedWhenAvailable()
    {
        _settings.SaveSmartBrowserBackend(SmartBrowserBackendMode.BrowserOSExternal);
        _settings.SaveBrowserOSEnabled(true);
        _settings.SaveAllowAutomationProfileFallback(true);

        var decision = Router().Choose(new BrowserAgentRouteInput
        {
            Task = "Search my logged in browser page safely.",
            StartUrl = "https://example.com/",
            BrowserOSAvailable = true,
            BrowserUseAvailable = true,
        });

        Assert.Equal(BrowserAgentBackendKind.BrowserOSExternal, decision.SelectedBackend);
        Assert.Equal(BrowserAgentBackendKind.BrowserUseGemini, decision.FallbackBackend);
    }

    [Fact]
    public void KnownWorkflowRequiresCompanionReplayWithoutSandboxFallback()
    {
        var decision = Router().Choose(new BrowserAgentRouteInput
        {
            Task = "Find highest prize Devpost hackathon again.",
            StartUrl = "https://devpost.com/hackathons",
            BrowserUseAvailable = true,
            KnownWorkflowMatch = true,
        });

        Assert.Equal(BrowserAgentBackendKind.BrowserCompanionExtension, decision.SelectedBackend);
        Assert.Contains("Connect it", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KnownWorkflowUsesCompanionReplayWhenAvailable()
    {
        var decision = Router().Choose(new BrowserAgentRouteInput
        {
            Task = "Play oldest MrBeast video again.",
            StartUrl = "https://www.youtube.com/",
            BrowserCompanionAvailable = true,
            BrowserUseAvailable = true,
            KnownWorkflowMatch = true,
        });

        Assert.Equal(BrowserAgentBackendKind.BrowserCompanionExtension, decision.SelectedBackend);
        Assert.Contains("replay", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private BrowserAgentRouter Router() => new(_settings);
}
