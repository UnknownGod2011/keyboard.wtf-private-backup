namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class TaskAgentSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "keyboard-wtf-task-settings",
        Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public TaskAgentSettingsTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "settings.json");
    }

    [Fact]
    public void TaskAgentDefaultsAreSafeAndVisible()
    {
        var settings = new SettingsService(_path);
        settings.Load();

        Assert.True(settings.Current.ShowTaskPopup);
        Assert.True(settings.Current.AskForClarificationWhenLowConfidence);
        Assert.Equal(TaskClarificationMode.SmartClarify, settings.Current.TaskClarificationMode);
        Assert.True(settings.Current.BrowserAgentEnabled);
        Assert.Equal(SmartBrowserBackendMode.Auto, settings.Current.SmartBrowserBackend);
        Assert.True(settings.Current.BrowserCompanionEnabled);
        Assert.True(settings.Current.BrowserUseEnabled);
        Assert.False(settings.Current.BrowserOSEnabled);
        Assert.True(settings.Current.LearnBrowserWorkflows);
        Assert.Equal(180, settings.Current.MaxBrowserTaskSeconds);
        Assert.False(settings.Current.BrowserAgentDeveloperDiagnostics);
    }

    [Fact]
    public void TaskAgentSettingsPersist()
    {
        var settings = new SettingsService(_path);
        settings.Load();
        settings.SaveShowTaskPopup(false);
        settings.SaveAskForClarificationWhenLowConfidence(false);
        settings.SaveTaskClarificationMode(TaskClarificationMode.LetAiDecide);
        settings.SaveBrowserAgentEnabled(false);
        settings.SaveSmartBrowserBackend(SmartBrowserBackendMode.BrowserCompanionExtension);
        settings.SaveBrowserCompanionEnabled(false);
        settings.SaveBrowserUseEnabled(false);
        settings.SaveBrowserOSEnabled(true);
        settings.SaveLearnBrowserWorkflows(false);
        settings.SaveMaxBrowserTaskSeconds(45);
        settings.SaveBrowserAgentDeveloperDiagnostics(true);

        var reloaded = new SettingsService(_path);
        reloaded.Load();

        Assert.False(reloaded.Current.ShowTaskPopup);
        Assert.False(reloaded.Current.AskForClarificationWhenLowConfidence);
        Assert.Equal(TaskClarificationMode.LetAiDecide, reloaded.Current.TaskClarificationMode);
        Assert.False(reloaded.Current.BrowserAgentEnabled);
        Assert.Equal(SmartBrowserBackendMode.BrowserCompanionExtension, reloaded.Current.SmartBrowserBackend);
        Assert.False(reloaded.Current.BrowserCompanionEnabled);
        Assert.False(reloaded.Current.BrowserUseEnabled);
        Assert.True(reloaded.Current.BrowserOSEnabled);
        Assert.False(reloaded.Current.LearnBrowserWorkflows);
        Assert.Equal(45, reloaded.Current.MaxBrowserTaskSeconds);
        Assert.True(reloaded.Current.BrowserAgentDeveloperDiagnostics);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
