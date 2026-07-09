using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserAutomationMemoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kwtf-browser-memory-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public BrowserAutomationMemoryStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "patterns.json");
    }

    [Fact]
    public void RecordSuccessStoresReusablePatternNotFinalUrlReplay()
    {
        var store = new BrowserAutomationMemoryStore(_path);
        var request = new BrowserAutomationRequest
        {
            TaskText = "Play the oldest Dude Perfect video on YouTube.",
            TargetSite = "youtube.com",
            TaskType = "youtube_oldest_video",
            ActionLevel = BrowserAutomationActionLevel.Navigation,
        };
        var result = new BrowserAutomationResult
        {
            Success = true,
            RuntimeMs = 12000,
            FinalUrl = "https://www.youtube.com/watch?v=example",
            ReceiptSummary = "Watch page reached.",
        };

        store.RecordSuccess(request, result);

        var pattern = Assert.Single(store.Snapshot());
        Assert.Equal("youtube.com", pattern.Domain);
        Assert.Equal("youtube_oldest_video", pattern.TaskType);
        Assert.Contains("Search/open channel", pattern.StepSummary);
        Assert.DoesNotContain("watch?v=example", pattern.StepSummary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("watch?v=example", pattern.Notes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PromptContextOnlyMatchesSameDomainAndTaskType()
    {
        var store = new BrowserAutomationMemoryStore(_path);
        store.RecordSuccess(
            new BrowserAutomationRequest
            {
                TargetSite = "youtube.com",
                TaskType = "youtube_oldest_video",
                ActionLevel = BrowserAutomationActionLevel.Navigation,
            },
            new BrowserAutomationResult { Success = true, RuntimeMs = 1000, ReceiptSummary = "Watch page reached." });

        var match = store.BuildPromptContext(new BrowserAutomationClassification
        {
            TargetSite = "youtube.com",
            TaskType = "youtube_oldest_video",
        });
        var mismatch = store.BuildPromptContext(new BrowserAutomationClassification
        {
            TargetSite = "amazon.in",
            TaskType = "ecommerce_add_to_cart",
        });

        Assert.Contains("youtube.com", match);
        Assert.Equal("", mismatch);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
