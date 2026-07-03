namespace KeyboardWtf;

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using KeyboardWtf.Models;
using KeyboardWtf.Services;

internal sealed class TaskPanelForm : Form
{
    private const int PanelWidth = 430;
    private const int CornerRadius = 26;
    private const int OuterPad = 18;
    private const int HeaderHeight = 64;
    private const int ChipHeight = 28;
    private const int StepIconSize = 18;
    private const int MaxVisibleSteps = 6;
    private static readonly TimeSpan TerminalTaskVisibleFor = TimeSpan.FromSeconds(6);

    private static readonly Color Shell = Color.FromArgb(9, 9, 10);
    private static readonly Color Shell2 = Color.FromArgb(21, 21, 23);
    private static readonly Color Glass = Color.FromArgb(33, 255, 255, 255);
    private static readonly Color GlassStrong = Color.FromArgb(48, 255, 255, 255);
    private static readonly Color Hairline = Color.FromArgb(58, 255, 255, 255);
    private static readonly Color TextPrimary = Color.FromArgb(246, 246, 247);
    private static readonly Color TextSecondary = Color.FromArgb(178, 178, 184);
    private static readonly Color TextTertiary = Color.FromArgb(126, 126, 132);
    private static readonly Color Blue = Color.FromArgb(104, 160, 255);
    private static readonly Color Green = Color.FromArgb(52, 199, 89);
    private static readonly Color Amber = Color.FromArgb(255, 184, 77);
    private static readonly Color Red = Color.FromArgb(255, 95, 86);
    private static readonly Color Muted = Color.FromArgb(92, 99, 116);

    private readonly JarvisTaskManager _tasks;
    private readonly SettingsService _settings;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly TextBox _clarificationBox;
    private readonly List<OverlayButton> _buttons = new();
    private readonly Dictionary<string, Rectangle> _chipBounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Font _titleFont = new("Segoe UI Semibold", 12.5f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _bodyFont = new("Segoe UI", 9.3f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _smallFont = new("Segoe UI", 8.1f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _tinyFont = new("Segoe UI", 7.7f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _buttonFont = new("Segoe UI Semibold", 8.4f, FontStyle.Regular, GraphicsUnit.Point);
    private readonly Font _symbolFont = new("Segoe UI Symbol", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
    private JarvisTask[] _visibleTasks = [];
    private string _selectedTaskId = "";
    private bool _allowClose;
    private bool _detailsExpanded;
    private int _spinnerFrame;
    private bool _dragging;
    private Point _dragOffset;
    private Rectangle _closeBounds;
    private Rectangle _minimizeBounds;
    private Rectangle _detailsBounds;

    public TaskPanelForm(JarvisTaskManager tasks, SettingsService settings)
    {
        _tasks = tasks;
        _settings = settings;

        Text = "Jarvis";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Shell;
        ForeColor = TextPrimary;
        Opacity = 0.96;
        ClientSize = new Size(PanelWidth, 320);
        MinimumSize = new Size(PanelWidth, 240);
        MaximumSize = new Size(PanelWidth, 560);
        Padding = Padding.Empty;
        Font = _bodyFont;
        DoubleBuffered = true;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor,
            true);

        _clarificationBox = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(26, 26, 28),
            ForeColor = TextPrimary,
            Font = _bodyFont,
            Visible = false,
            TabStop = true,
        };
        _clarificationBox.KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Enter)
            {
                SubmitClarification();
                args.SuppressKeyPress = true;
            }
        };
        Controls.Add(_clarificationBox);

        _tasks.TaskChanged += OnTaskChanged;
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _refreshTimer.Tick += (_, _) =>
        {
            _spinnerFrame = (_spinnerFrame + 1) % 24;
            if (_spinnerFrame % 4 == 0)
                RefreshTasks(reposition: false);
            Invalidate();
        };
        _refreshTimer.Start();

        FormClosing += (_, args) =>
        {
            if (_allowClose)
                return;
            args.Cancel = true;
            Hide();
        };

        // Task events are published from worker threads. Create the hidden window
        // handle on the UI thread so the first event can marshal through BeginInvoke.
        _ = Handle;
    }

    public int VisibleTaskTabCount => _visibleTasks.Length;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        PositionPanel();
        EnsureTopMost();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyRoundedRegion();
        UpdateLayoutCache();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Avoid WinForms erasing the background white between custom frames.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var task = SelectedTask;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var background = new LinearGradientBrush(
            ClientRectangle,
            Color.FromArgb(252, Shell2),
            Color.FromArgb(252, Shell),
            LinearGradientMode.Vertical);
        using var path = RoundedRect(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), CornerRadius);
        e.Graphics.FillPath(background, path);
        using (var innerGlass = new SolidBrush(Color.FromArgb(10, 255, 255, 255)))
            e.Graphics.FillPath(innerGlass, RoundedRect(new Rectangle(1, 1, ClientSize.Width - 3, 42), CornerRadius - 2));
        using (var borderPen = new Pen(Hairline, 1))
            e.Graphics.DrawPath(borderPen, path);

