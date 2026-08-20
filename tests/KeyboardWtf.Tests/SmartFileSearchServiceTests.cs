namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class SmartFileSearchServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-file-tests", Guid.NewGuid().ToString("N"));
    private readonly LearnedMappingService _mappings;

    public SmartFileSearchServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested", "project screenshots"));
        File.WriteAllText(Path.Combine(_root, "nested", "project screenshots", "keyboard-thumbnail-final.png"), "test");
        File.WriteAllText(Path.Combine(_root, "dangerous-installer.exe"), "test");
        _mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        _mappings.Load();
    }

    [Fact]
    public async Task FindsNestedApproximateImage()
    {
        var service = new SmartFileSearchService(
            _mappings,
            [(_root, "Test", 10)]);
        var result = await service.SearchAsync(
            "keyboard thumbnail image",
            "",
            includeFiles: true,
            includeFolders: false,
            CancellationToken.None);

        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.EndsWith("keyboard-thumbnail-final.png", result.Best.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlocksDangerousFileTypes()
    {
        Assert.True(SmartFileSearchService.IsRiskyFile(Path.Combine(_root, "dangerous-installer.exe")));
        Assert.False(SmartFileSearchService.TryOpenSafe(
            Path.Combine(_root, "dangerous-installer.exe"),
            out var error));
        Assert.Contains("blocked", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LatestScreenshotPrefersMostRecentMatch()
    {
        var older = Path.Combine(_root, "screenshot-old.png");
        var newer = Path.Combine(_root, "screenshot-new.png");
        File.WriteAllText(older, "old");
        File.WriteAllText(newer, "new");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-20));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);
        var service = new SmartFileSearchService(_mappings, [(_root, "Test", 10)]);

        var result = await service.SearchAsync(
            "latest screenshot",
            "",
            includeFiles: true,
            includeFolders: false,
            CancellationToken.None);

        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.Equal(newer, result.Best.Path);
    }

    [Fact]
    public async Task UsesLearnedFolderAlias()
    {
        var folder = Path.Combine(_root, "nested", "project screenshots");
        _mappings.Remember(LearnedMappingKind.Folder, "demo assets", folder, "Project screenshots");
        var service = new SmartFileSearchService(_mappings, [(_root, "Test", 10)]);

        var result = await service.SearchAsync(
            "demo assets",
            "",
            includeFiles: false,
            includeFolders: true,
            CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(folder, result.Best.Path);
    }

    [Fact]
    public async Task UsesFuzzyLearnedFolderAlias()
    {
        var folder = Path.Combine(_root, "nested", "project screenshots");
        _mappings.Remember(LearnedMappingKind.Folder, "hackathon assets", folder, "Project screenshots");
        var service = new SmartFileSearchService(_mappings, [(_root, "Test", 10)]);

        var result = await service.SearchAsync(
            "hackaton asset",
            "",
            includeFiles: false,
            includeFolders: true,
            CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(folder, result.Best.Path);
    }

    [Fact]
    public async Task BreadthFirstSearchFindsMatchOutsideLargeFirstFolder()
    {
        var noisy = Path.Combine(_root, "a-noisy");
        var targetRoot = Path.Combine(_root, "z-target");
        Directory.CreateDirectory(noisy);
        Directory.CreateDirectory(targetRoot);
        for (var i = 0; i < 1200; i++)
            File.WriteAllText(Path.Combine(noisy, $"noise-{i:D4}.txt"), "test");
        var target = Path.Combine(targetRoot, "keyboard-demo-video.mp4");
        File.WriteAllText(target, "test");
        var service = new SmartFileSearchService(
            _mappings,
            [(noisy, "Noisy", 10), (targetRoot, "Target", 9)]);

        var result = await service.SearchAsync(
            "keyboard demo video",
            "",
            includeFiles: true,
            includeFolders: false,
            CancellationToken.None,
            maxDepth: 2,
            maxResults: 5,
            // Exercise the real production search budget. A two-second budget
            // made this filesystem-I/O test depend on shared-runner load even
            // though the breadth-first result was correct.
            timeout: SmartFileSearchService.DefaultTimeout);

        Assert.False(result.TimedOut);
        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.Equal(target, result.Best.Path);
    }

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
