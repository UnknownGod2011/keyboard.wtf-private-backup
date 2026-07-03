namespace KeyboardWtf.Tests;

using System.Reflection;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

public sealed class BrowserAgentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-browser-service-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StartTaskCapturesExplicitSelectedContextAndCreatesReceipt()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.SaveAllowAutomationProfileFallback(true);
        settings.SaveAllowAutomationProfileFallback(true);
        using var tasks = new JarvisTaskManager();
        var context = new TaskContextCaptureService(
            _ => Task.FromResult("Project: keyboard.wtf"),
            () => "",
            () => ("Document", "notepad"));
        await using var service = new BrowserAgentService(
            settings,
            tasks,
            context,
            new FakeBrowserBackend(),
            smartBackend: new FakeSmartBrowserBackend { Available = false });

        var result = await service.StartAsync(new BrowserTaskRequest
        {
            Title = "Fill project form",
            OriginalUserCommand = "Use this selected text to fill the project form",
            StartUrl = "https://example.com/",
            UseSelectedText = true,
            ContextExplicitlyRequested = true,
        }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains(
            result.Task.Receipt,
            item => item.Contains("Captured selected text", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(JarvisTaskStatus.Running, result.Task.Status);
    }

    [Fact]
    public async Task AmbiguousElementMovesTaskToWaitingForClarification()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.SaveAllowAutomationProfileFallback(true);
        settings.SaveAllowAutomationProfileFallback(true);
        using var tasks = new JarvisTaskManager();
        await using var service = new BrowserAgentService(
            settings,
            tasks,
            backend: new FakeBrowserBackend { AmbiguousNextAction = true },
            smartBackend: new FakeSmartBrowserBackend { Available = false });
        var started = await service.StartAsync(new BrowserTaskRequest
        {
            Title = "Choose project",
            OriginalUserCommand = "Find my project",
            StartUrl = "https://example.com/",
        }, CancellationToken.None);

        var result = await service.ActAsync(new BrowserAgentActionRequest
        {
            TaskId = started.TaskId,
            Action = BrowserAgentActionKind.Click,
            Role = "button",
            Target = "Project",
        }, CancellationToken.None);

        Assert.True(result.ClarificationRequired);
        Assert.Equal(JarvisTaskStatus.WaitingForClarification, result.Task.Status);
        Assert.Equal(
            JarvisTaskStepStatus.WaitingForClarification,
            result.Task.Steps[3].Status);
    }

    [Fact]
    public async Task BroadTaskUsesBrowserUseSmartBackendWhenAvailable()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.SaveAllowAutomationProfileFallback(true);
        using var tasks = new JarvisTaskManager();
        var direct = new FakeBrowserBackend();
        var smart = new FakeSmartBrowserBackend
        {
            Available = true,
            NextResult = new BrowserSmartTaskResult
            {
                Ok = true,
                Status = BrowserSmartTaskStatus.Completed,
                Message = "Build with Gemini XPRIZE has the highest visible prize pool.",
                Summary = "Build with Gemini XPRIZE has the highest visible prize pool.",
                Backend = "browser-use + Gemini (fake)",
                DurationMs = 1200,
                Steps = 3,
                Progress =
                [
                    new BrowserSmartTaskStep
                    {
                        Title = "Read Devpost listings",
                        Status = "completed",
                        Detail = "Visible cards ranked.",
                    },
                ],
            },
        };
        await using var service = new BrowserAgentService(
            settings,
            tasks,
            backend: direct,
            smartBackend: smart);

        var result = await service.StartAsync(new BrowserTaskRequest
        {
            Title = "Find highest Devpost prize",
            OriginalUserCommand = "Find the hackathon with the highest prize pool on Devpost.",
            StartUrl = "https://devpost.com/hackathons",
        }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, smart.RunCount);
        Assert.Equal(0, direct.OpenCount);
        Assert.Equal(JarvisTaskStatus.Completed, result.Task.Status);
        Assert.Contains("highest visible prize pool", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SmartBackendQuotaFallsBackToDirectPlaywrightForSafeTask()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.SaveAllowAutomationProfileFallback(true);
        using var tasks = new JarvisTaskManager();
        var direct = new FakeBrowserBackend();
        var smart = new FakeSmartBrowserBackend
        {
            Available = true,
            NextResult = new BrowserSmartTaskResult
            {
                Ok = false,
                Status = BrowserSmartTaskStatus.QuotaLimited,
                Message = "Browser-use backend is temporarily quota-limited by Gemini.",
                Backend = "browser-use + Gemini (fake)",
                CanFallbackToDirect = true,
            },
        };
        await using var service = new BrowserAgentService(
            settings,
            tasks,
            backend: direct,
            smartBackend: smart);

        var result = await service.StartAsync(new BrowserTaskRequest
        {
            Title = "Compare products",
            OriginalUserCommand = "Compare visible products on an ecommerce page.",
            StartUrl = "https://example.com/store",
        }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, smart.RunCount);
        Assert.Equal(1, direct.OpenCount);
        Assert.Equal(JarvisTaskStatus.Running, result.Task.Status);
        Assert.Contains(
            result.Task.Receipt,
            item => item.Contains("Direct Playwright fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RiskySmartCartTaskAsksOneUpfrontConfirmation()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        using var tasks = new JarvisTaskManager();
        var smart = new FakeSmartBrowserBackend { Available = true };
        var companion = new BrowserCompanionBridgeService();
        companion.Register("session-1", "0.1.0");
        await using var service = new BrowserAgentService(
            settings,
            tasks,
            backend: new FakeBrowserBackend(),
            smartBackend: smart,
            companionBridge: companion);

        var result = await service.StartAsync(new BrowserTaskRequest
        {
            Title = "Add items to cart",
            OriginalUserCommand = "Open Amazon and add a Logitech mouse to my cart. Stop before checkout.",
            StartUrl = "https://www.amazon.com/",
        }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(result.ConfirmationRequired);
        Assert.Equal(0, smart.RunCount);
        Assert.Equal(JarvisTaskStatus.WaitingForConfirmation, result.Task.Status);
    }

    [Fact]
    public async Task BroadTaskWithoutCompanionDoesNotLaunchAutomationProfileByDefault()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        using var tasks = new JarvisTaskManager();
        var direct = new FakeBrowserBackend();
        var smart = new FakeSmartBrowserBackend { Available = true };
        await using var service = new BrowserAgentService(
            settings,
            tasks,
            backend: direct,
            smartBackend: smart);

        var result = await service.StartAsync(new BrowserTaskRequest
        {
            Title = "Find Devpost prize",
            OriginalUserCommand = "Find the hackathon with the highest prize pool on Devpost.",
        }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, smart.RunCount);
        Assert.Equal(0, direct.OpenCount);
        Assert.Contains("Browser Companion Extension is not connected", result.Message);
    }

    [Fact]
    public void RequestNormalizationInfersFlipkartStartUrl()
    {
        var method = typeof(BrowserAgentService).GetMethod(
            "InferStartUrl",
            BindingFlags.NonPublic | BindingFlags.Static);

        var url = Assert.IsType<string>(method!.Invoke(null, ["Open Flipkart and search for iPhone 17 Pro and add to cart."]));

        Assert.Equal("https://www.flipkart.com", url);
    }

    [Fact]
    public void EcommerceProductExtractionDoesNotTreatCartTailAsProduct()
    {
        var method = typeof(BrowserAgentService).GetMethod(
            "ExtractEcommerceProducts",
            BindingFlags.NonPublic | BindingFlags.Static);

        var products = Assert.IsType<string[]>(method!.Invoke(
            null,
            ["Open Flipkart and search for iPhone 17 Pro and add to cart. Stop at the cart page."]));

        Assert.Equal(["iPhone 17 Pro"], products);
    }

    [Fact]
    public void EcommerceProductTokensKeepShortVersionNumbers()
    {
        var method = typeof(BrowserAgentService).GetMethod(
            "ProductTokens",
            BindingFlags.NonPublic | BindingFlags.Static);

        var tokens = Assert.IsAssignableFrom<IEnumerable<string>>(method!.Invoke(null, ["MX Master 4 mouse"]));

        Assert.Contains("4", tokens);
        Assert.Contains("master", tokens);
    }

    [Fact]
    public void EcommerceVersionDigitMustAppearNearQueryWords()
    {
        var method = typeof(BrowserAgentService).GetMethod(
            "DigitTokenHasQueryContext",
            BindingFlags.NonPublic | BindingFlags.Static);

        var queryTokens = new[] { "mx", "master", "4", "mouse" };

        var wrongVariant = Assert.IsType<bool>(method!.Invoke(
            null,
            [new[] { "logitech", "mx", "master", "2s", "wireless", "mouse", "4", "4", "stars" }, queryTokens, "4"]));
        var rightVariant = Assert.IsType<bool>(method.Invoke(
            null,
            [new[] { "logitech", "mx", "master", "4", "wireless", "mouse" }, queryTokens, "4"]));

        Assert.False(wrongVariant);
        Assert.True(rightVariant);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class FakeBrowserBackend : IBrowserAutomationBackend
    {
        public bool AmbiguousNextAction { get; init; }
        public int OpenCount { get; private set; }
        public BrowserBackendStatus Status => new()
        {
            Initialized = true,
            Available = true,
            BrowserChannel = "fake",
        };

        public Task<BrowserPageState> OpenAsync(string taskId, string url, CancellationToken token)
        {
            OpenCount++;
            return Task.FromResult(Page(url));
        }

        public Task<BrowserPageState> ObserveAsync(string taskId, CancellationToken token) =>
            Task.FromResult(Page("https://example.com/"));

        public Task<BrowserBackendActionResult> ExecuteAsync(
            BrowserAgentActionRequest request,
            CancellationToken token)
        {
            if (AmbiguousNextAction)
            {
                return Task.FromResult(new BrowserBackendActionResult
                {
                    Ok = false,
                    Ambiguous = true,
                    MatchCount = 2,
                    Message = "Two projects matched.",
                    Candidates = ["keyboard.wtf", "keyboard.wtf demo"],
                    State = Page("https://example.com/"),
                });
            }
            return Task.FromResult(new BrowserBackendActionResult
            {
                Ok = true,
                Verified = true,
                Message = "Action completed.",
                State = Page("https://example.com/"),
            });
        }

        public Task CloseTaskAsync(string taskId) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static BrowserPageState Page(string url) => new()
        {
            Title = "Example Domain",
            Url = url,
            VisibleText = "Example Domain",
            Loaded = true,
            InteractiveElements = [new BrowserElementSummary { Role = "link", Name = "More information" }],
        };
    }

    private sealed class FakeSmartBrowserBackend : ISmartBrowserTaskBackend
    {
        public bool Available { get; init; }
        public int RunCount { get; private set; }
        public BrowserSmartTaskResult NextResult { get; init; } = new()
        {
            Ok = true,
            Status = BrowserSmartTaskStatus.Completed,
            Message = "Smart task completed.",
            Summary = "Smart task completed.",
            Backend = "browser-use + Gemini (fake)",
            DurationMs = 100,
            Steps = 1,
        };

        public BrowserBackendStatus Status => new()
        {
            Initialized = true,
            Available = Available,
            BrowserChannel = "browser-use + Gemini (fake)",
            ProfilePath = "fake",
            LastError = Available ? "" : "Browser-use backend needs Gemini API key configured.",
        };

        public Task<BrowserSmartTaskResult> RunAsync(
            BrowserSmartTaskRequest request,
            CancellationToken token)
        {
            RunCount++;
            return Task.FromResult(NextResult);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
