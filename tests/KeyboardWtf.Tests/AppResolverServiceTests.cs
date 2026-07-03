namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class AppResolverServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-app-tests", Guid.NewGuid().ToString("N"));
    private readonly LearnedMappingService _mappings;

    public AppResolverServiceTests()
    {
        Directory.CreateDirectory(_root);
        _mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        _mappings.Load();
    }

    [Fact]
    public void LearnedAppMappingWinsDeterministically()
    {
        _mappings.Remember(LearnedMappingKind.App, "my assistant", "appx:OpenAI.Codex_2p2nqsd0c76g0!App", "Codex");
        var resolver = new AppResolverService(_mappings);

        var result = resolver.Resolve("my assistant");

        Assert.Equal(AppResolutionStatus.Found, result.Status);
        Assert.Equal("Codex", result.Best.Name);
        Assert.Equal("Learned", result.Best.Source);
    }

    [Fact]
    public void CoreWindowsAppCanBeResolvedFromTrustedSources()
    {
        var resolver = new AppResolverService(_mappings);
        var result = resolver.Resolve("notepad");

        Assert.Equal(AppResolutionStatus.Found, result.Status);
        Assert.True(result.Best.Trusted);
    }

    [Fact]
    public void InstalledMicrosoftExcelResolvesToExcelOnThisMachine()
    {
        var resolver = new AppResolverService(_mappings);

        var result = resolver.Resolve("Microsoft Excel", refresh: true);

        Assert.Equal(AppResolutionStatus.Found, result.Status);
        Assert.Contains("Excel", result.Best.Name, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Best.Trusted);
    }

    [Fact]
    public void CommandScriptsAreNeverTrustedLaunchTargets()
    {
        Assert.False(AppResolverService.IsTrustedLaunchPath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "example.cmd")));
    }

    [Theory]
    [InlineData("spotfy", "Spotify")]
    [InlineData("aple music", "Apple Music")]
    [InlineData("visual code", "Visual Studio Code")]
    [InlineData("xbox game bar", "Game Bar")]
    [InlineData("windows camera", "Camera")]
    public void NaturalNameTyposCanonicalize(string query, string expected)
    {
        Assert.Equal(expected, AppResolverService.CanonicalizeNaturalName(query));
    }

    [Fact]
    public void ShortGenericNameDoesNotBecomeCodex()
    {
        Assert.Equal("code", AppResolverService.CanonicalizeNaturalName("code"));
    }

    [Theory]
    [InlineData("Microsoft Excel", "Excel")]
    [InlineData("Microsoft Word", "Word")]
    [InlineData("Google Chrome", "Google Chrome")]
    [InlineData("Chrome", "Google Chrome")]
    [InlineData("Microsoft Edge", "Microsoft Edge")]
    [InlineData("Edge", "Microsoft Edge")]
    public void VendorAndProductNamesResolveToTheSameTrustedApp(string query, string expected)
    {
        var candidates = new[]
        {
            Candidate("Excel"),
            Candidate("Word"),
            Candidate("Google Chrome"),
            Candidate("Microsoft Edge"),
            Candidate("Microsoft To Do"),
        };
        var resolver = new AppResolverService(_mappings, () => candidates);

        var result = resolver.Resolve(query);

        Assert.Equal(AppResolutionStatus.Found, result.Status);
        Assert.Equal(expected, result.Best.Name);
    }

    [Fact]
    public void VendorPrefixDoesNotForceALowConfidenceGuess()
    {
        var candidates = new[]
        {
            Candidate("Microsoft Visual Studio Code"),
            Candidate("Google Visual Studio Code"),
        };
        var resolver = new AppResolverService(_mappings, () => candidates);

        var result = resolver.Resolve("Visual Studio Code");

        Assert.Equal(AppResolutionStatus.Ambiguous, result.Status);
        Assert.True(result.Candidates.Count >= 2);
    }

    private static AppCandidate Candidate(string name) => new()
    {
        Name = name,
        LaunchTarget = $"appx:test-{name}",
        Source = "Test",
        Trusted = true,
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch { }
    }
}
