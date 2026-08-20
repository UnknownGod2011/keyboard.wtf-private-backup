namespace KeyboardWtf;

using System.Drawing.Drawing2D;
using KeyboardWtf.Models;
using KeyboardWtf.Services;

internal sealed class VoiceOverlayForm : Form
{
    private readonly AudioRecorderService _audioRecorder;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly Panel _orb;
    private Color _orbColor = Color.FromArgb(90, 90, 90);
    private VoiceUiPhase _lastPhase = VoiceUiPhase.Idle;
    private double _lastLevelDb = -96;

    public VoiceOverlayForm(AudioRecorderService audioRecorder)
    {
        _audioRecorder = audioRecorder;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(18, 18, 18);
        ForeColor = Color.White;
        DoubleBuffered = true;
        ClientSize = new Size(460, 82);
        Opacity = 0.96;
        Padding = new Padding(12);

        _orb = new Panel
        {
            Location = new Point(16, 19),
            Size = new Size(42, 42),
            BackColor = Color.FromArgb(18, 18, 18),
        };
        _orb.Paint += PaintOrb;

        _statusLabel = new Label
        {
            AutoSize = false,
            Location = new Point(74, 13),
            Size = new Size(382, 26),
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            BackColor = Color.Transparent,
            ForeColor = Color.White,
            Text = "keyboard.wtf",
        };

        _detailLabel = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Location = new Point(74, 40),
            Size = new Size(382, 24),
            Font = new Font("Segoe UI", 9),
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(205, 205, 205),
            Text = "Ctrl+Alt+Q for Jarvis",
        };

        Controls.Add(_orb);
        Controls.Add(_statusLabel);
        Controls.Add(_detailLabel);
        Region = RoundedRegion(ClientRectangle, 12);

        _timer = new System.Windows.Forms.Timer { Interval = 80 };
        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        KeyboardWtfState.UiChanged += HandleUiChanged;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExToolWindow = 0x00000080;
            const int wsExNoActivate = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= wsExToolWindow | wsExNoActivate;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PositionOverlay();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (ClientRectangle.Width > 0 && ClientRectangle.Height > 0)
            Region = RoundedRegion(ClientRectangle, 12);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            KeyboardWtfState.UiChanged -= HandleUiChanged;
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void HandleUiChanged()
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            if (IsHandleCreated)
                BeginInvoke((Action)RefreshState);
            return;
        }

        RefreshState();
        // Force the tiny overlay to paint before a slow audio driver blocks
        // the UI thread while opening the selected microphone.
        Update();
    }

    private void RefreshState()
    {
        var phase = KeyboardWtfState.UiPhase;
        if (phase == VoiceUiPhase.Idle)
        {
            if (Visible)
                Hide();
            _lastPhase = phase;
            return;
        }

        var terminal = phase is VoiceUiPhase.Done
            or VoiceUiPhase.Cancelled
            or VoiceUiPhase.Error;
        if (terminal && DateTime.UtcNow - KeyboardWtfState.UiPhaseStartedUtc > TimeSpan.FromSeconds(3.2))
        {
            if (Visible)
                Hide();
            _lastPhase = phase;
            return;
        }

        if (!Visible)
        {
            PositionOverlay();
            Show();
        }

        _statusLabel.Text = KeyboardWtfState.UiTitle;
        _detailLabel.Text = BuildDetail(phase, KeyboardWtfState.UiDetail);
        _orbColor = PhaseColor(phase);

        if (phase != _lastPhase)
            _orb.Invalidate();
        else if (phase == VoiceUiPhase.Listening
            && Math.Abs(KeyboardWtfState.InputLevelDb - _lastLevelDb) > 2)
        {
            _orb.Invalidate();
        }

        _lastLevelDb = KeyboardWtfState.InputLevelDb;
        _lastPhase = phase;
    }

    private void PaintOrb(object sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(_orbColor);
        e.Graphics.FillEllipse(brush, 2, 2, 38, 38);

        if (KeyboardWtfState.UiPhase != VoiceUiPhase.Listening)
            return;

        var normalized = Math.Clamp((KeyboardWtfState.InputLevelDb + 60) / 60, 0, 1);
        var diameter = 12 + (int)(normalized * 17);
        var offset = (42 - diameter) / 2;
        using var center = new SolidBrush(Color.White);
        e.Graphics.FillEllipse(center, offset, offset, diameter, diameter);
    }

    private void PositionOverlay()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 18);
    }

    private static Region RoundedRegion(Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return new Region(path);
    }

    private static string Shorten(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Ready for Ctrl+Alt+Q";
        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..(maxLength - 1)] + "...";
    }

    private static string BuildDetail(VoiceUiPhase phase, string detail)
    {
        var elapsed = Math.Max(0, (int)(DateTime.UtcNow - KeyboardWtfState.UiPhaseStartedUtc).TotalSeconds);
        return phase switch
        {
            VoiceUiPhase.Listening => Shorten(detail, 88),
            VoiceUiPhase.Transcribing or VoiceUiPhase.Thinking or VoiceUiPhase.Executing =>
                Shorten($"{detail}  {elapsed}s - Ctrl+Alt+X cancels", 88),
            _ => Shorten(detail, 88),
        };
    }

    private static Color PhaseColor(VoiceUiPhase phase) => phase switch
    {
        VoiceUiPhase.Listening => Color.FromArgb(239, 68, 68),
        VoiceUiPhase.Transcribing => Color.FromArgb(59, 130, 246),
        VoiceUiPhase.Thinking => Color.FromArgb(168, 85, 247),
        VoiceUiPhase.Executing => Color.FromArgb(245, 158, 11),
        VoiceUiPhase.Speaking => Color.FromArgb(20, 184, 166),
        VoiceUiPhase.Done => Color.FromArgb(34, 197, 94),
        VoiceUiPhase.Cancelled => Color.FromArgb(245, 158, 11),
        VoiceUiPhase.Error => Color.FromArgb(220, 38, 38),
        _ => Color.FromArgb(90, 90, 90),
    };
}
