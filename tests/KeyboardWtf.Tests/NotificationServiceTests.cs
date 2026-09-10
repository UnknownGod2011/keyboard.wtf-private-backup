namespace KeyboardWtf.Tests;

using System.Windows.Forms;
using KeyboardWtf.Services;
using Xunit;

public sealed class NotificationServiceTests
{
    [Fact]
    public void Routine_information_does_not_publish_a_system_notification()
    {
        var published = new List<(string Title, string Message, ToolTipIcon Icon)>();
        var notifications = NotificationService.CreateForTesting(
            (title, message, icon) => published.Add((title, message, icon)));

        notifications.Info("Opened", "File Explorer");
        notifications.InfoTransient("Browser", "Switched to the last Chrome tab.");

        Assert.Empty(published);
    }

    [Fact]
    public void Attention_warnings_and_errors_publish_system_notifications()
    {
        var published = new List<(string Title, string Message, ToolTipIcon Icon)>();
        var notifications = NotificationService.CreateForTesting(
            (title, message, icon) => published.Add((title, message, icon)));

        notifications.Attention("Jarvis timer", "Tea timer finished.");
        notifications.AttentionTransient("Browser automation", "The long task completed.");
        notifications.Warning("Account action required", "Sign in to continue.");
        notifications.WarningTransient("Browser automation needs you", "Choose an account.");
        notifications.Error("Jarvis unavailable", "The connection could not be restored.");

        Assert.Collection(
            published,
            item => Assert.Equal(("Jarvis timer", "Tea timer finished.", ToolTipIcon.Info), item),
            item => Assert.Equal(("Browser automation", "The long task completed.", ToolTipIcon.Info), item),
            item => Assert.Equal(("Account action required", "Sign in to continue.", ToolTipIcon.Warning), item),
            item => Assert.Equal(("Browser automation needs you", "Choose an account.", ToolTipIcon.Warning), item),
            item => Assert.Equal(("Jarvis unavailable", "The connection could not be restored.", ToolTipIcon.Error), item));
    }

    [Fact]
    public void Outcome_keeps_success_routine_but_surfaces_failure()
    {
        var published = new List<(string Title, string Message, ToolTipIcon Icon)>();
        var notifications = NotificationService.CreateForTesting(
            (title, message, icon) => published.Add((title, message, icon)));

        notifications.Outcome(true, "Sent", "Send failed", "Copied to the active app.");
        notifications.Outcome(false, "Sent", "Send failed", "The target window closed.");

        var item = Assert.Single(published);
        Assert.Equal(("Send failed", "The target window closed.", ToolTipIcon.Warning), item);
    }
}
