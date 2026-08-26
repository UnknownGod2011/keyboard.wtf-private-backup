namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

public sealed class RoutineMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-routine-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RememberRoutineSavesWorkflowAndFuzzyTrigger()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        var history = new JarvisActionHistoryService(Path.Combine(_root, "history.json"));
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            history,
            mappings,
            () => { });
        var args = JsonSerializer.SerializeToElement(new
        {
            trigger = "initialize my setup",
            apps = "Notepad",
            urls = "",
            folder = "",
        });

        var result = await automation.ExecuteAsync("remember_routine", args, CancellationToken.None);

        Assert.True((bool)result["ok"]);
        Assert.Contains(
            settings.Current.JarvisWorkflows,
            workflow => workflow.Name.Equals("initialize my setup", StringComparison.OrdinalIgnoreCase)
                && workflow.Apps == "Notepad"
                && workflow.Version == JarvisWorkflowSettings.CurrentVersion
                && workflow.Actions.Count == 1
                && workflow.Actions[0].Kind == "app");
        Assert.Contains(
            mappings.Search("initialize my set", LearnedMappingKind.Workflow, 3),
            mapping => mapping.Target.Equals("initialize my setup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Explicitly_remembered_routine_survives_complete_service_reload()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var settings = new SettingsService(settingsPath);
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var mappings = new LearnedMappingService(mappingsPath);
        mappings.Load();
        using (var automation = new JarvisAutomationService(
                   new NotificationService(null),
                   settings,
                   new JarvisActionHistoryService(historyPath),
                   mappings,
                   () => { }))
        {
            var args = JsonSerializer.SerializeToElement(new
            {
                trigger = "potato patato",
                apps = "Notepad",
                urls = "https://example.com/",
                folder = "",
            });
            var result = await automation.ExecuteAsync("remember_routine", args, CancellationToken.None);
            Assert.True((bool)result["ok"]);
        }

        var reloadedSettings = new SettingsService(settingsPath);
        reloadedSettings.Load();
        var reloadedMappings = new LearnedMappingService(mappingsPath);
        reloadedMappings.Load();

        var workflow = Assert.Single(reloadedSettings.Current.JarvisWorkflows);
        Assert.Equal("potato patato", workflow.Name, ignoreCase: true);
        Assert.Equal("Notepad", workflow.Apps);
        Assert.Equal("https://example.com/", workflow.Urls);
        Assert.Equal(JarvisWorkflowSettings.CurrentVersion, workflow.Version);
        Assert.Collection(
            workflow.Actions,
            action =>
            {
                Assert.Equal("app", action.Kind);
                Assert.Equal("Notepad", action.Target);
            },
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://example.com/", action.Target);
            });
        Assert.Equal(
            "potato patato",
            reloadedMappings.FindExact(LearnedMappingKind.Workflow, "potato patato")?.Target,
            ignoreCase: true);
    }

    [Fact]
    public async Task Ordered_durable_workflow_resolves_in_a_completely_new_conversation_service()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var firstSettings = new SettingsService(settingsPath);
        firstSettings.Load();
        firstSettings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var firstMappings = new LearnedMappingService(mappingsPath);
        firstMappings.Load();
        using (var firstConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   firstSettings,
                   new JarvisActionHistoryService(historyPath),
                   firstMappings,
                   () => { }))
        {
            var result = await firstConversation.ExecuteAsync(
                "remember_routine",
                JsonSerializer.SerializeToElement(new
                {
                    trigger = "potato patato",
                    actions = new object[]
                    {
                        new { kind = "url", target = "https://mail.google.com/" },
                        new { kind = "app", target = "Discord" },
                        new { kind = "url", target = "https://devpost.com/" },
                        new { kind = "url", target = "https://www.instagram.com/" },
                        new { kind = "app", target = "Excel" },
                    },
                }),
                CancellationToken.None);

            Assert.True((bool)result["ok"]);
            Assert.True((bool)result["persistence_verified"]);
        }

        var secondSettings = new SettingsService(settingsPath);
        secondSettings.Load();
        var secondMappings = new LearnedMappingService(mappingsPath);
        secondMappings.Load();
        using var completelyNewConversation = new JarvisAutomationService(
            new NotificationService(null),
            secondSettings,
            new JarvisActionHistoryService(historyPath),
            secondMappings,
            () => { });

        Assert.True(completelyNewConversation.TryResolveSavedWorkflowTrigger(
            "Potato-patato!",
            out var canonical));
        Assert.Equal("potato patato", canonical, ignoreCase: true);
        var persisted = secondSettings.ReadPersistedWorkflow("potato patato");
        Assert.NotNull(persisted);
        Assert.Equal(5, persisted.Actions.Count);
        Assert.Equal(
            new[] { "url", "app", "url", "url", "app" },
            persisted.Actions.Select(action => action.Kind).ToArray());
        Assert.Contains(
            "[potato patato]",
            JarvisAutomationService.BuildSavedWorkflowPromptDigest(secondSettings.Current.JarvisWorkflows),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_workflow_save_language_is_never_browser_pre_routed()
    {
        Assert.True(JarvisAutomationService.IsExplicitWorkflowPersistenceRequest(
            "Whenever I say potato patato, open Gmail and Discord. Remember this."));
        Assert.True(JarvisAutomationService.IsExplicitWorkflowPersistenceRequest(
            "Save this workflow for later."));
        Assert.False(JarvisAutomationService.IsExplicitWorkflowPersistenceRequest(
            "Open Gmail."));
    }

    [Fact]
    public async Task Deleting_a_workflow_by_alias_removes_and_tombstones_the_canonical_routine()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        settings.SaveWorkflow("initialize my setup", "Notepad", "", "");
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        mappings.Remember(
            LearnedMappingKind.Workflow,
            "start my day",
            "initialize my setup",
            "Initialize my setup");
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });

        var result = await automation.ExecuteAsync(
            "delete_workflow",
            JsonSerializer.SerializeToElement(new { name = "start my day" }),
            CancellationToken.None);

        Assert.True((bool)result["ok"]);
        Assert.Empty(settings.Current.JarvisWorkflows);
        Assert.True(settings.IsWorkflowDeleted("initialize my setup"));
        Assert.True(settings.IsWorkflowDeleted("start my day"));
        Assert.Empty(mappings.Snapshot(LearnedMappingKind.Workflow));

        var reloadedSettings = new SettingsService(settings.SettingsPath);
        reloadedSettings.Load();
        var reloadedMappings = new LearnedMappingService(mappings.MappingPath);
        reloadedMappings.Load();
        using var laterConversation = new JarvisAutomationService(
            new NotificationService(null),
            reloadedSettings,
            new JarvisActionHistoryService(Path.Combine(_root, "later-history.json")),
            reloadedMappings,
            () => { });
        Assert.False(laterConversation.TryResolveSavedWorkflowTrigger("start my day", out _));
        Assert.False(laterConversation.TryResolveSavedWorkflowTrigger("initialize my setup", out _));
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
