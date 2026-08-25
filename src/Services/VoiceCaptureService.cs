namespace KeyboardWtf.Services;

using System.Diagnostics;
using NAudio.Wave;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;
using KeyboardWtf.Services.Ai;

public sealed class VoiceCaptureService
{
    private readonly AudioRecorderService _audioRecorder;
    private readonly SpeechRecognitionService _speechRecognition;
    private readonly NotificationService _notifications;
    private readonly DestinationRouter _router;

    private DateTime _recordingStartUtc;
    private CancellationTokenSource _autoStopCts;
    private CancellationTokenSource _silenceCts;
    private CancellationTokenSource _processingCts;
    private Func<string, Task> _completionHandler;
    private Func<SmartWritingAudioCapture, Task> _audioCompletionHandler;
    private bool _preferFastRecognition;
    private VoskStreamingSession _streamingSession;
    private DateTime _lastPreviewUtc;
    private string _lastPreview = "";
    private int _autoCompleteInProgress;
    private int _operationId;
    private TextInsertionTarget _deliveryTarget;

    internal TextInsertionTarget CurrentDeliveryTarget => _deliveryTarget;
    internal CancellationToken CurrentOperationToken =>
        _processingCts?.Token ?? CancellationToken.None;
    internal long LastRecordingDurationMs { get; private set; }
    internal long LastTranscriptionDurationMs { get; private set; }

    public VoiceCaptureService(
        AudioRecorderService audioRecorder,
        SpeechRecognitionService speechRecognition,
        NotificationService notifications,
        DestinationRouter router)
    {
        _audioRecorder = audioRecorder;
        _speechRecognition = speechRecognition;
        _notifications = notifications;
        _router = router;
    }

    public bool Start(RecordingMode mode, string quickTarget = null)
    {
        if (KeyboardWtfState.IsProcessing
            || (KeyboardWtfState.IsDownloadingModels && !_speechRecognition.IsVoskLoaded && !_speechRecognition.IsWhisperLoaded))
        {
            _notifications.Warning("keyboard.wtf", KeyboardWtfState.ModelDownloadStatus ?? "Still processing");
            return false;
        }

        if (KeyboardWtfState.IsRecording)
            return false;

        if (!IsMicrophoneAvailable(out var reason))
        {
            KeyboardWtfState.LastSendResult = "No mic";
            _notifications.Error("No microphone", reason);
            return false;
        }

        _deliveryTarget = TextInsertionTarget.Capture();
        KeyboardWtfState.SetUi(
            VoiceUiPhase.Listening,
            ModeTitle(mode),
            "Starting microphone...");
        StartLivePreview();
        var microphoneStarted = Stopwatch.GetTimestamp();
        try
        {
            _audioRecorder.StartRecording();
        }
        catch (Exception ex)
        {
            StopLivePreview();
            _deliveryTarget = null;
            KeyboardWtfState.IsRecording = false;
            KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
            KeyboardWtfState.LastSendResult = "Microphone unavailable";
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Error,
                "Microphone unavailable",
                "Microphone access is required. Check Windows privacy settings and the selected input device.");
            _notifications.Error(
                "Microphone unavailable",
                "Microphone access is required. Check Windows privacy settings and the selected input device.");
            AppLog.Warning(ex, "Microphone recording could not start");
            return false;
        }
        AppLog.Info(
            $"Microphone capture started: mode={mode} startupMs=" +
            $"{(long)Stopwatch.GetElapsedTime(microphoneStarted).TotalMilliseconds}");
        KeyboardWtfState.IsRecording = true;
        KeyboardWtfState.CurrentRecordingMode = mode;
        KeyboardWtfState.ActiveQuickSendTarget = quickTarget;
        if (!KeyboardWtfState.AppendMode && mode != RecordingMode.Command)
            KeyboardWtfState.CurrentTranscript = string.Empty;
        KeyboardWtfState.FormattedOutputs.Clear();
        KeyboardWtfState.LastSendResult = null;
        _recordingStartUtc = DateTime.UtcNow;
        KeyboardWtfState.SetUi(
            VoiceUiPhase.Listening,
            ModeTitle(mode),
            "Speak now. Press the same shortcut to finish.");

        _autoStopCts?.Cancel();
        _autoStopCts = new CancellationTokenSource();
        _ = AutoStopAfterLimitAsync(mode, _autoStopCts.Token);
        // Smart Writing is explicitly stopped by the user. A pause between
        // sentences must not discard the rest of a long-form recording.
        if (UsesSilenceAutoStop(mode))
            StartSilenceWatcher(mode);

