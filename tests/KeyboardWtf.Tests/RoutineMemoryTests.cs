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
    public void Complete_explicit_save_turn_is_persisted_without_relying_on_model_tool_choice()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });

        var handled = automation.TryRememberWorkflowFromTranscript(
            "Whenever I say potato patato, open Gmail, Discord, Devpost, Instagram, and Excel. Remember this.",
            completeTurn: true,
            out var result);

        Assert.True(handled);
        Assert.True((bool)result["ok"]);
        Assert.True((bool)result["persistence_verified"]);
        var persisted = settings.ReadPersistedWorkflow("potato patato");
        Assert.NotNull(persisted);
        Assert.Collection(
            persisted.Actions,
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://mail.google.com/", action.Target);
            },
            action =>
            {
                Assert.Equal("app", action.Kind);
                Assert.Equal("Discord", action.Target);
            },
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://devpost.com/", action.Target);
            },
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://www.instagram.com/", action.Target);
            },
            action =>
            {
                Assert.Contains(action.Kind, new[] { "app", "auto" });
                Assert.Equal("Excel", action.Target);
            });
        Assert.Equal(
            "potato patato",
            mappings.FindExact(LearnedMappingKind.Workflow, "potato patato")?.Target,
            ignoreCase: true);
    }

    [Fact]
    public void Incremental_voice_save_waits_for_the_explicit_terminal_marker()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });

        Assert.False(automation.TryRememberWorkflowFromTranscript(
            "Whenever I say focus mode, open Gmail",
            completeTurn: false,
            out _));
        Assert.Empty(settings.Current.JarvisWorkflows);

        Assert.True(automation.TryRememberWorkflowFromTranscript(
            "Whenever I say focus mode, open Gmail and Notepad. Remember this.",
            completeTurn: false,
            out var result));
        Assert.True((bool)result["persistence_verified"]);
        Assert.Equal(2, settings.ReadPersistedWorkflow("focus mode").Actions.Count);
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
        Assert.True(laterConversation.IsDeletedWorkflowTrigger("start my day"));
        Assert.True(laterConversation.IsDeletedWorkflowTrigger("Initialize-my setup!"));
        Assert.False(laterConversation.IsDeletedWorkflowTrigger("an unrelated request"));
    }

    [Fact]
    public void ExplicitDurableWebCorrectionPersistsUpdatesAndDeletesAcrossConversations()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var settings = new SettingsService(settingsPath);
        settings.Load();
        var mappings = new LearnedMappingService(mappingsPath);
        mappings.Load();
        using (var firstConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   settings,
                   new JarvisActionHistoryService(historyPath),
                   mappings,
                   () => { }))
        {
            Assert.True(firstConversation.TryApplyDurablePreferenceFromTranscript(
                "Always open Discord on the web instead.",
                out var tool,
                out var saved));
            Assert.Equal("remember_link_alias", tool);
            Assert.True((bool)saved["ok"]);
            Assert.True((bool)saved["persistence_verified"]);
            Assert.False(firstConversation.TryApplyDurablePreferenceFromTranscript(
                "Open Discord on the web this time.",
                out _,
                out _));
        }

        var secondConversationMappings = new LearnedMappingService(mappingsPath);
        secondConversationMappings.Load();
        Assert.Equal(
            "https://discord.com/app",
            secondConversationMappings.FindExact(LearnedMappingKind.Link, "discord")?.Target);
        var secondSettings = new SettingsService(settingsPath);
        secondSettings.Load();
        using (var secondConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   secondSettings,
                   new JarvisActionHistoryService(historyPath),
                   secondConversationMappings,
                   () => { }))
        {
            Assert.True(secondConversation.TryApplyDurablePreferenceFromTranscript(
                "Always open https://discord.com/channels/@me when I say Discord.",
                out var updateTool,
                out var updated));
            Assert.Equal("remember_link_alias", updateTool);
            Assert.True((bool)updated["ok"]);
        }

        var updatedMappings = new LearnedMappingService(mappingsPath);
        updatedMappings.Load();
        Assert.Equal(
            "https://discord.com/channels/@me",
            updatedMappings.FindExact(LearnedMappingKind.Link, "discord")?.Target);
        using (var finalConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   new SettingsService(settingsPath),
                   new JarvisActionHistoryService(historyPath),
                   updatedMappings,
                   () => { }))
        {
            Assert.True(finalConversation.TryApplyDurablePreferenceFromTranscript(
                "Forget my saved preference for Discord.",
                out var deleteTool,
                out var deleted));
            Assert.Equal("forget_learned_mapping", deleteTool);
            Assert.True((bool)deleted["ok"]);
        }
        var afterDelete = new LearnedMappingService(mappingsPath);
        afterDelete.Load();
        Assert.Null(afterDelete.FindExact(LearnedMappingKind.Link, "discord"));
    }

    [Fact]
    public async Task WorkflowEditsUpdateOneAuthoritativeRecordAndSurviveFreshConversations()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        var mappingsPath = Path.Combine(_root, "mappings.json");
        var historyPath = Path.Combine(_root, "history.json");
        var settings = new SettingsService(settingsPath);
        settings.Load();
        var mappings = new LearnedMappingService(mappingsPath);
        mappings.Load();
        using (var firstConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   settings,
                   new JarvisActionHistoryService(historyPath),
                   mappings,
                   () => { }))
        {
            Assert.True(firstConversation.TryRememberWorkflowFromTranscript(
                "Whenever I say duck duck setup, open Gmail and Devpost. Remember this.",
                completeTurn: true,
                out var saved));
            Assert.True((bool)saved["ok"]);

            Assert.True(firstConversation.TryEditWorkflowFromTranscript(
                "Add Instagram to my duck duck setup.",
                out var added));
            Assert.True((bool)added["ok"], added["message"]?.ToString());
            Assert.Equal(3, settings.ReadPersistedWorkflow("duck duck setup").Actions.Count);
        }

        var secondSettings = new SettingsService(settingsPath);
        secondSettings.Load();
        var secondMappings = new LearnedMappingService(mappingsPath);
        secondMappings.Load();
        using (var secondConversation = new JarvisAutomationService(
                   new NotificationService(null),
                   secondSettings,
                   new JarvisActionHistoryService(historyPath),
                   secondMappings,
                   () => { }))
        {
            Assert.True(secondConversation.TryResolveSavedWorkflowTrigger("duck duck setup", out _));
            Assert.True(secondConversation.TryEditWorkflowFromTranscript(
                "Remove Devpost from that workflow.",
                out var removed));
            Assert.True((bool)removed["ok"]);
            Assert.True(secondConversation.TryEditWorkflowFromTranscript(
                "Change Gmail to Google Sheets in that workflow.",
                out var replaced));
            Assert.True((bool)replaced["ok"]);
            Assert.True(secondConversation.TryEditWorkflowFromTranscript(
                "Add alias morning ducks to my duck duck setup.",
                out var aliased));
            Assert.True((bool)aliased["ok"]);
        }

        var thirdSettings = new SettingsService(settingsPath);
        thirdSettings.Load();
        thirdSettings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
        var persisted = thirdSettings.ReadPersistedWorkflow("morning ducks");
        Assert.NotNull(persisted);
        Assert.Collection(
            persisted.Actions,
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://docs.google.com/spreadsheets/", action.Target);
            },
            action =>
            {
                Assert.Equal("url", action.Kind);
                Assert.Equal("https://www.instagram.com/", action.Target);
            });
        Assert.DoesNotContain("Devpost", persisted.Apps + persisted.Urls, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("morning ducks", persisted.Aliases, StringComparer.OrdinalIgnoreCase);

        var thirdMappings = new LearnedMappingService(mappingsPath);
        thirdMappings.Load();
        using var thirdConversation = new JarvisAutomationService(
            new NotificationService(null),
            thirdSettings,
            new JarvisActionHistoryService(historyPath),
            thirdMappings,
            () => { });
        Assert.True(thirdConversation.TryResolveSavedWorkflowTrigger("morning ducks", out var canonical));
        Assert.Equal("duck duck setup", canonical, ignoreCase: true);
        thirdConversation.ResetConversationTransientContext();
        Assert.False(thirdConversation.TryEditWorkflowFromTranscript(
            "Add GitHub to that workflow.",
            out _));

        var deleted = await thirdConversation.ExecuteAsync(
            "delete_workflow",
            JsonSerializer.SerializeToElement(new { name = "morning ducks" }),
            CancellationToken.None);
        Assert.True((bool)deleted["ok"]);
        var afterDeleteSettings = new SettingsService(settingsPath);
        afterDeleteSettings.Load();
        var afterDeleteMappings = new LearnedMappingService(mappingsPath);
        afterDeleteMappings.Load();
        using var afterDeleteConversation = new JarvisAutomationService(
            new NotificationService(null),
            afterDeleteSettings,
            new JarvisActionHistoryService(historyPath),
            afterDeleteMappings,
            () => { });
        Assert.False(afterDeleteConversation.TryResolveSavedWorkflowTrigger("morning ducks", out _));
        Assert.False(afterDeleteConversation.TryResolveSavedWorkflowTrigger("duck duck setup", out _));
        Assert.True(afterDeleteConversation.IsDeletedWorkflowTrigger("morning ducks"));
    }

    [Fact]
    public void WebPageMutationLanguageIsNotMistakenForAWorkflowEdit()
    {
        Directory.CreateDirectory(_root);
        var settings = new SettingsService(Path.Combine(_root, "settings.json"));
        settings.Load();
        var mappings = new LearnedMappingService(Path.Combine(_root, "mappings.json"));
        mappings.Load();
        using var automation = new JarvisAutomationService(
            new NotificationService(null),
            settings,
            new JarvisActionHistoryService(Path.Combine(_root, "history.json")),
            mappings,
            () => { });

        Assert.False(automation.TryEditWorkflowFromTranscript(
            "Add the first result to my Amazon cart.",
            out _));
        Assert.False(automation.TryEditWorkflowFromTranscript(
            "Remove the selected file from Downloads.",
            out _));
        Assert.False(automation.TryEditWorkflowFromTranscript(
            "Change the destination to Mumbai in the current page.",
            out _));
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
