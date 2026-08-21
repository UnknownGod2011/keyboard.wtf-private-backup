namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class FinalPolishRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-final-polish-tests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("browser-automation", "browser-automation")]
    [InlineData("#how-to-use-jarvis", "how-to-use")]
    [InlineData("keyboard.wtf text to speech settings", "tts")]
    [InlineData("what's next", "roadmap")]
    [InlineData("account", "account-billing")]
    public void Settings_routes_use_one_canonical_section_registry(
        string requested,
        string expected) =>
        Assert.Equal(expected, SettingsSectionRegistry.Resolve(requested));

    [Fact]
    public void Every_canonical_settings_route_exists_in_the_packaged_page()
    {
        var root = FindRepositoryRoot();
        var html = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));
        foreach (var id in SettingsSectionRegistry.CanonicalIds)
            Assert.Contains($"<section id=\"{id}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleted_workflow_tombstone_survives_restart_and_is_cleared_by_explicit_resave()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        var settings = new SettingsService(path);
        settings.Load();
        settings.SaveWorkflow("initialize my setup", "Notepad", "", "");

        Assert.True(settings.DeleteWorkflow("initialize my setup"));
        Assert.True(settings.IsWorkflowDeleted("initialize my setup"));

        var reloaded = new SettingsService(path);
        reloaded.Load();
        Assert.True(reloaded.IsWorkflowDeleted("initialize my setup"));
        Assert.Empty(reloaded.Current.JarvisWorkflows);

        reloaded.SaveWorkflow("initialize my setup", "Calculator", "", "");
        Assert.False(reloaded.IsWorkflowDeleted("initialize my setup"));
        Assert.Equal("Calculator", Assert.Single(reloaded.Current.JarvisWorkflows).Apps);
    }

    [Fact]
    public void Deleting_a_workflow_removes_aliases_and_targets_from_learned_mappings()
    {
        Directory.CreateDirectory(_root);
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(
            LearnedMappingKind.Workflow,
            "start my day",
            "initialize my setup",
            "Initialize my setup");

        Assert.Equal(1, mappings.ForgetWorkflowReferences("initialize my setup"));
        Assert.Empty(mappings.Snapshot());
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(91, false)]
    public void Cached_selection_is_only_safe_in_the_same_recent_window(int ageSeconds, bool expected)
    {
        var context = new SelectionContext((IntPtr)10, "notepad", "Draft - Notepad");
        var snapshot = new SelectionSnapshot(
            "current selection",
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(ageSeconds),
            context.Handle,
            context.ProcessName,
            context.WindowTitle);

        Assert.Equal(expected, JarvisAutomationService.CanUseCachedSelection(
            snapshot,
            context,
            DateTimeOffset.UtcNow));
        Assert.False(JarvisAutomationService.CanUseCachedSelection(
            snapshot,
            new SelectionContext((IntPtr)11, "chrome", "Unrelated tab"),
            DateTimeOffset.UtcNow));
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