        if (task == null)
            return;

        UpdateLayoutCache();
        DrawHeader(e.Graphics, task);
        DrawTaskChips(e.Graphics);
        DrawContext(e.Graphics, task);
        DrawSteps(e.Graphics, task);
        DrawPromptOrSummary(e.Graphics, task);
        DrawButtons(e.Graphics);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        if (_closeBounds.Contains(e.Location))
        {
            Hide();
            return;
        }
        if (_minimizeBounds.Contains(e.Location))
        {
            Hide();
            return;
        }
        foreach (var pair in _chipBounds)
        {
            if (pair.Value.Contains(e.Location))
            {
                _selectedTaskId = pair.Key;
                _detailsExpanded = false;
                UpdateInteractiveControls();
                Invalidate();
                return;
            }
        }
        foreach (var button in _buttons.ToArray())
        {
            if (button.Bounds.Contains(e.Location))
            {
                button.Click();
                return;
            }
        }
        if (_detailsBounds.Contains(e.Location))
        {
            _detailsExpanded = !_detailsExpanded;
            ApplyDesiredSize(SelectedTask, _visibleTasks.Length);
            UpdateLayoutCache();
            Invalidate();
            return;
        }
        if (e.Y <= HeaderHeight + (_visibleTasks.Length > 1 ? ChipHeight + 10 : 10))
        {
            _dragging = true;
            _dragOffset = e.Location;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            Location = new Point(MousePosition.X - _dragOffset.X, MousePosition.Y - _dragOffset.Y);
            return;
        }
        var hover = _closeBounds.Contains(e.Location)
            || _minimizeBounds.Contains(e.Location)
            || _detailsBounds.Contains(e.Location)
            || _chipBounds.Values.Any(rect => rect.Contains(e.Location))
            || _buttons.Any(button => button.Bounds.Contains(e.Location));
        Cursor = hover ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _allowClose = true;
            _tasks.TaskChanged -= OnTaskChanged;
            _refreshTimer.Dispose();
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _smallFont.Dispose();
            _tinyFont.Dispose();
            _buttonFont.Dispose();
            _symbolFont.Dispose();
            _clarificationBox.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0014) // WM_ERASEBKGND
            return;
        base.WndProc(ref m);
    }

    private JarvisTask SelectedTask =>
        _visibleTasks.FirstOrDefault(task => string.Equals(task.TaskId, _selectedTaskId, StringComparison.OrdinalIgnoreCase))
        ?? _visibleTasks.FirstOrDefault();

    private void OnTaskChanged(JarvisTask task)
    {
        if (IsDisposed)
            return;
        try
        {
            BeginInvoke(() =>
            {
                if (_settings.Current.ShowTaskPopup && IsVisibleTask(task) && !Visible)
                {
                    RefreshTasks(reposition: true);
                    Show();
                    EnsureTopMost();
                    return;
                }
                RefreshTasks(reposition: Visible);
                if (Visible)
                    EnsureTopMost();
            });
        }
        catch { }
    }

    private void RefreshTasks(bool reposition)
    {
        if (IsDisposed)
            return;
        if (!_settings.Current.ShowTaskPopup)
        {
            if (Visible)
                Hide();
            return;
        }

        var tasks = _tasks.Snapshot()
            .Where(IsVisibleTask)
            .OrderByDescending(task => IsActive(task.Status))
            .ThenByDescending(task => task.UpdatedAt)
            .Take(JarvisTaskManager.MaxActiveTasks)
            .ToArray();
        if (tasks.Length == 0)
        {
            _visibleTasks = [];
            _selectedTaskId = "";
            UpdateInteractiveControls();
            if (Visible)
                Hide();
            return;
        }

        _visibleTasks = tasks;
        var selectedTask = _visibleTasks.FirstOrDefault(task =>
            string.Equals(task.TaskId, _selectedTaskId, StringComparison.OrdinalIgnoreCase));
        if (selectedTask == null || (!IsActive(selectedTask.Status) && _visibleTasks.Any(task => IsActive(task.Status))))
        {
            _selectedTaskId = _visibleTasks[0].TaskId;
            _detailsExpanded = false;
        }

        ApplyDesiredSize(SelectedTask, _visibleTasks.Length);
        UpdateInteractiveControls();
        if (reposition)
            PositionPanel();
        Invalidate();
    }

    private void ApplyDesiredSize(JarvisTask task, int taskCount)
    {
        if (task == null)
            return;

        var desired = ComputeDesiredHeight(task, taskCount);
        if (ClientSize.Width != PanelWidth || ClientSize.Height != desired)
            ClientSize = new Size(PanelWidth, desired);
    }

    private int ComputeDesiredHeight(JarvisTask task, int taskCount)
    {
        var height = HeaderHeight + 18;
        if (taskCount > 1)
            height += ChipHeight + 12;
        if (!string.IsNullOrWhiteSpace(task.ContextSummary))
            height += 22;

        var visibleSteps = VisibleSteps(task).ToArray();
        height += visibleSteps.Sum(step => StepRowHeight(step));
        if (task.Steps.Count > visibleSteps.Length)
            height += 24;

        height += PromptHeight(task);
        if (_detailsExpanded && task.Receipt.Count > 0)
            height += Math.Min(88, 22 + task.Receipt.Take(3).Count() * 18);

        height += 24;
        return Math.Clamp(height, 252, 548);
    }

    private void UpdateLayoutCache()
    {
        _buttons.Clear();
        _chipBounds.Clear();
        _detailsBounds = Rectangle.Empty;
        _closeBounds = new Rectangle(ClientSize.Width - 40, 18, 22, 22);
        _minimizeBounds = new Rectangle(ClientSize.Width - 68, 18, 22, 22);

        if (_visibleTasks.Length > 1)
        {
            var x = OuterPad;
            var y = HeaderHeight + 4;
            foreach (var task in _visibleTasks)
            {
                var label = Shorten(task.Title, 18);
                var width = Math.Min(120, Math.Max(72, TextRenderer.MeasureText(label, _tinyFont).Width + 24));
                if (x + width > ClientSize.Width - OuterPad)
                    break;
                _chipBounds[task.TaskId] = new Rectangle(x, y, width, ChipHeight);
                x += width + 8;
            }
        }

        var selected = SelectedTask;
        if (selected == null)
        {
            if (_clarificationBox != null)
                _clarificationBox.Visible = false;
            return;
        }

        var promptY = PromptTop(selected);
        if (selected.Status == JarvisTaskStatus.WaitingForClarification)
        {
            var input = new Rectangle(OuterPad + 14, promptY + 98, ClientSize.Width - (OuterPad * 2) - 104, 28);
            _clarificationBox.Bounds = input;
            _buttons.Add(new OverlayButton(
                new Rectangle(input.Right + 8, input.Top - 1, 74, 30),
                "Send",
                ButtonKind.Primary,
                SubmitClarification));
        }
        else
        {
            _clarificationBox.Visible = false;
        }

        if (selected.Status == JarvisTaskStatus.WaitingForConfirmation)
        {
            var y = promptY + 82;
            _buttons.Add(new OverlayButton(
                new Rectangle(OuterPad + 14, y, 92, 30),
                "Confirm",
                ButtonKind.Confirm,
                () => _tasks.RespondToConfirmation(selected.TaskId, true)));
            _buttons.Add(new OverlayButton(
                new Rectangle(OuterPad + 114, y, 82, 30),
                "Cancel",
                ButtonKind.Destructive,
                () => _tasks.RespondToConfirmation(selected.TaskId, false)));
        }
        else if (selected.Status == JarvisTaskStatus.Running)
        {
            var y = ClientSize.Height - 44;
            _buttons.Add(new OverlayButton(
                new Rectangle(OuterPad, y, 76, 28),
                "Pause",
                ButtonKind.Subtle,
                () => _tasks.Pause(selected.TaskId)));
            _buttons.Add(new OverlayButton(
                new Rectangle(OuterPad + 84, y, 72, 28),
                "Stop",
                ButtonKind.Destructive,
                () => _tasks.Cancel(selected.TaskId, "Task cancelled. No changes were made.")));
        }
        else if (selected.Status == JarvisTaskStatus.Paused)
        {
            var y = ClientSize.Height - 44;
            _buttons.Add(new OverlayButton(
                new Rectangle(OuterPad, y, 88, 28),
                "Resume",
                ButtonKind.Primary,
                () => _tasks.Resume(selected.TaskId)));
            _buttons.Add(new OverlayButton(
                new Rectangle(OuterPad + 96, y, 72, 28),
                "Stop",
                ButtonKind.Destructive,
                () => _tasks.Cancel(selected.TaskId, "Task cancelled. No changes were made.")));
        }
    }

    private void UpdateInteractiveControls()
    {
        UpdateLayoutCache();
        var selected = SelectedTask;
        var showInput = selected?.Status == JarvisTaskStatus.WaitingForClarification;
        _clarificationBox.Visible = showInput;
        if (showInput && Visible)
            _clarificationBox.BringToFront();
    }

    private void SubmitClarification()
    {
        var selected = SelectedTask;
        if (selected == null || selected.Status != JarvisTaskStatus.WaitingForClarification)
            return;
        var answer = _clarificationBox.Text.Trim();
        if (answer.Length == 0)
            return;
        _clarificationBox.Clear();
        _tasks.ProvideClarification(selected.TaskId, answer);
    }

    private void DrawHeader(Graphics g, JarvisTask task)
    {
        var orb = new Rectangle(OuterPad, 17, 28, 28);
        using (var glow = new PathGradientBrush(RoundedRect(orb, 14)))
        {
            glow.CenterColor = Color.FromArgb(220, 246, 246, 248);
            glow.SurroundColors = [Color.FromArgb(32, 255, 255, 255)];
            g.FillEllipse(glow, orb);
        }
        using (var orbPen = new Pen(Color.FromArgb(140, 255, 255, 255), 1))
            g.DrawEllipse(orbPen, orb);

        var titleRect = new Rectangle(OuterPad + 42, 13, ClientSize.Width - 126, 24);
        TextRenderer.DrawText(g, Shorten(task.Title, 34), _titleFont, titleRect, TextPrimary,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        var statusText = Humanize(task.Status);
        var statusColor = StatusColor(task.Status);
        var statusWidth = Math.Min(210, TextRenderer.MeasureText(statusText, _smallFont).Width + 20);
        var statusRect = new Rectangle(OuterPad + 42, 39, statusWidth, 20);
        using (var statusBrush = new SolidBrush(Color.FromArgb(26, statusColor)))
            g.FillPath(statusBrush, RoundedRect(statusRect, 10));
        using (var statusPen = new Pen(Color.FromArgb(70, statusColor), 1))
            g.DrawPath(statusPen, RoundedRect(statusRect, 10));
        TextRenderer.DrawText(g, statusText, _smallFont, statusRect, statusColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        DrawWindowGlyph(g, _minimizeBounds, "–", TextSecondary);
        DrawWindowGlyph(g, _closeBounds, "×", TextSecondary);
    }

    private void DrawTaskChips(Graphics g)
    {
        if (_visibleTasks.Length <= 1)
            return;

        foreach (var task in _visibleTasks)
        {
            if (!_chipBounds.TryGetValue(task.TaskId, out var bounds))
                continue;
            var selected = string.Equals(task.TaskId, _selectedTaskId, StringComparison.OrdinalIgnoreCase);
            using var fill = new SolidBrush(selected ? GlassStrong : Color.FromArgb(22, 255, 255, 255));
            using var pen = new Pen(selected ? Color.FromArgb(78, 255, 255, 255) : Color.FromArgb(34, 255, 255, 255), 1);
            using var chipPath = RoundedRect(bounds, 14);
            g.FillPath(fill, chipPath);
            g.DrawPath(pen, chipPath);
            TextRenderer.DrawText(g, Shorten(task.Title, 18), _tinyFont, bounds, selected ? TextPrimary : TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    private void DrawContext(Graphics g, JarvisTask task)
    {
        if (string.IsNullOrWhiteSpace(task.ContextSummary))
            return;
        var y = BodyTop(task) - 22;
        var text = ContextSummaryLabel(task.ContextSummary);
        var rect = new Rectangle(OuterPad, y, ClientSize.Width - OuterPad * 2, 18);
        TextRenderer.DrawText(g, text, _tinyFont, rect, TextTertiary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private void DrawSteps(Graphics g, JarvisTask task)
    {
        var y = BodyTop(task);
        var steps = VisibleSteps(task).ToArray();
        var lineX = OuterPad + 9;
        using var linePen = new Pen(Color.FromArgb(38, 255, 255, 255), 1);
        if (steps.Length > 1)
            g.DrawLine(linePen, lineX, y + 20, lineX, y + steps.Sum(StepRowHeight) - 20);

        foreach (var step in steps)
        {
            var height = StepRowHeight(step);
            DrawStep(g, step, new Rectangle(OuterPad, y, ClientSize.Width - OuterPad * 2, height));
            y += height;
        }

        if (task.Steps.Count > steps.Length)
        {
            var more = $"+ {task.Steps.Count - steps.Length} more planned steps";
            TextRenderer.DrawText(g, more, _tinyFont, new Rectangle(OuterPad + 34, y - 2, 220, 20), TextTertiary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private void DrawStep(Graphics g, JarvisTaskStep step, Rectangle row)
    {
        var icon = new Rectangle(row.Left, row.Top + 8, StepIconSize, StepIconSize);
        DrawStepIcon(g, icon, step.Status);

        var textX = row.Left + 34;
        var titleRect = new Rectangle(textX, row.Top + 3, row.Width - 34, 22);
        TextRenderer.DrawText(g, Shorten(step.Title, 58), _bodyFont, titleRect, TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var detail = SanitizeDetail(step.Detail);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var detailRect = new Rectangle(textX, row.Top + 25, row.Width - 34, 18);
            TextRenderer.DrawText(g, Shorten(detail, 74), _tinyFont, detailRect, TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    private void DrawStepIcon(Graphics g, Rectangle icon, JarvisTaskStepStatus status)
    {
        switch (status)
        {
            case JarvisTaskStepStatus.Running:
                using (var ring = new Pen(Color.FromArgb(46, Blue), 2f))
                    g.DrawEllipse(ring, icon);
                using (var arc = new Pen(Blue, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, icon, _spinnerFrame * 15, 105);
                break;
            case JarvisTaskStepStatus.Completed:
                using (var fill = new SolidBrush(Color.FromArgb(28, Green)))
                    g.FillEllipse(fill, icon);
                using (var pen = new Pen(Green, 1.7f))
                    g.DrawEllipse(pen, icon);
                TextRenderer.DrawText(g, "✓", _symbolFont, icon, Green,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                break;
            case JarvisTaskStepStatus.WaitingForConfirmation:
                DrawPillIcon(g, icon, Amber, "!");
                break;
            case JarvisTaskStepStatus.WaitingForClarification:
                DrawPillIcon(g, icon, Blue, "?");
                break;
            case JarvisTaskStepStatus.Failed:
                DrawPillIcon(g, icon, Red, "!");
                break;
            case JarvisTaskStepStatus.TimedOut:
                DrawPillIcon(g, icon, Amber, "!");
                break;
            case JarvisTaskStepStatus.Cancelled:
                DrawPillIcon(g, icon, Muted, "×");
                break;
            default:
                using (var pen = new Pen(Color.FromArgb(115, Muted), 1.4f))
                    g.DrawEllipse(pen, icon);
                break;
        }
    }

    private void DrawPillIcon(Graphics g, Rectangle icon, Color color, string glyph)
    {
        using (var fill = new SolidBrush(Color.FromArgb(28, color)))
            g.FillEllipse(fill, icon);
        using (var pen = new Pen(color, 1.5f))
            g.DrawEllipse(pen, icon);
        TextRenderer.DrawText(g, glyph, _symbolFont, icon, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawPromptOrSummary(Graphics g, JarvisTask task)
    {
        var y = PromptTop(task);
        if (task.Status == JarvisTaskStatus.WaitingForClarification)
        {
            DrawPromptCard(g, y, "Waiting for clarification", PromptText(task), Blue, 142);
            TextRenderer.DrawText(g, "Say the answer, or type it here.", _tinyFont,
                new Rectangle(OuterPad + 14, y + 76, ClientSize.Width - OuterPad * 2 - 28, 18),
                TextSecondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            using var inputPath = RoundedRect(new Rectangle(_clarificationBox.Left - 8, _clarificationBox.Top - 5, _clarificationBox.Width + 16, _clarificationBox.Height + 10), 13);
            using var fill = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
            using var pen = new Pen(Color.FromArgb(58, 255, 255, 255), 1);
            g.FillPath(fill, inputPath);
            g.DrawPath(pen, inputPath);
        }
        else if (task.Status == JarvisTaskStatus.WaitingForConfirmation)
        {
            DrawPromptCard(g, y, "Confirmation required", PromptText(task), Amber, 118);
        }
        else if (task.Status is JarvisTaskStatus.Completed or JarvisTaskStatus.Cancelled or JarvisTaskStatus.Failed or JarvisTaskStatus.TimedOut)
        {
            DrawSummaryCard(g, y, FinalSummary(task), StatusColor(task.Status));
        }

        if (task.Receipt.Count > 0
            && task.Status is (JarvisTaskStatus.Completed or JarvisTaskStatus.Cancelled or JarvisTaskStatus.Failed or JarvisTaskStatus.TimedOut))
            DrawDetailsDisclosure(g, task, y + 56);
    }

    private void DrawPromptCard(Graphics g, int y, string title, string body, Color accent, int height)
    {
        var rect = new Rectangle(OuterPad, y, ClientSize.Width - OuterPad * 2, height);
        using var path = RoundedRect(rect, 18);
        using var fill = new SolidBrush(Color.FromArgb(28, 255, 255, 255));
        using var pen = new Pen(Color.FromArgb(54, accent), 1);
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
        TextRenderer.DrawText(g, title, _smallFont, new Rectangle(rect.Left + 14, rect.Top + 10, rect.Width - 28, 20),
            accent, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Shorten(body, 140), _tinyFont, new Rectangle(rect.Left + 14, rect.Top + 29, rect.Width - 28, Math.Max(34, height - 88)),
            TextPrimary, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    private void DrawSummaryCard(Graphics g, int y, string summary, Color accent)
    {
        var rect = new Rectangle(OuterPad, y, ClientSize.Width - OuterPad * 2, 52);
        using var path = RoundedRect(rect, 18);
        using var fill = new SolidBrush(Color.FromArgb(28, 255, 255, 255));
        using var pen = new Pen(Color.FromArgb(42, accent), 1);
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
        DrawStepIcon(g, new Rectangle(rect.Left + 14, rect.Top + 17, 18, 18), taskStatusToStepStatus(accent));
        TextRenderer.DrawText(g, Shorten(summary, 104), _bodyFont, new Rectangle(rect.Left + 42, rect.Top + 8, rect.Width - 56, 36),
            TextPrimary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        static JarvisTaskStepStatus taskStatusToStepStatus(Color color)
        {
            if (color.ToArgb() == Green.ToArgb())
                return JarvisTaskStepStatus.Completed;
            if (color.ToArgb() == Red.ToArgb())
                return JarvisTaskStepStatus.Failed;
            return JarvisTaskStepStatus.Cancelled;
        }
    }

    private void DrawDetailsDisclosure(Graphics g, JarvisTask task, int y)
    {
        _detailsBounds = new Rectangle(OuterPad, y + 4, 96, 22);
        TextRenderer.DrawText(g, _detailsExpanded ? "Hide details" : "View details", _tinyFont, _detailsBounds, TextTertiary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (!_detailsExpanded)
            return;

        var details = task.Receipt
            .Select(SanitizeDetail)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(3)
            .ToArray();
        var rect = new Rectangle(OuterPad, y + 30, ClientSize.Width - OuterPad * 2, Math.Max(24, details.Length * 18 + 12));
        using var path = RoundedRect(rect, 14);
        using var fill = new SolidBrush(Color.FromArgb(22, 255, 255, 255));
        g.FillPath(fill, path);
        var lineY = rect.Top + 7;
        foreach (var detail in details)
        {
            TextRenderer.DrawText(g, "• " + Shorten(detail, 76), _tinyFont, new Rectangle(rect.Left + 12, lineY, rect.Width - 24, 18),
                TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            lineY += 18;
        }
    }

    private void DrawButtons(Graphics g)
    {
        foreach (var button in _buttons)
        {
            var (fillColor, borderColor, textColor) = button.Kind switch
            {
                ButtonKind.Primary => (Color.FromArgb(54, 82, 120), Color.FromArgb(94, 132, 184), Color.White),
                ButtonKind.Confirm => (Color.FromArgb(34, 98, 58), Color.FromArgb(68, 156, 96), Color.White),
                ButtonKind.Destructive => (Color.FromArgb(82, 42, 44), Color.FromArgb(150, 66, 70), Color.White),
                _ => (Color.FromArgb(34, 255, 255, 255), Color.FromArgb(60, 255, 255, 255), TextPrimary),
            };
            using var path = RoundedRect(button.Bounds, 14);
            using var fill = new SolidBrush(fillColor);
            using var pen = new Pen(borderColor, 1);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            TextRenderer.DrawText(g, button.Text, _buttonFont, button.Bounds, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    private void DrawWindowGlyph(Graphics g, Rectangle bounds, string glyph, Color color)
    {
        using var path = RoundedRect(bounds, bounds.Height / 2);
        using var fill = new SolidBrush(Color.FromArgb(0, 255, 255, 255));
        g.FillPath(fill, path);
        TextRenderer.DrawText(g, glyph, _bodyFont, bounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private int BodyTop(JarvisTask task)
    {
        var top = HeaderHeight + 14;
        if (_visibleTasks.Length > 1)
            top += ChipHeight + 12;
        if (!string.IsNullOrWhiteSpace(task.ContextSummary))
            top += 22;
        return top;
    }

    private int PromptTop(JarvisTask task)
    {
        var top = BodyTop(task);
        var steps = VisibleSteps(task).ToArray();
        top += steps.Sum(StepRowHeight);
        if (task.Steps.Count > steps.Length)
            top += 24;
        return top + 8;
    }

    private int PromptHeight(JarvisTask task) => task.Status switch
    {
        JarvisTaskStatus.WaitingForClarification => 148,
        JarvisTaskStatus.WaitingForConfirmation => 132,
        JarvisTaskStatus.Completed or JarvisTaskStatus.Cancelled or JarvisTaskStatus.Failed or JarvisTaskStatus.TimedOut => 82,
        JarvisTaskStatus.Running or JarvisTaskStatus.Paused => 52,
        _ => 26,
    };

    private static IEnumerable<JarvisTaskStep> VisibleSteps(JarvisTask task)
    {
        if (task.Steps.Count <= MaxVisibleSteps)
            return task.Steps;

        var activeIndex = task.Steps.FindIndex(step => step.Status is JarvisTaskStepStatus.Running
            or JarvisTaskStepStatus.WaitingForConfirmation
            or JarvisTaskStepStatus.WaitingForClarification
            or JarvisTaskStepStatus.Failed);
        if (activeIndex < 0)
            activeIndex = Math.Min(task.Steps.Count - 1, MaxVisibleSteps - 1);
        var start = Math.Clamp(activeIndex - 2, 0, Math.Max(0, task.Steps.Count - MaxVisibleSteps));
        return task.Steps.Skip(start).Take(MaxVisibleSteps);
    }

    private static int StepRowHeight(JarvisTaskStep step) =>
        string.IsNullOrWhiteSpace(SanitizeDetail(step.Detail)) ? 36 : 52;

    private void PositionPanel()
    {
        var screen = Screen.FromPoint(Cursor.Position)?.WorkingArea
            ?? Screen.PrimaryScreen?.WorkingArea
            ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(screen.Right - Width - 22, screen.Bottom - Height - 22);
    }

    private void EnsureTopMost()
    {
        if (!IsHandleCreated)
            return;
        TopMost = true;
        SetWindowPos(
            Handle,
            new IntPtr(-1),
            0,
            0,
            0,
            0,
            0x0001 | 0x0002 | 0x0010 | 0x0040); // NOSIZE | NOMOVE | NOACTIVATE | SHOWWINDOW
    }

    private void ApplyRoundedRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;
        using var path = RoundedRect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), CornerRadius);
        Region?.Dispose();
        Region = new Region(path);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static bool IsVisibleTask(JarvisTask task)
    {
        if (task == null)
            return false;
        if (IsActive(task.Status))
            return true;
        if (task.Status is not (JarvisTaskStatus.Completed or JarvisTaskStatus.Failed or JarvisTaskStatus.Cancelled or JarvisTaskStatus.TimedOut))
            return false;
        var finishedAt = task.CompletedAt ?? task.UpdatedAt;
        return DateTimeOffset.UtcNow - finishedAt <= TerminalTaskVisibleFor;
    }

    private static bool IsActive(JarvisTaskStatus status) =>
        status is JarvisTaskStatus.Pending
            or JarvisTaskStatus.Running
            or JarvisTaskStatus.Paused
            or JarvisTaskStatus.WaitingForConfirmation
            or JarvisTaskStatus.WaitingForClarification;

    private static string Humanize(JarvisTaskStatus status) => status switch
    {
        JarvisTaskStatus.WaitingForConfirmation => "Waiting for confirmation",
        JarvisTaskStatus.WaitingForClarification => "Waiting for clarification",
        _ => status.ToString(),
    };

    private static Color StatusColor(JarvisTaskStatus status) => status switch
    {
        JarvisTaskStatus.Completed => Green,
        JarvisTaskStatus.Failed => Red,
        JarvisTaskStatus.TimedOut => Amber,
        JarvisTaskStatus.WaitingForConfirmation => Amber,
        JarvisTaskStatus.WaitingForClarification => Blue,
        JarvisTaskStatus.Cancelled => Muted,
        _ => TextSecondary,
    };

    private static string PromptText(JarvisTask task)
    {
        var prompt = SanitizeDetail(task.PendingPrompt);
        if (task.Status == JarvisTaskStatus.WaitingForClarification && task.PendingOptions.Count > 0)
            prompt = $"{prompt} Options: {string.Join(", ", task.PendingOptions.Take(4).Select(SanitizeDetail))}";
        return string.IsNullOrWhiteSpace(prompt)
            ? task.Status == JarvisTaskStatus.WaitingForConfirmation
                ? "This step needs your approval before Jarvis continues."
                : "Jarvis needs one detail before continuing."
            : prompt;
    }

    private static string FinalSummary(JarvisTask task)
    {
        var result = SanitizeDetail(task.Result);
        if (task.Status == JarvisTaskStatus.Completed)
            return string.IsNullOrWhiteSpace(result) ? "Task completed." : result;
        if (task.Status == JarvisTaskStatus.TimedOut)
            return string.IsNullOrWhiteSpace(result) ? "Task timed out. No final action was taken." : result;
        if (task.Status == JarvisTaskStatus.Cancelled)
            return "Task cancelled. No changes were made.";
        return string.IsNullOrWhiteSpace(result) ? "Task stopped. Jarvis could not complete this step." : result;
    }

    private static string ContextSummaryLabel(string value)
    {
        var normalized = FuzzyMatcher.Normalize(value);
        if (normalized.Contains("selectedtext", StringComparison.Ordinal) || normalized.Contains("selected text", StringComparison.Ordinal))
            return "✓ Captured selected text as task context";
        if (normalized.Contains("clipboard", StringComparison.Ordinal))
            return "✓ Captured clipboard text as task context";
        return "✓ Context captured for this task";
    }

    private static string SanitizeDetail(string value)
    {
        var clean = string.Join(
            " ",
            (value ?? "").ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (clean.Length == 0)
            return "";

        clean = System.Text.RegularExpressions.Regex.Replace(
            clean,
            @"https?://(?:localhost|127\.0\.0\.1|\[::1\])(?::\d+)?[^\s,.;)]*",
            "the local preview page",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        clean = System.Text.RegularExpressions.Regex.Replace(
            clean,
            @"https?://[^\s,.;)]*",
            "the page",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        clean = System.Text.RegularExpressions.Regex.Replace(
            clean,
            @"\bcontrolled\s+(msedge|chrome|edge)\s+session\b",
            "browser",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        clean = clean.Replace("Browser task started and the first page was inspected.", "Browser task started.");
        return clean;
    }

    private static string Shorten(string value, int maxLength)
    {
        var clean = string.Join(
            " ",
            (value ?? "").ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= maxLength ? clean : clean[..Math.Max(0, maxLength - 1)].Trim() + "…";
    }

    private sealed class OverlayButton
    {
        public OverlayButton(Rectangle bounds, string text, ButtonKind kind, Action click)
        {
            Bounds = bounds;
            Text = text;
            Kind = kind;
            Click = click;
        }

        public Rectangle Bounds { get; }
        public string Text { get; }
        public ButtonKind Kind { get; }
        public Action Click { get; }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    private enum ButtonKind
    {
        Primary,
        Confirm,
        Destructive,
        Subtle,
    }
}
