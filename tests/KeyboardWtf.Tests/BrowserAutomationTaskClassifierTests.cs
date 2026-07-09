using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserAutomationTaskClassifierTests
{
    private readonly BrowserAutomationTaskClassifier _classifier = new();

    [Fact]
    public void SimpleYoutubeSearchStaysOnExistingTools()
    {
        var result = _classifier.Classify("Search MrBeast on YouTube.");

        Assert.False(result.ShouldUseBrowserAutomation);
        Assert.Contains("Simple", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OldestYoutubeVideoUsesSmartBrowserAutomation()
    {
        var result = _classifier.Classify("Play the oldest Dude Perfect video on YouTube.");

        Assert.True(result.ShouldUseBrowserAutomation);
        Assert.Equal("youtube_oldest_video", result.TaskType);
        Assert.Equal("youtube.com", result.TargetSite);
        Assert.Equal(BrowserAutomationActionLevel.Navigation, result.ActionLevel);
        Assert.Equal("smart", result.Profile);
    }

    [Fact]
    public void AmazonCartTaskRequiresCartMutationLevel()
    {
        var result = _classifier.Classify(
            "Open Amazon India and add MX Master 4 mouse and Stanley bottle to cart. Stop at the cart page.");

        Assert.True(result.ShouldUseBrowserAutomation);
        Assert.Equal("ecommerce_add_to_cart", result.TaskType);
        Assert.Equal("amazon.in", result.TargetSite);
        Assert.Equal(BrowserAutomationActionLevel.CartMutationAfterConfirmation, result.ActionLevel);
    }

    [Fact]
    public void FlipkartCtaInspectionIsReadOnlySmartAutomation()
    {
        var result = _classifier.Classify(
            "Open Flipkart, close popup if it appears, search iPhone 17 Pro, and report CTA state.");

        Assert.True(result.ShouldUseBrowserAutomation);
        Assert.Equal("product_cta_inspection", result.TaskType);
        Assert.Equal("flipkart.com", result.TargetSite);
        Assert.Equal(BrowserAutomationActionLevel.ReadOnly, result.ActionLevel);
    }

    [Fact]
    public void ProtectedFinalActionIsClassifiedButBlocked()
    {
        var result = _classifier.Classify("Open Amazon and checkout with my cart.");

        Assert.True(result.ShouldUseBrowserAutomation);
        Assert.Equal(BrowserAutomationActionLevel.ProtectedFinalActionBlocked, result.ActionLevel);
    }
}