        _notifications.Info(ModeTitle(mode), "Listening");
        return true;
    }

    public async Task<string> StopAndTranscribeAsync(
        bool updateTranscript = true,
        bool preferFast = false,
        bool keepProcessingUntilCompletionHandler = false)
    {
        if (!KeyboardWtfState.IsRecording)
            return string.Empty;

        _autoStopCts?.Cancel();
        _silenceCts?.Cancel();
        var mode = KeyboardWtfState.CurrentRecordingMode;
        var wavData = _audioRecorder.StopRecording();
        LastRecordingDurationMs = Math.Max(
            0,
            (long)(DateTime.UtcNow - _recordingStartUtc).TotalMilliseconds);
        StopLivePreview();
        KeyboardWtfState.IsRecording = false;
        KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
        KeyboardWtfState.ActiveQuickSendTarget = null;
        KeyboardWtfState.IsProcessing = true;
        var operationId = Interlocked.Increment(ref _operationId);
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _processingCts = new CancellationTokenSource();
        var token = _processingCts.Token;
        KeyboardWtfState.SetUi(
            VoiceUiPhase.Transcribing,
            "Transcribing",
            preferFast ? "Fast local recognition" : "Accurate local recognition");
        _notifications.Info("Processing", "Transcribing audio...");

        try
        {
            var recognitionStarted = Stopwatch.GetTimestamp();
            var transcript = await _speechRecognition.RecognizeFromWavAsync(wavData, token, preferFast);
            token.ThrowIfCancellationRequested();
            transcript = await PostProcessTranscriptAsync(
                transcript,
                allowAiTranslation: RecordingModeUsesAiPostProcessing(mode),
                token);
            LastTranscriptionDurationMs = (long)Stopwatch
                .GetElapsedTime(recognitionStarted)
                .TotalMilliseconds;
            if (operationId != _operationId)
                return string.Empty;
            if (updateTranscript)
                KeyboardWtfState.SetTranscript(transcript);
            AppLog.Info(
                $"Voice transcription completed: mode={mode} recordingMs={LastRecordingDurationMs} " +
                $"transcriptionMs={LastTranscriptionDurationMs} characters={transcript?.Length ?? 0}");
            _notifications.InfoTransient(
                "Transcribed",
                string.IsNullOrWhiteSpace(transcript) ? "No speech detected." : transcript);
            if (string.IsNullOrWhiteSpace(transcript))
                KeyboardWtfState.SetUi(VoiceUiPhase.Done, "No speech detected", "Press a shortcut to try again.");
            return transcript;
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Transcription operation cancelled");
            return string.Empty;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Transcription failed");
            KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Transcription failed", ex.Message);
            _notifications.Error("Transcription failed", ex.Message);
            return string.Empty;
        }
        finally
        {
            if (operationId == _operationId && !keepProcessingUntilCompletionHandler)
                KeyboardWtfState.IsProcessing = false;
        }
    }

    public Task ToggleDictationAsync(Func<string, Task> handleTranscript) =>
        ToggleModeAsync(RecordingMode.Dictate, handleTranscript, preferFast: true);

    internal Task ToggleSmartModeAsync(Func<SmartWritingAudioCapture, Task> handleAudio) =>
        ToggleAudioModeAsync(RecordingMode.VoiceAssistant, handleAudio);

    public Task ToggleCommandModeAsync(Func<string, Task> handleTranscript) =>
        ToggleModeAsync(RecordingMode.Command, handleTranscript, preferFast: true);

    public Task ToggleJarvisPreflightAsync(Func<string, Task> handleTranscript) =>
        ToggleModeAsync(RecordingMode.JarvisPreflight, handleTranscript, preferFast: true);

    public async Task QuickSendAsync(string destinationName)
    {
        if (KeyboardWtfState.IsProcessing)
        {
            Cancel();
            return;
        }

        if (!KeyboardWtfState.IsRecording)
        {
            _preferFastRecognition = true;
            _completionHandler = async transcript =>
            {
                var ok = await _router.SendAsync(
                    destinationName,
                    transcript,
                    _deliveryTarget,
                    _processingCts?.Token ?? CancellationToken.None);
                _notifications.Info(ok ? "Sent" : "Send failed", KeyboardWtfState.LastSendResult ?? destinationName);
            };
            Start(RecordingMode.QuickSend, destinationName);
            return;
        }

        if (KeyboardWtfState.CurrentRecordingMode == RecordingMode.QuickSend)
            await CompleteRecordingAsync();
    }

    public void Cancel()
    {
        Interlocked.Increment(ref _operationId);
        _autoStopCts?.Cancel();
        _silenceCts?.Cancel();
        _processingCts?.Cancel();
        if (KeyboardWtfState.IsRecording)
        {
            _audioRecorder.StopRecording();
            StopLivePreview();
            KeyboardWtfState.IsRecording = false;
        }

        KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
        KeyboardWtfState.IsProcessing = false;
        KeyboardWtfState.IsProcessingAi = false;
        _completionHandler = null;
        _audioCompletionHandler = null;
        _deliveryTarget = null;
        KeyboardWtfState.LastSendResult = "Cancelled";
        KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "Nothing was typed or executed.");
        _notifications.Info("Cancelled", "Current voice operation cancelled.");
    }

    public async Task<string> ApplyPromptToCurrentTranscriptAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var transcript = KeyboardWtfState.CurrentTranscript;
        if (string.IsNullOrWhiteSpace(transcript))
            return string.Empty;

        var provider = AiProviderRegistry.Current;
        if (provider == null || !provider.IsAvailable)
        {
            if (provider is PaidCloudAiProvider paidProvider)
                throw new InvalidOperationException(paidProvider.UnavailableMessage);
            _notifications.Warning("AI unavailable", "No configured AI provider; copied raw transcript instead.");
            return transcript;
        }

        KeyboardWtfState.IsProcessingAi = true;
        KeyboardWtfState.SetUi(VoiceUiPhase.Thinking, "Polishing", "Gemini is cleaning up your words.");
        try
        {
            var result = await provider
                .ReformatAsync(
                    transcript,
                    prompt,
                    KeyboardWtfState.SelectedLanguage,
                    cancellationToken);
            if (!string.IsNullOrWhiteSpace(result))
                KeyboardWtfState.SetTranscript(result);
            return string.IsNullOrWhiteSpace(result) ? transcript : result;
        }
        finally
        {
            KeyboardWtfState.IsProcessingAi = false;
        }
    }

    internal async Task<string> TranscribeCapturedAudioAsync(
        byte[] wavData,
        bool preferFast,
        CancellationToken cancellationToken)
    {
        if (wavData is null || wavData.Length < 44)
            return string.Empty;
        var started = Stopwatch.GetTimestamp();
        var transcript = await _speechRecognition.RecognizeFromWavAsync(
            wavData,
            cancellationToken,
            preferFast);
        transcript = await PostProcessTranscriptAsync(
            transcript,
            allowAiTranslation: true,
            cancellationToken);
        LastTranscriptionDurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return transcript;
    }

    internal static bool RecordingModeUsesAiPostProcessing(RecordingMode mode) =>
        mode != RecordingMode.Dictate;

    internal static bool UsesSilenceAutoStop(RecordingMode mode) =>
        mode != RecordingMode.VoiceAssistant;

    private async Task<string> PostProcessTranscriptAsync(
        string transcript,
        bool allowAiTranslation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return transcript;

        if (KeyboardWtfState.UseFillerWordCleaner)
            transcript = FillerWordFilter.Apply(transcript);
        transcript = CaseTransformer.Apply(transcript, KeyboardWtfState.SelectedCaseTransform);

        var targetLang = KeyboardWtfState.TranslateTargetLanguage;
        if (allowAiTranslation && !string.IsNullOrEmpty(targetLang))
        {
            var provider = AiProviderRegistry.Current;
            if (provider?.IsAvailable == true)
            {
                var langName = targetLang == "de" ? "German" : targetLang;
                var translated = await provider.ReformatAsync(
                    transcript,
                    $"Translate the following text to {langName}. Return only the translated text, no explanations.",
                    targetLang,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(translated))
                    transcript = translated;
            }
        }

        return transcript;
    }

    private async Task AutoStopAfterLimitAsync(RecordingMode mode, CancellationToken token)
    {
        try
        {
            var maximumSeconds = mode == RecordingMode.VoiceAssistant
                ? SmartWritingAudioCapture.MaximumSeconds
                : KeyboardWtfState.MaxRecordingSeconds;
            await Task.Delay(TimeSpan.FromSeconds(maximumSeconds), token);
            if (!token.IsCancellationRequested && KeyboardWtfState.IsRecording && KeyboardWtfState.CurrentRecordingMode == mode)
            {
                _notifications.Warning("Auto-stopped", "Recording reached the configured limit.");
                await CompleteRecordingAsync();
            }
        }
        catch (TaskCanceledException) { }
    }

    private void StartSilenceWatcher(RecordingMode mode)
    {
        _silenceCts?.Cancel();
        _silenceCts = new CancellationTokenSource();
        _ = MonitorSilenceAsync(mode, _silenceCts.Token);
    }

    private async Task MonitorSilenceAsync(RecordingMode mode, CancellationToken token)
    {
        var heardVoice = false;
        var heardStrongVoice = false;
        var lastVoiceUtc = DateTime.UtcNow;
        var lastStrongVoiceUtc = DateTime.UtcNow;

        try
        {
            while (!token.IsCancellationRequested
                && KeyboardWtfState.IsRecording
                && KeyboardWtfState.CurrentRecordingMode == mode)
            {
                await Task.Delay(100, token);
                var now = DateTime.UtcNow;
                var elapsed = now - _recordingStartUtc;
                var peakDb = _audioRecorder.CurrentPeakDb;

                if (peakDb > -36)
                {
                    heardVoice = true;
                    lastVoiceUtc = now;
                }
                else if (heardVoice && peakDb > -43)
                {
                    lastVoiceUtc = now;
                }

                if (peakDb > -29)
                {
                    heardStrongVoice = true;
                    lastStrongVoiceUtc = now;
                }

                var quietPause = (now - lastVoiceUtc).TotalMilliseconds > 800;
                var noisyRoomPause = heardStrongVoice
                    && (now - lastStrongVoiceUtc).TotalMilliseconds > 1200;
                if (heardVoice && elapsed.TotalMilliseconds > 650 && (quietPause || noisyRoomPause))
                {
                    _notifications.Info("Processing", "Pause detected.");
                    await CompleteRecordingAsync();
                    return;
                }

                if (!heardVoice && elapsed.TotalSeconds > 5)
                {
                    _notifications.Warning("No speech", "Stopped listening after silence.");
                    await CompleteRecordingAsync();
                    return;
                }
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Silence watcher failed");
        }
    }

    private async Task ToggleModeAsync(
        RecordingMode mode,
        Func<string, Task> handleTranscript,
        bool preferFast)
    {
        if (KeyboardWtfState.IsProcessing || KeyboardWtfState.IsProcessingAi)
        {
            Cancel();
            return;
        }

        if (!KeyboardWtfState.IsRecording)
        {
            _audioCompletionHandler = null;
            _completionHandler = handleTranscript;
            _preferFastRecognition = preferFast;
            Start(mode);
            return;
        }

        if (KeyboardWtfState.CurrentRecordingMode == mode)
            await CompleteRecordingAsync();
        else
            _notifications.Warning("Already listening", "Finish or cancel the current voice mode first.");
    }

    private async Task ToggleAudioModeAsync(
        RecordingMode mode,
        Func<SmartWritingAudioCapture, Task> handleAudio)
    {
        if (KeyboardWtfState.IsProcessing || KeyboardWtfState.IsProcessingAi)
        {
            Cancel();
            return;
        }

        if (!KeyboardWtfState.IsRecording)
        {
            _completionHandler = null;
            _audioCompletionHandler = handleAudio;
            _preferFastRecognition = false;
            Start(mode);
            return;
        }

        if (KeyboardWtfState.CurrentRecordingMode == mode)
            await CompleteRecordingAsync();
        else
            _notifications.Warning("Already listening", "Finish or cancel the current voice mode first.");
    }

    private async Task CompleteRecordingAsync()
    {
        if (Interlocked.Exchange(ref _autoCompleteInProgress, 1) == 1)
            return;

        try
        {
            var mode = KeyboardWtfState.CurrentRecordingMode;
            var handler = _completionHandler;
            var audioHandler = _audioCompletionHandler;
            if (mode == RecordingMode.VoiceAssistant && audioHandler != null)
            {
                var capture = StopAndCaptureSmartWritingAudio();
                if (capture.WavData.Length >= 44)
                    await audioHandler(capture);
            }
            else
            {
                var transcript = await StopAndTranscribeAsync(
                    updateTranscript: false,
                    preferFast: _preferFastRecognition,
                    keepProcessingUntilCompletionHandler: true);
                if (!string.IsNullOrWhiteSpace(transcript) && handler != null)
                    await handler(transcript);
            }
        }
        catch (OperationCanceledException)
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "Voice processing was stopped.");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Voice completion failed");
            KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Voice action failed", ex.Message);
            _notifications.Error("Voice action failed", ex.Message);
        }
        finally
        {
            _completionHandler = null;
            _audioCompletionHandler = null;
            _preferFastRecognition = false;
            _deliveryTarget = null;
            KeyboardWtfState.IsProcessing = false;
            Interlocked.Exchange(ref _autoCompleteInProgress, 0);
        }
    }

    private SmartWritingAudioCapture StopAndCaptureSmartWritingAudio()
    {
        if (!KeyboardWtfState.IsRecording)
            return new SmartWritingAudioCapture([], 0);

        _autoStopCts?.Cancel();
        _silenceCts?.Cancel();
        var wavData = _audioRecorder.StopRecording();
        LastRecordingDurationMs = Math.Max(
            0,
            (long)(DateTime.UtcNow - _recordingStartUtc).TotalMilliseconds);
        LastTranscriptionDurationMs = 0;
        StopLivePreview();
        KeyboardWtfState.IsRecording = false;
        KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
        KeyboardWtfState.ActiveQuickSendTarget = null;
        KeyboardWtfState.IsProcessing = true;
        Interlocked.Increment(ref _operationId);
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _processingCts = new CancellationTokenSource();
        KeyboardWtfState.SetUi(
            VoiceUiPhase.Transcribing,
            "Understanding speech",
            "Gemini is listening to the recording.");
        return new SmartWritingAudioCapture(wavData, LastRecordingDurationMs);
    }

    private static string ModeTitle(RecordingMode mode) => mode switch
    {
        RecordingMode.Dictate => "Dictation",
        RecordingMode.VoiceAssistant => "Smart writing",
        RecordingMode.Command => "Execute command",
        RecordingMode.JarvisPreflight => "Jarvis",
        RecordingMode.QuickSend => "Quick send",
        _ => "Listening",
    };

    private void StartLivePreview()
    {
        StopLivePreview();
        _streamingSession = _speechRecognition.CreateStreamingSession();
        if (_streamingSession == null)
            return;

        _lastPreview = "";
        _lastPreviewUtc = DateTime.MinValue;
        _audioRecorder.PcmDataAvailable += OnPcmDataAvailable;
    }

    private void OnPcmDataAvailable(byte[] buffer, int count)
    {
        try
        {
            var preview = _streamingSession?.AcceptPcm(buffer, count) ?? "";
            if (string.IsNullOrWhiteSpace(preview)
                || string.Equals(preview, _lastPreview, StringComparison.Ordinal)
                || DateTime.UtcNow - _lastPreviewUtc < TimeSpan.FromMilliseconds(180))
                return;

            _lastPreview = preview;
            _lastPreviewUtc = DateTime.UtcNow;
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Listening,
                ModeTitle(KeyboardWtfState.CurrentRecordingMode),
                preview);
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Live Vosk preview failed");
            StopLivePreview();
        }
    }

    private void StopLivePreview()
    {
        _audioRecorder.PcmDataAvailable -= OnPcmDataAvailable;
        _streamingSession?.Dispose();
        _streamingSession = null;
        _lastPreview = "";
    }

    private static bool IsMicrophoneAvailable(out string reason)
    {
        try
        {
            return IsMicrophoneSelectionAvailable(
                WaveInEvent.DeviceCount,
                KeyboardWtfState.SelectedMicrophoneIndex,
                out reason);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    internal static bool IsMicrophoneSelectionAvailable(
        int deviceCount,
        int selectedIndex,
        out string reason)
    {
        if (deviceCount <= 0)
        {
            reason = "No microphone detected.";
            return false;
        }
        if (selectedIndex >= deviceCount)
        {
            reason = $"Selected microphone {selectedIndex} is not available.";
            return false;
        }
        if (selectedIndex < AudioRecorderService.SystemDefaultDeviceNumber)
        {
            reason = $"Selected microphone {selectedIndex} is invalid.";
            return false;
        }
        reason = null;
        return true;
    }
}

internal sealed record SmartWritingAudioCapture(byte[] WavData, long RecordingDurationMs)
{
    public const int MaximumSeconds = 300;
}
