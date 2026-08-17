namespace KeyboardWtf.Services;

using KeyboardWtf.Models;
using NAudio.Wave;

// MME gives 50ms callbacks with no exclusive microphone hold. WASAPI exclusive mode would
// starve other apps, while shared mode adds latency and extra COM setup.
// Future: WASAPI loopback (output capture) would enable "transcribe what's playing" mode.
public sealed class AudioRecorderService : IDisposable
{
    // WinMM's WAVE_MAPPER value. NAudio forwards DeviceNumber directly to
    // waveInOpen, so -1 follows the current Windows default input instead of
    // pinning first-run users to whatever happens to be device zero.
    internal const int SystemDefaultDeviceNumber = -1;

    private WaveInEvent _waveIn;
    private MemoryStream _pcmStream;
    private WaveFormat _waveFormat;
    private bool _disposed;

    public bool IsRecording { get; private set; }
    public event Action<byte[], int> PcmDataAvailable;

    // volatile: DataAvailable writes on the NAudio callback thread; meters read on timer threads.
    // No lock needed — a torn int read is acceptable for a level display.
    private volatile int _currentPeak;
    public int CurrentPeak => _currentPeak;
    public double CurrentPeakDb =>
        _currentPeak > 0 ? 20.0 * Math.Log10(_currentPeak / 32768.0) : -96.0;

    public void StartRecording(int sampleRate = 16000)
    {
        if (IsRecording)
            return;

        _waveFormat = new WaveFormat(sampleRate: sampleRate, channels: 1);
        _pcmStream = new MemoryStream();
        var micIndex = ResolveDeviceNumber(KeyboardWtfState.SelectedMicrophoneIndex);
        _waveIn = new WaveInEvent
        {
            DeviceNumber = micIndex,
            WaveFormat = _waveFormat,
            BufferMilliseconds = 50 // 50ms: responsive enough for live meter, large enough to avoid callback thrash
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        try
        {
            _waveIn.StartRecording();
        }
        catch
        {
            CleanupRecordingResources();
            throw;
        }

        _currentPeak = 0;
        IsRecording = true;
    }

    internal static int ResolveDeviceNumber(int selectedIndex) =>
        selectedIndex >= 0 ? selectedIndex : SystemDefaultDeviceNumber;

    public byte[] StopRecording()
    {
        if (!IsRecording)
            return Array.Empty<byte>();

        IsRecording = false;
        _waveIn.StopRecording();

        // WaveFileWriter.Dispose() finalizes the RIFF header (writes the byte-count fields at
        // offset 4 and 40). Without it, the WAV is malformed and Whisper rejects it. Calling
        // ToArray() after Dispose() on a MemoryStream is safe — it returns the full internal buffer.
        var pcmData = _pcmStream.ToArray();
        if (KeyboardWtfState.UseNoiseGate)
            pcmData = NoiseGateFilter.Apply(pcmData, _waveFormat.SampleRate);
        var wavStream = new MemoryStream();
        var writer = new WaveFileWriter(wavStream, _waveFormat);
        writer.Write(pcmData, 0, pcmData.Length);
        writer.Dispose();
        var wavBytes = wavStream.ToArray();


        CleanupRecordingResources();

        return wavBytes;
    }

    private void OnDataAvailable(object sender, WaveInEventArgs e)
    {
        _pcmStream?.Write(e.Buffer, 0, e.BytesRecorded);
        PcmDataAvailable?.Invoke(e.Buffer, e.BytesRecorded);

        // Compute peak for this buffer (16-bit signed samples)
        var peak = 0;
        for (var i = 0; i < e.BytesRecorded - 1; i += 2)
        {
            var sample = Math.Abs((short)(e.Buffer[i] | (e.Buffer[i + 1] << 8)));
            if (sample > peak) peak = sample;
        }
        _currentPeak = peak;
        KeyboardWtfState.InputLevelDb = CurrentPeakDb;
    }

    private void OnRecordingStopped(object sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            AppLog.Error($"Recording error: {e.Exception.Message}");
        }
    }

    private void CleanupRecordingResources()
    {
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }

        _pcmStream?.Dispose();
        _pcmStream = null;
        KeyboardWtfState.InputLevelDb = -96;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (IsRecording)
            StopRecording();
        else
            CleanupRecordingResources();
    }
}
