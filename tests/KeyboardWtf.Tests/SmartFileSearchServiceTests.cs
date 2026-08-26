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
    public async Task Natural_desktop_file_request_uses_the_name_instead_of_location_words()
    {
        var target = Path.Combine(_root, "Tanush_oppo.txt");
        File.WriteAllText(target, "test");
        var service = new SmartFileSearchService(_mappings, [(_root, "Desktop", 10)]);

        var result = await service.SearchAsync(
            "Find the file on my Desktop called tanush-oppo",
            "",
            includeFiles: true,
            includeFolders: true,
            CancellationToken.None);

        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.Equal(target, result.Best.Path);
        Assert.Equal("tanush oppo", SmartFileSearchService.SearchNeedle(
            "Find the file on my Desktop called tanush-oppo"));
    }

    [Fact]
    public async Task Natural_desktop_folder_request_handles_spaces_underscores_and_case()
    {
        var target = Path.Combine(_root, "Keyboard_WTF Project");
        Directory.CreateDirectory(target);
        var service = new SmartFileSearchService(_mappings, [(_root, "Desktop", 10)]);

        var result = await service.SearchAsync(
            "Find the keyboard-wtf project folder on my desktop",
            "",
            includeFiles: false,
            includeFolders: true,
            CancellationToken.None);

        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.Equal(target, result.Best.Path);
    }

    [Fact]
    public async Task Explicit_location_scopes_before_global_duplicate_ranking()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var downloads = Path.Combine(_root, "Downloads");
        var desktopTarget = Path.Combine(desktop, "keyboard.wtf");
        var downloadsTarget = Path.Combine(downloads, "some", "nested", "keyboard.wtf");
        Directory.CreateDirectory(desktopTarget);
        Directory.CreateDirectory(downloadsTarget);
        var service = new SmartFileSearchService(
            _mappings,
            [(desktop, "Desktop", 10), (downloads, "Downloads", 9)]);

        var result = await service.SearchAsync(
            "open the folder called keyboard.wtf on my Desktop",
            "",
            includeFiles: false,
            includeFolders: true,
            CancellationToken.None,
            timeout: TimeSpan.FromTicks(1));

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(desktopTarget, result.Best.Path);
        Assert.All(result.Candidates, candidate =>
            Assert.StartsWith(desktop, candidate.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Exact_direct_child_beats_a_nested_duplicate_within_requested_location()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var direct = Path.Combine(desktop, "Important_Doc.txt");
        var nested = Path.Combine(desktop, "archive", "Important_Doc.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(direct, "current");
        File.WriteAllText(nested, "archived");
        var service = new SmartFileSearchService(_mappings, [(desktop, "Desktop", 10)]);

        var result = await service.SearchAsync(
            "find the important doc on my desktop",
            "",
            includeFiles: true,
            includeFolders: false,
            CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(direct, result.Best.Path);
    }

    [Theory]
    [InlineData("tanush oppo")]
    [InlineData("TANUSH-OPPO")]
    [InlineData("tanush_op")]
    public async Task Explicit_location_normalizes_case_spaces_underscores_hyphens_and_partial_names(
        string queryName)
    {
        var desktop = Path.Combine(_root, "Desktop");
        Directory.CreateDirectory(desktop);
        var target = Path.Combine(desktop, "Tanush_oppo.txt");
        File.WriteAllText(target, "test");
        var service = new SmartFileSearchService(_mappings, [(desktop, "Desktop", 10)]);

        var result = await service.SearchAsync(
            $"find {queryName} on my Desktop",
            "",
            includeFiles: true,
            includeFolders: false,
            CancellationToken.None);

        Assert.NotEqual(FileResolutionStatus.NotFound, result.Status);
        Assert.Equal(target, result.Best.Path);
    }

    [Fact]
    public async Task Explicit_path_scope_ignores_a_learned_duplicate_outside_that_scope()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var downloads = Path.Combine(_root, "Downloads");
        var desktopTarget = Path.Combine(desktop, "keyboard.wtf");
        var downloadsTarget = Path.Combine(downloads, "keyboard.wtf");
        Directory.CreateDirectory(desktopTarget);
        Directory.CreateDirectory(downloadsTarget);
        _mappings.Remember(
            LearnedMappingKind.Folder,
            "keyboard wtf",
            downloadsTarget,
            "Downloads copy");
        var service = new SmartFileSearchService(
            _mappings,
            [(desktop, "Desktop", 10), (downloads, "Downloads", 9)]);

        var result = await service.SearchAsync(
            "keyboard wtf",
            desktop,
            includeFiles: false,
            includeFolders: true,
            CancellationToken.None);

        Assert.Equal(FileResolutionStatus.Found, result.Status);
        Assert.Equal(desktopTarget, result.Best.Path);
    }

    [Theory]
    [InlineData("open it on my Desktop", "desktop")]
    [InlineData("find it in downloads", "downloads")]
    [InlineData("locate it inside my Documents", "documents")]
    [InlineData("open it from OneDrive", "onedrive")]
    public void Extracts_explicit_known_location_from_the_full_user_request(
        string query,
        string expected) =>
        Assert.Equal(expected, SmartFileSearchService.ResolveRequestedLocation(query, ""));

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
