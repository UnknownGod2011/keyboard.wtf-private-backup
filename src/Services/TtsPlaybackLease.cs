namespace KeyboardWtf.Services;

/// <summary>
/// A tiny ownership lease for asynchronous speech playback. A stale cancelled
/// request must never clear or stop the request that followed it.
/// </summary>
internal sealed class TtsPlaybackLease
{
    private readonly object _sync = new();
    private long _next;
    private long _active;

    public long Begin()
    {
        lock (_sync)
        {
            _active = ++_next;
            return _active;
        }
    }

    public bool IsCurrent(long lease)
    {
        lock (_sync)
            return lease != 0 && _active == lease;
    }

    public bool Complete(long lease)
    {
        lock (_sync)
        {
            if (lease == 0 || _active != lease)
                return false;
            _active = 0;
            return true;
        }
    }

    public long CancelCurrent()
    {
        lock (_sync)
        {
            var cancelled = _active;
            _active = 0;
            return cancelled;
        }
    }
}
