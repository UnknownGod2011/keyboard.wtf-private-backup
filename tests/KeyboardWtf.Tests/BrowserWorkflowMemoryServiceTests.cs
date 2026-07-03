namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

public sealed class BrowserWorkflowMemoryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-browser-workflow-memory-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public BrowserWorkflowMemoryServiceTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "browser-workflows.json");
    }

    [Fact]
    public void SavesSuccessfulWorkflowAndMatchesSimilarTask()
    {
        var memory = NewMemory();
        var workflow = memory.SaveSuccessfulWorkflow(
            "devpost.com",
            "rank_visible_options",
            "find hackathon with highest prize pool",
            "Find the hackathon with the highest prize pool on Devpost",
            "browser-use + Gemini",
            ["Open listings", "Read visible cards", "Rank prize amounts"],
            ["open start url", "extract visible text"],
            ["hackathon card", "prize"],
            ["read-only"],
            confirmationRequired: false,
            runtimeMs: 33000);

        var match = memory.FindBestMatch("https://devpost.com/hackathons", "find highest prize Devpost hackathon");

        Assert.NotNull(match.Workflow);
        Assert.Equal(workflow.WorkflowId, match.Workflow.WorkflowId);
        Assert.True(match.HighConfidence);
        Assert.True(match.Workflow.AverageRuntimeMs > 0);
    }

    [Fact]
    public void RepeatedSuccessMergesWorkflowInsteadOfClogging()
    {
        var memory = NewMemory();
        memory.SaveSuccessfulWorkflow("amazon.in", "comparison", "compare mx master products", "Compare Logitech MX Master options", "browser-use", ["Search results"], [], [], ["do not add to cart"], false, 57000);
        memory.SaveSuccessfulWorkflow("amazon.in", "comparison", "compare mx master products", "Find best Logitech MX Master mouse", "browser-use", ["Compare price and rating"], [], [], ["do not add to cart"], false, 41000);

        var all = memory.Snapshot();

        Assert.Single(all);
        Assert.Equal(2, all[0].SuccessCount);
        Assert.Contains(all[0].LearnedSteps, step => step.Contains("Compare price", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FailureDecaysConfidenceAndRepairRaisesIt()
    {
        var memory = NewMemory();
        var workflow = memory.SaveSuccessfulWorkflow("example.com", "form_fill", "fill demo form", "Fill the demo form", "browser-use", ["Fill fields"], ["fill input"], [], ["stop before submit"], false, 15000);
        var before = workflow.Confidence;

        memory.RecordFailure(workflow.WorkflowId);
        var failed = memory.Snapshot().Single();
        memory.RecordRepairSuccess(workflow.WorkflowId, ["Use new textarea label"], 12000);
        var repaired = memory.Snapshot().Single();

        Assert.True(failed.Confidence < before);
        Assert.True(repaired.Confidence > failed.Confidence);
        Assert.Contains(repaired.LearnedSteps, step => step.Contains("new textarea", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SensitiveDataIsNotStored()
    {
        var memory = NewMemory();
        var workflow = memory.SaveSuccessfulWorkflow(
            "example.com",
            "form_fill",
            "fill form",
            "Fill form password hunter2",
            "browser-use",
            ["Fill password hunter2", "Use API key AIzaFakeSecretValueThatShouldNeverPersist"],
            ["enter card number 4111111111111111"],
            ["input[name=password]"],
            ["stop before submit"],
            false,
            10000);

        Assert.DoesNotContain(workflow.LearnedSteps, step => step.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(workflow.DeterministicActions, step => step.Contains("4111111111111111", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(workflow.UserIntentExamples, item => item.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PrunesOldLowConfidenceWorkflows()
    {
        var memory = NewMemory();
        var workflow = memory.SaveSuccessfulWorkflow("example.com", "comparison", "compare items", "Compare items", "browser-use", ["Read items"], [], [], ["read-only"], false, 8000);
        for (var i = 0; i < 4; i++)
            memory.RecordFailure(workflow.WorkflowId);
        memory.Prune();

        Assert.Empty(memory.Snapshot());
    }

    [Fact]
    public void ExactSuccessfulInstanceRequiresSameEntity()
    {
        var memory = NewMemory();
        memory.SaveExactSuccessfulInstance(
            "https://www.youtube.com/",
            "youtube_play_oldest_video",
            "play oldest channel video",
            "Play the oldest MrBeast video on YouTube",
            "MrBeast",
            "https://www.youtube.com/watch?v=2XVcLrB7B3Y",
            "Browser Companion Extension",
            ["Open current entity channel"],
            ["youtube_pattern:channel_videos_sort_oldest_open_first_watch"],
            ["youtube_watch_url:MrBeast|https://www.youtube.com/watch?v=2XVcLrB7B3Y"],
            ["read-only"],
            false,
            6000);

        var mrBeast = memory.FindExactSuccessfulInstance("www.youtube.com", "youtube_play_oldest_video", "MrBeast");
        var dudePerfect = memory.FindExactSuccessfulInstance("www.youtube.com", "youtube_play_oldest_video", "Dude Perfect");

        Assert.NotNull(mrBeast.Workflow);
        Assert.Equal(BrowserWorkflowMemoryKind.ExactSuccessfulInstance, mrBeast.Workflow.MemoryKind);
        Assert.Equal("https://www.youtube.com/watch?v=2XVcLrB7B3Y", mrBeast.Workflow.FinalUrl);
        Assert.Null(dudePerfect.Workflow);
    }

    [Fact]
    public void ParameterizedTemplateStripsUnsafeFinalUrlsAndEntityActions()
    {
        var memory = NewMemory();
        var workflow = memory.SaveSuccessfulWorkflow(
            "www.youtube.com",
            "youtube_play_oldest_video",
            "play oldest channel video",
            "play oldest channel video on YouTube",
            "Browser Companion Extension",
            ["Navigate to @MrBeast/videos", "Click Oldest"],
            ["youtube_channel:MrBeast", "navigate:https://www.youtube.com/watch?v=2XVcLrB7B3Y", "youtube_pattern:channel_videos_sort_oldest_open_first_watch"],
            ["watch_url:https://www.youtube.com/watch?v=2XVcLrB7B3Y", "youtube_watch_url:MrBeast|https://www.youtube.com/watch?v=2XVcLrB7B3Y"],
            ["read-only"],
            false,
            8000);

        Assert.Equal(BrowserWorkflowMemoryKind.ParameterizedWorkflowTemplate, workflow.MemoryKind);
        Assert.DoesNotContain(workflow.LearnedSteps, step => step.Contains("@MrBeast", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(workflow.DeterministicActions, action => action.Contains("youtube_channel:", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(workflow.DeterministicActions, action => action.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(workflow.SelectorsOrLocatorHints, hint => hint.Contains("watch_url:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FailureHintDoesNotBecomeBestMatchOrBlockFreshAttempt()
    {
        var memory = NewMemory();
        memory.RecordFailureHint(
            "flipkart.com",
            "ecommerce_add_to_cart",
            "iphone 17 pro",
            "Open Flipkart and search for iPhone 17 Pro and add to cart",
            "https://www.flipkart.com/search?q=iPhone%2017%20Pro",
            "Login popup required account.",
            ["login_visible"]);

        var match = memory.FindBestMatch("flipkart.com", "Open Flipkart and search for iPhone 17 Pro and add to cart", "ecommerce_add_to_cart");
        var all = memory.Snapshot();

        Assert.Null(match.Workflow);
        Assert.Single(all);
        Assert.Equal(BrowserWorkflowMemoryKind.FailureHint, all[0].MemoryKind);
        Assert.True(all[0].FailureExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void ParameterizedEcommerceTemplateMatchesDifferentProductsOnSameDomain()
    {
        var memory = NewMemory();
        var workflow = memory.SaveSuccessfulWorkflow(
            "www.amazon.in",
            "ecommerce_add_to_cart",
            "ecommerce search product, add to cart, and stop at cart",
            "ecommerce search and add selected products to cart after upfront confirmation",
            "Browser Companion Extension",
            ["Use fresh Page Control Graph to find product cards"],
            ["navigate:https://www.amazon.in/s?k={product_query}", "ecommerce_add_to_cart_after_upfront_confirmation"],
            [],
            ["one upfront add-to-cart confirmation", "never checkout/pay/place order"],
            confirmationRequired: true,
            runtimeMs: 42000);

        var match = memory.FindBestMatch(
            "https://www.amazon.in/gp/cart/view.html",
            "Open Amazon and add a mechanical keyboard to cart. Stop at cart.",
            "ecommerce_add_to_cart");

        Assert.NotNull(match.Workflow);
        Assert.Equal(workflow.WorkflowId, match.Workflow.WorkflowId);
        Assert.True(match.HighConfidence);
    }

    [Fact]
    public void LoadQuarantinesCorruptedLocalhostAndMixedTaskMemory()
    {
        var corrupt = new BrowserWorkflowMemory
        {
            Domain = "127.0.0.1",
            TaskType = "youtube_play_oldest_video",
            TaskPattern = "play oldest channel video",
            UserIntentExamples = ["Open the MX Master mouse on Amazon"],
            LearnedSteps = ["Use direct ecommerce search URL"],
            DeterministicActions = ["ecommerce_choose_visible_product_card"],
            SuccessCount = 2,
            Confidence = 0.96,
            StaleAfter = DateTimeOffset.UtcNow.AddDays(30),
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(new[] { corrupt }));

        var memory = NewMemory();
        var loaded = memory.Snapshot().Single();

        Assert.True(loaded.Quarantined);
        Assert.Equal(BrowserWorkflowMemoryKind.LegacyQuarantined, loaded.MemoryKind);
        Assert.Contains("local/mock", loaded.QuarantineReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(memory.FindBestMatch("127.0.0.1", "Play oldest video", "youtube_play_oldest_video").Workflow);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private BrowserWorkflowMemoryService NewMemory()
    {
        var memory = new BrowserWorkflowMemoryService(_path);
        memory.Load();
        return memory;
    }
}
