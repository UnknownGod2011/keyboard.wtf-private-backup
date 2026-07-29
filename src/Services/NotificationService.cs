namespace KeyboardWtf.Services;

using System.Windows.Forms;

public sealed class NotificationService
{
    private readonly NotifyIcon _notifyIcon;

    public NotificationService(NotifyIcon notifyIcon) => _notifyIcon = notifyIcon;

    public void Info(string title, string message) => Show(title, message, ToolTipIcon.Info);
    public void Warning(string title, string message) => Show(title, message, ToolTipIcon.Warning);
    public void Error(string title, string message) => Show(title, message, ToolTipIcon.Error);
    // Browser results may contain page-derived text. Keep the user-facing notification,
    // but do not copy that text into the persistent application log.
    public void InfoTransient(string title, string message) => Show(title, message, ToolTipIcon.Info, writeLog: false);
    public void WarningTransient(string title, string message) => Show(title, message, ToolTipIcon.Warning, writeLog: false);

    private void Show(string title, string message, ToolTipIcon icon, bool writeLog = true)
    {
        if (writeLog)
            AppLog.Info($"{title}: {message}");
        try
        {
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
