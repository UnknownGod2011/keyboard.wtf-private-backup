namespace KeyboardWtf.Services;

using NAudio.Wave;
using KeyboardWtf.Models;

public static class GuitarTunerService
{
    public static bool     IsActive       { get; private set; }
    public static string   Note           { get; private set; } = "---";
    public static float    CentsOff       { get; private set; }
    public static float    Frequency      { get; private set; }
    public static DateTime InTuneHoldUntil { get; private set; } = DateTime.MinValue;

    public static event Action StateChanged;

    private static WaveInEvent _waveIn;
    private static System.Threading.Timer _refreshTimer;
    private static readonly List<float> _buf  = new();
    private static readonly object      _lock = new();

    private const int    SampleRate      = 16_000;
    private const int    WindowSamples   = 4_000;   // 250 ms
    private const float  MinRms          = 0.015f;
    private const double InTuneThreshold = 8.0;     // cents
    private const double InTuneHoldSecs  = 2.5;     // green hold after string rings out

    public static bool Start()
    {
        if (IsActive) return true;
        if (KeyboardWtfState.IsRecording)
        {
            AppLog.Warning("GuitarTuner: cannot start while recording is active");
            return false;
        }

        try
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat         = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 80,
                DeviceNumber       = AudioRecorderService.ResolveDeviceNumber(
                    KeyboardWtfState.SelectedMicrophoneIndex)
            };
            _waveIn.DataAvailable += OnData;
            _waveIn.StartRecording();

            _refreshTimer = new System.Threading.Timer(_ => StateChanged?.Invoke(), null, 100, 100);

            IsActive = true;
            StateChanged?.Invoke();
            AppLog.Info("GuitarTuner: started");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"GuitarTuner: WaveIn failed: {ex.Message}");
            _waveIn?.Dispose();
            _waveIn = null;
            return false;
        }
    }

    public static void Stop()
    {
        if (!IsActive) return;

        _refreshTimer?.Dispose();
        _refreshTimer = null;
        IsActive       = false;
        Note           = "---";
        CentsOff       = 0;
        Frequency      = 0;
        InTuneHoldUntil = DateTime.MinValue;

        try { _waveIn?.StopRecording(); _waveIn?.Dispose(); } catch { }
        _waveIn = null;

        lock (_lock) _buf.Clear();
        StateChanged?.Invoke();
        AppLog.Info("GuitarTuner: stopped");
    }

    private static void OnData(object sender, WaveInEventArgs e)
    {
        int count = e.BytesRecorded / 2;
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
                _buf.Add(BitConverter.ToInt16(e.Buffer, i * 2) / 32768f);

            if (_buf.Count > SampleRate)
                _buf.RemoveRange(0, _buf.Count - SampleRate);
        }

        if (_buf.Count < WindowSamples) return;

        float[] window;
        lock (_lock)
        {
            var start = _buf.Count - WindowSamples;
            window = _buf.GetRange(start, WindowSamples).ToArray();
        }

        ProcessPitch(window);
    }

    private static void ProcessPitch(float[] buf)
    {
        // Silence gate
        float sum = 0;
        foreach (var s in buf) sum += s * s;
        if (MathF.Sqrt(sum / buf.Length) < MinRms)
        {
            Note = "---"; CentsOff = 0; Frequency = 0;
            return;
        }

        var freq = Autocorrelate(buf);

        if (freq < 60 || freq > 420)
        {
            Note = "---"; CentsOff = 0; Frequency = 0;
            return;
        }

        Frequency = freq;

        double midiExact   = 12.0 * Math.Log2(freq / 440.0) + 69.0;
        int    midiNearest = (int)Math.Round(midiExact);
        CentsOff = (float)((midiExact - midiNearest) * 100.0);

        string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        int    octave  = midiNearest / 12 - 1;
        string name    = names[((midiNearest % 12) + 12) % 12];
        Note = $"{name}{octave}";

        // Extend the green hold every time we are in tune
        if (Math.Abs(CentsOff) <= InTuneThreshold)
            InTuneHoldUntil = DateTime.UtcNow.AddSeconds(InTuneHoldSecs);
    }

    // Normalized autocorrelation: fundamental in guitar range 60–420 Hz
    private static float Autocorrelate(float[] buf)
    {
        int minPeriod = SampleRate / 420;
        int maxPeriod = SampleRate / 60;
        int n         = buf.Length;

        double bestCorr   = 0;
        int    bestPeriod = 0;

        for (int period = minPeriod; period <= maxPeriod; period++)
        {
            double corr = 0;
            int    len  = n - period;
            for (int i = 0; i < len; i++)
                corr += buf[i] * (double)buf[i + period];
            corr /= len;

            if (corr > bestCorr) { bestCorr = corr; bestPeriod = period; }
        }

        return (bestPeriod > 0 && bestCorr > 0.02) ? (float)SampleRate / bestPeriod : 0;
    }
}
