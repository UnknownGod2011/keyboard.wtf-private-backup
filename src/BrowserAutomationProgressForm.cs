namespace KeyboardWtf;

using KeyboardWtf.Services.BrowserAutomationV2;

internal sealed class BrowserAutomationProgressForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private readonly BrowserAutomationCoordinator _coordinator;
    private readonly Label _title;
    private readonly Label _status;
    private readonly Label _tab;
    private readonly Label _elapsed;
    private readonly Button _pauseResume;
    private readonly System.Windows.Forms.Timer _timer;
    private BrowserAutomationSnapshot _snapshot;

    public BrowserAutomationProgressForm(BrowserAutomationCoordinator coordinator)
    {
        _coordinator = coordinator;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Browser Automation";
        ClientSize = new Size(470, 150);
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Color.White;

        _title = LabelAt(14, 12, 440, 22, FontStyle.Bold);
        _status = LabelAt(14, 38, 440, 22);
        _tab = LabelAt(14, 62, 440, 20);
        _elapsed = LabelAt(14, 85, 180, 24);
        _elapsed.ForeColor = Color.FromArgb(205, 205, 205);

        _pauseResume = new Button
        {
            Text = "Pause",
            Location = new Point(230, 105),
            Size = new Size(104, 32),
            FlatStyle = FlatStyle.Flat,
        };
        _pauseResume.Click += (_, _) =>
        {
            if (_snapshot?.CanResume == true)
                _coordinator.Resume();
            else
                _coordinator.Pause();
        };

        var cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(344, 105),
            Size = new Size(104, 32),
            FlatStyle = FlatStyle.Flat,
        };
        cancel.Click += async (_, _) => await _coordinator.CancelAsync();

        Controls.AddRange(new Control[] { _title, _status, _tab, _elapsed, _pauseResume, cancel });
        _coordinator.StatusChanged += OnStatusChanged;
        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) => RefreshElapsed();
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coordinator.StatusChanged -= OnStatusChanged;
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate;
            return parameters;
        }
    }

    private void OnStatusChanged(BrowserAutomationSnapshot snapshot)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnStatusChanged(snapshot));
            return;
        }

        _snapshot = snapshot;
        _title.Text = Short(snapshot.Request, 58);
        _status.Text = $"{snapshot.State}: {Short(snapshot.CurrentAction, 70)}";
        _tab.Text = string.IsNullOrWhiteSpace(snapshot.CurrentDomain)
            ? "Waiting for Chrome Helper"
            : $"{Short(snapshot.CurrentTabTitle, 45)} · {snapshot.CurrentDomain}";
        _pauseResume.Text = snapshot.CanResume ? "Resume" : "Pause";
        _pauseResume.Enabled = snapshot.CanPause || snapshot.CanResume;

        if (IsTerminal(snapshot.State))
        {
            if (Visible)
            {
                var timer = new System.Windows.Forms.Timer { Interval = 2200 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    if (!IsDisposed) Hide();
                };
                timer.Start();
            }
        }
        else if (!Visible)
        {
            PositionWindow();
            Show();
        }

        RefreshElapsed();
    }

    private void RefreshElapsed()
    {
        if (_snapshot == null)
            return;
        var elapsed = DateTime.UtcNow - _snapshot.StartedUtc;
        _elapsed.Text = $"{elapsed.TotalSeconds:0}s · Ctrl+Alt+X cancels";
    }

    private Label LabelAt(int x, int y, int width, int height, FontStyle style = FontStyle.Regular) =>
        new()
        {
            AutoEllipsis = true,
            Location = new Point(x, y),
            Size = new Size(width, height),
            Font = new Font("Segoe UI", 9.5f, style),
            ForeColor = Color.White,
        };

    private void PositionWindow()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Right - Width - 18, area.Top + 18);
    }

    private static bool IsTerminal(BrowserTaskState state) =>
        state is BrowserTaskState.Completed
            or BrowserTaskState.CompletedWithNote
            or BrowserTaskState.Partial
            or BrowserTaskState.Blocked
            or BrowserTaskState.Cancelled
            or BrowserTaskState.Failed;

    private static string Short(string value, int max) =>
        BrowserAutomationText.Truncate(value, max);
}
