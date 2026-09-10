namespace KeyboardWtf.Services;

using System.Windows.Forms;

public sealed class NotificationService
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Action<string, string, ToolTipIcon> _systemNotificationSink;

    public NotificationService(NotifyIcon notifyIcon) => _notifyIcon = notifyIcon;

    private NotificationService(Action<string, string, ToolTipIcon> systemNotificationSink) =>
        _systemNotificationSink = systemNotificationSink;

    internal static NotificationService CreateForTesting(
        Action<string, string, ToolTipIcon> systemNotificationSink) =>
        new(systemNotificationSink);

    // Routine status already appears in keyboard.wtf's UI and, for Jarvis,
    // in the one spoken completion owned by the turn. It must not also become
    // a redundant Windows notification.
    public void Info(string title, string message) =>
        Publish(title, message, ToolTipIcon.Info, notifySystem: false);

    public void Attention(string title, string message) =>
        Publish(title, message, ToolTipIcon.Info, notifySystem: true);

    public void Warning(string title, string message) =>
        Publish(title, message, ToolTipIcon.Warning, notifySystem: true);

    public void Error(string title, string message) =>
        Publish(title, message, ToolTipIcon.Error, notifySystem: true);

    // Page-derived or generated text must not be copied into the persistent log.
    public void InfoTransient(string title, string message) =>
        Publish(title, message, ToolTipIcon.Info, notifySystem: false, writeLog: false);

    public void AttentionTransient(string title, string message) =>
        Publish(title, message, ToolTipIcon.Info, notifySystem: true, writeLog: false);

    public void WarningTransient(string title, string message) =>
        Publish(title, message, ToolTipIcon.Warning, notifySystem: true, writeLog: false);

    public void Outcome(
        bool succeeded,
        string successTitle,
        string failureTitle,
        string message)
    {
        if (succeeded)
            Info(successTitle, message);
        else
            Warning(failureTitle, message);
    }

    private void Publish(
        string title,
        string message,
        ToolTipIcon icon,
        bool notifySystem,
        bool writeLog = true)
    {
        if (writeLog)
            AppLog.Info($"{title}: {message}");
        if (!notifySystem)
            return;

        try
        {
            if (_systemNotificationSink != null)
            {
                _systemNotificationSink(title, message, icon);
                return;
            }
            if (_notifyIcon == null)
                return;

            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(3500);
        }
        catch
        {
            // Balloon notifications are best-effort only.
        }
    }
}
