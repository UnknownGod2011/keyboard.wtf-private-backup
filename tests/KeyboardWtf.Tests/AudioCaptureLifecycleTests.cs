using System.Reflection;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using NAudio.Wave;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class AudioCaptureLifecycleTests
{
    [Theory]
    [InlineData(RecordingMode.Dictate, true)]
    [InlineData(RecordingMode.VoiceAssistant, true)]
    [InlineData(RecordingMode.Command, false)]
    [InlineData(RecordingMode.QuickSend, false)]
    [InlineData(RecordingMode.JarvisPreflight, false)]
    public void Capture_failure_may_recover_writing_but_never_execute_partial_commands(RecordingMode mode, bool expected) =>
        Assert.Equal(expected, VoiceCaptureService.CanDeliverInterruptedRecording(mode));

    [Fact]
    public void Every_valid_pcm16_sample_including_negative_full_scale_has_a_valid_peak()
    {
        for (var value = (int)short.MinValue; value <= short.MaxValue; value++)
        {
            var pcm = BitConverter.GetBytes((short)value);
            Assert.Equal(Math.Abs((int)value), AudioRecorderService.Pcm16Peak(pcm));
        }
        Assert.Equal(0, AudioRecorderService.Pcm16Peak([]));
        Assert.Equal(0, AudioRecorderService.Pcm16Peak([255]));
    }

    [Fact]
    public async Task Ten_minute_capture_preserves_all_callbacks_and_the_final_drain_buffer_exactly_once()
    {
        var oldGate = KeyboardWtfState.UseNoiseGate;
        KeyboardWtfState.UseNoiseGate = false;
        try
        {
            var input = new FakeInput();
            using var recorder = new AudioRecorderService(_ => input);
            recorder.StartRecording();
            using var expected = new MemoryStream();
            var block = new byte[1600]; // 50ms at the real 16kHz mono capture format.
            for (var index = 0; index < 610 * 20; index++)
            {
                Random.Shared.NextBytes(block);
                block[0] = 0; block[1] = 128; // Full-scale negative samples every callback.
                expected.Write(block);
                input.Emit(block);
            }
            var result = recorder.StopRecordingAsync();
            Assert.False(result.IsCompleted);
            Assert.Same(result, recorder.StopRecordingAsync());
            Assert.False(input.Disposed);
            byte[] tail = [11, 0, 22, 0, 0, 128];
            expected.Write(tail);
            input.Emit(tail);
            input.Finish();
            using var reader = new WaveFileReader(new MemoryStream(await result));
            var actual = new byte[reader.Length];
            Assert.Equal(actual.Length, reader.Read(actual, 0, actual.Length));
            Assert.Equal(expected.ToArray(), actual);
            Assert.Null(recorder.LastRecordingError);
            Assert.True(input.Disposed);
            Assert.False(recorder.IsRecording);
            Assert.Equal(1, input.StopCalls);
        }
        finally { KeyboardWtfState.UseNoiseGate = oldGate; }
    }

    [Fact]
    public async Task Input_failure_reaches_existing_completion_owner_and_recovers_captured_audio_once()
    {
        var input = new FakeInput();
        using var recorder = new AudioRecorderService(_ => input);
        var capture = CreateCapture(recorder);
        var delivered = 0;
        byte[] recovered = null;
        Set(capture, "_audioCompletionHandler", (Func<SmartWritingAudioCapture, Task>)(audio =>
        {
            delivered++;
            recovered = audio.WavData;
            KeyboardWtfState.SetUi(VoiceUiPhase.Done, "Recovered", "Captured speech retained");
            return Task.CompletedTask;
        }));
        recorder.StartRecording();
        input.Emit(new byte[32000]);
        input.Finish(new IOException("Device disconnected"));
        Assert.NotNull(recorder.LastRecordingError);
        await Invoke(capture, "MonitorSilenceAsync", RecordingMode.VoiceAssistant, CancellationToken.None);
        Assert.Equal(1, delivered);
        using var reader = new WaveFileReader(new MemoryStream(recovered));
        Assert.Equal(32000, reader.Length);
        Assert.False(KeyboardWtfState.IsRecording);
        Assert.False(KeyboardWtfState.IsProcessing);
        Assert.True(input.Disposed);
        capture.Cancel();
    }

    [Fact]
    public async Task Cancellation_while_draining_does_not_deliver_late_audio_or_leave_processing_state()
    {
        var input = new FakeInput();
        using var recorder = new AudioRecorderService(_ => input);
        var capture = CreateCapture(recorder);
        var delivered = 0;
        Set(capture, "_audioCompletionHandler", (Func<SmartWritingAudioCapture, Task>)(_ =>
        {
            delivered++;
            return Task.CompletedTask;
        }));
        recorder.StartRecording();
        input.Emit(new byte[1600]);
        var completion = Invoke(capture, "CompleteRecordingAsync");
        Assert.False(completion.IsCompleted);
        capture.Cancel();
        input.Emit(new byte[1600]);
        input.Finish();
        await completion;
        Assert.Equal(0, delivered);
        Assert.False(KeyboardWtfState.IsRecording);
        Assert.False(KeyboardWtfState.IsProcessing);
        Assert.Equal(RecordingMode.None, KeyboardWtfState.CurrentRecordingMode);
        Assert.True(input.Disposed);
    }

    [Fact]
    public async Task Cancellation_during_interrupted_command_drain_keeps_the_cancelled_terminal_state()
    {
        var input = new FakeInput();
        using var recorder = new AudioRecorderService(_ => input);
        var capture = CreateCapture(recorder);
        KeyboardWtfState.CurrentRecordingMode = RecordingMode.Command;
        recorder.StartRecording();
        input.Emit(new byte[1600]);
        var completion = Invoke(capture, "CompleteRecordingAsync");
        Assert.False(completion.IsCompleted);
        capture.Cancel();
        input.Finish(new IOException("Input disconnected while stopping"));
        await completion;
        Assert.Equal(VoiceUiPhase.Cancelled, KeyboardWtfState.UiPhase);
        Assert.False(KeyboardWtfState.IsProcessing);
    }

    [Fact]
    public async Task Empty_final_recognition_preserves_known_raw_preview_without_executing_partial_commands()
    {
        var oldTranscript = KeyboardWtfState.CurrentTranscript;
        try
        {
            foreach (var mode in new[] { RecordingMode.Dictate, RecordingMode.Command })
            {
                var input = new FakeInput();
                using var recorder = new AudioRecorderService(_ => input);
                // Recognition providers already return empty on failure/unavailability.
                // Exercise that real facade contract instead of throwing from a fake.
                using var recognition = new SpeechRecognitionService();
                var capture = new VoiceCaptureService(recorder, recognition, new NotificationService(null), null);
                KeyboardWtfState.IsRecording = true;
                KeyboardWtfState.IsProcessing = false;
                KeyboardWtfState.CurrentRecordingMode = mode;
                Set(capture, "_recordingStartUtc", DateTime.UtcNow);
                Set(capture, "_lastPreview", "First sentence. Second sentence. Last captured words.");
                recorder.StartRecording();
                input.Emit(new byte[32000]);
                var result = capture.StopAndTranscribeAsync(preferFast: true);
                input.Finish();
                var transcript = await result;
                Assert.Equal(mode == RecordingMode.Dictate
                    ? "First sentence. Second sentence. Last captured words." : "", transcript);
                Assert.False(KeyboardWtfState.IsRecording);
                Assert.False(KeyboardWtfState.IsProcessing);
                Assert.Equal(RecordingMode.None, KeyboardWtfState.CurrentRecordingMode);
                Assert.True(input.Disposed);
                capture.Cancel();
            }
        }
        finally { KeyboardWtfState.CurrentTranscript = oldTranscript; }
    }

    [Fact]
    public async Task Old_input_callbacks_cannot_contaminate_a_restarted_capture()
    {
        var first = new FakeInput();
        var second = new FakeInput();
        var input = first;
        using var recorder = new AudioRecorderService(_ => input);
        recorder.StartRecording();
        var staleData = first.SnapshotDataHandler();
        first.Finish();
        await recorder.StopRecordingAsync();
        input = second;
        recorder.StartRecording();
        staleData(first, new WaveInEventArgs(new byte[4000], 4000));
        second.Emit(new byte[2000]);
        second.Finish();
        using var reader = new WaveFileReader(new MemoryStream(await recorder.StopRecordingAsync()));
        Assert.Equal(2000, reader.Length);
    }

    private static VoiceCaptureService CreateCapture(AudioRecorderService recorder)
    {
        var service = new VoiceCaptureService(recorder, null, new NotificationService(null), null);
        KeyboardWtfState.IsRecording = true;
        KeyboardWtfState.IsProcessing = false;
        KeyboardWtfState.CurrentRecordingMode = RecordingMode.VoiceAssistant;
        Set(service, "_recordingStartUtc", DateTime.UtcNow);
        return service;
    }

    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static Task Invoke(object target, string method, params object[] args) => (Task)target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;

    private sealed class FakeInput : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 1);
        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;
        public int StopCalls { get; private set; }
        public bool Disposed { get; private set; }
        public void StartRecording() { }
        public void StopRecording() => StopCalls++;
        public void Dispose() => Disposed = true;
        public void Emit(byte[] data) => DataAvailable?.Invoke(this, new(data, data.Length));
        public void Finish(Exception error = null) => RecordingStopped?.Invoke(this, new(error));
        public EventHandler<WaveInEventArgs> SnapshotDataHandler() => DataAvailable;
    }
}
