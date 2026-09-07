namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class BrowserLauncherServiceTests
{
    [Theory]
    [InlineData("Google Chrome", "chrome")]
    [InlineData("Microsoft Edge", "edge")]
    [InlineData("Brave browser", "brave")]
    [InlineData("default browser", "")]
    public void BrowserNamesNormalize(string input, string expected)
    {
        Assert.Equal(expected, BrowserLauncherService.NormalizeBrowser(input));
    }

    [Fact]
    public void SearchUrlsAreServiceSpecific()
    {
        Assert.Contains("youtube.com", BrowserLauncherService.BuildSearchUrl("MrBeast", "youtube"));
        Assert.Contains("google.com", BrowserLauncherService.BuildSearchUrl("keyboard wtf", "google"));
    }

    [Fact]
    public void UnsafeSchemesAreRejected()
    {
        Assert.False(BrowserLauncherService.TryNormalizeUrl("file:///C:/Windows", out _));
    }

    [Theory]
    [InlineData("https://example.com/portal", "https://example.com/portal")]
    [InlineData("http://localhost:8080/test", "http://localhost:8080/test")]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("example.com/path?q=1", "https://example.com/path?q=1")]
    public void WebDestinationsUseOneSafeCanonicalForm(string input, string expected)
    {
        Assert.True(BrowserLauncherService.TryNormalizeUrl(input, out var uri));
        Assert.Equal(expected, uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("not a URL")]
    [InlineData("localhost")]
    [InlineData("user@example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("C:\\Windows\\file.example")]
    public void AmbiguousOrUnsafeBareDestinationsAreRejected(string input)
    {
        Assert.False(BrowserLauncherService.TryNormalizeUrl(input, out _));
    }
}
