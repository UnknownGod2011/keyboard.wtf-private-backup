namespace KeyboardWtf.Services;

using KeyboardWtf.Models;

public enum JarvisTimerState
{
    Running,
    Completed,
    Cancelled,
}

public sealed class JarvisTimerSnapshot
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset DueAt { get; init; }
    public JarvisTimerState State { get; init; }
    public TimeSpan Remaining =>
        State == JarvisTimerState.Running
            ? TimeSpan.FromMilliseconds(Math.Max(0, (DueAt - DateTimeOffset.Now).TotalMilliseconds))
            : TimeSpan.Zero;
}

public sealed class JarvisTimerService : IDisposable
{
    private sealed class Runtime
    {
        public JarvisTimerSnapshot Snapshot { get; init; }
        public System.Threading.Timer Timer { get; init; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Runtime> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, string> _notify;
    private readonly Action<string> _speak;
    private readonly Action<string, string, bool> _history;
    private bool _disposed;

    public JarvisTimerService(
        NotificationService notifications,
        JarvisActionHistoryService history)
        : this(
            (title, message) => notifications.Info(title, message),
            // Timer lifecycle messages are deterministic English system text.
            // Do not use "auto", which can retain a previously selected SAPI
            // voice from another language.
            message => TtsService.Instance.Speak(message, "en"),
            (kind, detail, ok) => history.Add(kind, detail, ok))
    {
    }

    internal JarvisTimerService(
        Action<string, string> notify,
        Action<string> speak,
        Action<string, string, bool> history)
    {
        _notify = notify ?? ((_, _) => { });
        _speak = speak ?? (_ => { });
        _history = history ?? ((_, _, _) => { });
    }

    public event Action<JarvisTimerSnapshot> Changed;
    public event Action<JarvisLifecycleAnnouncement> AnnouncementRequested;

    public IReadOnlyList<JarvisTimerSnapshot> ActiveTimers
    {
        get
        {
            lock (_sync)
            {
                return _active.Values
                    .Select(runtime => runtime.Snapshot)
                    .OrderBy(snapshot => snapshot.DueAt)
                    .ToArray();
            }
        }
    }

    public JarvisTimerSnapshot Start(TimeSpan duration, string label)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        duration = TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds, 1, 24 * 60 * 60));
        var now = DateTimeOffset.Now;
        var snapshot = new JarvisTimerSnapshot
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = string.IsNullOrWhiteSpace(label)
                ? HumanDuration(duration) + " timer"
                : Limit(label.Trim(), 80),
            StartedAt = now,
            DueAt = now + duration,
            State = JarvisTimerState.Running,
        };

        var timer = new System.Threading.Timer(
            _ => Complete(snapshot.Id),
            null,
            duration,
            Timeout.InfiniteTimeSpan);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active[snapshot.Id] = new Runtime
            {
                Snapshot = snapshot,
                Timer = timer,
            };
        }
        _history("timer_started", snapshot.Label, true);
        Changed?.Invoke(snapshot);
        return snapshot;
    }

    public int Cancel(string query = "")
    {
        Runtime[] cancelled;
        lock (_sync)
        {
            var normalized = (query ?? "").Trim();
            var matches = normalized.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? _active.Values.ToArray()
                : string.IsNullOrWhiteSpace(normalized)
                    ? _active.Values
                        .OrderByDescending(runtime => runtime.Snapshot.StartedAt)
                        .Take(1)
                        .ToArray()
                    : _active.Values
                        .Where(runtime =>
                            runtime.Snapshot.Id.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                            || runtime.Snapshot.Label.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
            cancelled = matches
                .Where(runtime => _active.Remove(runtime.Snapshot.Id))
                .ToArray();
        }

        foreach (var runtime in cancelled)
        {
            runtime.Timer.Dispose();
            var snapshot = Copy(runtime.Snapshot, JarvisTimerState.Cancelled);
            _history("timer_cancelled", snapshot.Label, true);
            Changed?.Invoke(snapshot);
        }
        if (cancelled.Length > 0)
        {
            RequestAnnouncement(new JarvisLifecycleAnnouncement
            {
                Source = "timer",
                State = "cancelled",
                Detail = cancelled.Length == 1
                    ? $"{cancelled[0].Snapshot.Label} was cancelled."
                    : $"{cancelled.Length} timers were cancelled.",
                FallbackSpeech = cancelled.Length == 1
                    ? "Timer cancelled."
                    : "Timers cancelled.",
            });
        }
        return cancelled.Length;
    }

    private void Complete(string id)
    {
        Runtime runtime;
        lock (_sync)
        {
            if (!_active.Remove(id, out runtime))
                return;
        }

        runtime.Timer.Dispose();
        var snapshot = Copy(runtime.Snapshot, JarvisTimerState.Completed);
        _notify("Jarvis timer", snapshot.Label);
        _history("timer_finished", snapshot.Label, true);
        Changed?.Invoke(snapshot);
        RequestAnnouncement(new JarvisLifecycleAnnouncement
        {
            Source = "timer",
            State = "completed",
            Detail = $"{snapshot.Label} finished.",
            FallbackSpeech = $"{snapshot.Label} finished.",
        });
    }

    private void RequestAnnouncement(JarvisLifecycleAnnouncement announcement)
    {
        var handler = AnnouncementRequested;
        if (handler == null)
        {
            _speak(announcement.FallbackSpeech);
            return;
        }

        try
        {
            handler(announcement);
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Timer lifecycle announcement handler failed");
            _speak(announcement.FallbackSpeech);
        }
    }

    public void Dispose()
    {
        Runtime[] active;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            active = _active.Values.ToArray();
            _active.Clear();
        }
        foreach (var runtime in active)
            runtime.Timer.Dispose();
    }

    private static JarvisTimerSnapshot Copy(
        JarvisTimerSnapshot source,
        JarvisTimerState state) => new()
    {
        Id = source.Id,
        Label = source.Label,
        StartedAt = source.StartedAt,
        DueAt = source.DueAt,
        State = state,
    };

    private static string HumanDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1 && duration.TotalSeconds % 60 == 0
            ? $"{duration.TotalMinutes:0} minute"
            : $"{duration.TotalSeconds:0} second";

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}
