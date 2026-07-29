namespace KeyboardWtf;

using KeyboardWtf.Services;

internal sealed class TimerProgressForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private readonly JarvisTimerService _timers;
    private readonly Label _title;
    private readonly Label _remaining;
    private readonly Label _count;
    private readonly Button _cancel;
    private readonly System.Windows.Forms.Timer _refresh;
    private readonly System.Windows.Forms.Timer _terminalHide;
    private JarvisTimerSnapshot _displayed;

    public TimerProgressForm(JarvisTimerService timers)
    {
        _timers = timers;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "Jarvis Timer";
        ClientSize = new Size(330, 122);
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Color.White;

        _title = LabelAt(14, 12, 300, 24, FontStyle.Bold);
        _remaining = LabelAt(14, 42, 190, 34, FontStyle.Regular);
        _remaining.Font = new Font("Segoe UI", 17, FontStyle.Bold);
        _count = LabelAt(14, 83, 190, 22);
        _count.ForeColor = Color.FromArgb(205, 205, 205);
        _cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(218, 77),
            Size = new Size(96, 32),
            FlatStyle = FlatStyle.Flat,
        };
        _cancel.Click += (_, _) =>
        {
            if (_displayed != null)
                _timers.Cancel(_displayed.Id);
        };

        Controls.AddRange(new Control[] { _title, _remaining, _count, _cancel });
        _timers.Changed += OnTimerChanged;
        _refresh = new System.Windows.Forms.Timer { Interval = 250 };
        _refresh.Tick += (_, _) => RefreshDisplay();
        _refresh.Start();
        _terminalHide = new System.Windows.Forms.Timer { Interval = 3200 };
        _terminalHide.Tick += (_, _) =>
        {
            _terminalHide.Stop();
            ShowNextOrHide();
        };
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timers.Changed -= OnTimerChanged;
            _refresh.Dispose();
            _terminalHide.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnTimerChanged(JarvisTimerSnapshot snapshot)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnTimerChanged(snapshot));
            return;
        }

        _displayed = snapshot;
        if (snapshot.State == JarvisTimerState.Running)
        {
            _terminalHide.Stop();
            if (!Visible)
            {
                PositionWindow();
                Show();
            }
        }
        else
        {
            _terminalHide.Stop();
            _terminalHide.Start();
        }
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        if (_displayed == null)
        {
            ShowNextOrHide();
            return;
        }

        var active = _timers.ActiveTimers;
        _title.Text = _displayed.Label;
        _remaining.Text = _displayed.State switch
        {
            JarvisTimerState.Completed => "Finished",
            JarvisTimerState.Cancelled => "Cancelled",
            _ => FormatRemaining(_displayed.Remaining),
        };
        _count.Text = active.Count switch
        {
            0 => "",
            1 => "1 active timer",
            _ => $"{active.Count} active timers",
        };
        _cancel.Enabled = _displayed.State == JarvisTimerState.Running;
    }

    private void ShowNextOrHide()
    {
        var next = _timers.ActiveTimers.FirstOrDefault();
        if (next == null)
        {
            _displayed = null;
            if (Visible)
                Hide();
            return;
        }

        _displayed = next;
        if (!Visible)
        {
            PositionWindow();
            Show();
        }
        RefreshDisplay();
    }

    private void PositionWindow()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Left + 18, area.Top + 18);
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        var total = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        return total >= 3600
            ? $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}"
            : $"{total / 60:00}:{total % 60:00}";
    }

    private static Label LabelAt(
        int x,
        int y,
        int width,
        int height,
        FontStyle style = FontStyle.Regular) => new()
    {
        AutoEllipsis = true,
        Location = new Point(x, y),
        Size = new Size(width, height),
        Font = new Font("Segoe UI", 9.5f, style),
        ForeColor = Color.White,
    };
}
