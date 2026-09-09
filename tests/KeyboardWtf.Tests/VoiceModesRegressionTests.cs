namespace KeyboardWtf.Tests;

using KeyboardWtf.Destinations;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using NAudio.Wave;
using Xunit;

public sealed class VoiceModesRegressionTests
{
    [Fact]
    public void Smart_writing_instruction_requires_editorial_cleanup_without_losing_meaning()
    {
        var instruction = CommandRegistry.SmartWritingSystemInstruction;

        Assert.Contains("real editorial cleanup", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("non-semantic fillers", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accidental repeated", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wordy speech scaffolding", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("discard the abandoned clause", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("correction markers", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("natural, grammatically correct written", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("separate silent final editorial pass", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not summarize", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("names, technical terminology, URLs, numbers", instruction, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only the final text", instruction, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "Uh, I think, um, we should probably launch this next week.",
        "I think we should probably launch this next week.")]
    [InlineData(
        "Erm—this is, hmm, the final draft.",
        "this is the final draft.")]
    public void Smart_writing_final_editor_removes_surviving_standalone_hesitations(
        string modelOutput,
        string expected) =>
        Assert.Equal(expected, CommandRegistry.FinalizeSmartWritingText(modelOutput));

    [Theory]
    [InlineData("She said, \"Um, that was the original quote.\"")]
    [InlineData("Use https://example.com/uh/reference for the ER workflow.")]
    public void Smart_writing_final_editor_preserves_quotes_urls_and_abbreviations(string value) =>
        Assert.Equal(value, CommandRegistry.FinalizeSmartWritingText(value));

    [Fact]
    public void Smart_writing_polishing_is_independent_of_the_local_filler_checkbox()
    {
        var root = FindRepositoryRoot();
        var captureSource = File.ReadAllText(Path.Combine(root, "src", "Services", "VoiceCaptureService.cs"));
        var commandSource = File.ReadAllText(Path.Combine(root, "src", "Services", "CommandRegistry.cs"));
        var settingsPage = File.ReadAllText(Path.Combine(root, "src", "Resources", "web", "settings.html"));

        Assert.Contains(
            "ToggleAudioModeAsync(RecordingMode.VoiceAssistant, handleAudio)",
            captureSource,
            StringComparison.Ordinal);
        Assert.Contains("paidProvider.ReformatAudioAsync(", commandSource, StringComparison.Ordinal);
        Assert.DoesNotContain("UseFillerWordCleaner", commandSource, StringComparison.Ordinal);
        Assert.Contains(
            "Optional local transcript cleanup for Raw Dictation",
            settingsPage,
            StringComparison.Ordinal);
        Assert.Contains(
            "Smart Writing always applies its own Gemini editing",
            settingsPage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Smart_writing_has_a_bounded_end_to_end_post_transcription_timeout() =>
        Assert.InRange(CommandRegistry.SmartWritingTimeout, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40));

    [Theory]
    [InlineData(10_000, 40.5)]
    [InlineData(30_000, 41.5)]
    [InlineData(300_000, 55)]
    public void Smart_writing_audio_timeout_is_bounded_for_short_and_long_recordings(
        long recordingMs,
        double expectedSeconds) =>
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            CommandRegistry.SmartWritingTimeoutFor(recordingMs));

    [Fact]
    public void Paid_transport_envelope_cannot_expire_before_the_owned_smart_writing_budget()
    {
        var longestOwnedOperation = CommandRegistry.SmartWritingTimeoutFor(
            CommandRegistry.SmartWritingProviderSegmentSeconds * 1000L);

        Assert.True(
            KeyboardWtf.Services.PaidStaging.PaidAccountService.TransportTimeout
            >= longestOwnedOperation + TimeSpan.FromSeconds(10));
        Assert.InRange(
            KeyboardWtf.Services.PaidStaging.PaidAccountService.TransportTimeout,
            TimeSpan.FromSeconds(65),
            TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void Smart_writing_has_no_duration_auto_stop_and_raw_dictation_can_be_manual()
    {
        Assert.False(VoiceCaptureService.ShouldScheduleAutoStop(
            RecordingMode.VoiceAssistant,
            configuredSeconds: 30));
        Assert.False(VoiceCaptureService.ShouldScheduleAutoStop(
            RecordingMode.Dictate,
            configuredSeconds: 0));
        Assert.True(VoiceCaptureService.ShouldScheduleAutoStop(
            RecordingMode.Dictate,
            configuredSeconds: 600));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 5)]
    [InlineData(600, 600)]
    [InlineData(7200, 3600)]
    public void Recording_duration_supports_manual_and_long_bounded_raw_dictation(
        int requested,
        int expected) =>
        Assert.Equal(expected, SettingsService.NormalizeMaxRecordingSeconds(requested));

    [Fact]
    public void Ten_minute_smart_writing_audio_is_split_into_backend_safe_ordered_segments()
    {
        var wav = CreatePcmWave(TimeSpan.FromMinutes(10));

        var segments = CommandRegistry.SplitSmartWritingAudio(wav);

        Assert.Equal(3, segments.Count);
        Assert.All(segments, segment =>
        {
            Assert.InRange(
                segment.RecordingDurationMs,
                1,
                CommandRegistry.SmartWritingProviderSegmentSeconds * 1000L);
            Assert.InRange(segment.WavData.Length, 44, 10 * 1024 * 1024);
        });
        Assert.InRange(
            segments.Sum(segment => segment.RecordingDurationMs),
            TimeSpan.FromMinutes(10).TotalMilliseconds - 5,
            TimeSpan.FromMinutes(10).TotalMilliseconds + 5);
    }

    [Fact]
    public void Smart_writing_failure_prefers_the_complete_local_recovery_over_partial_cloud_segments()
    {
        var result = CommandRegistry.ResolveSmartWritingRecoveryText(
            "Complete locally recovered transcript.",
            ["Only the first cloud segment."],
            out var partial);

        Assert.False(partial);
        Assert.Equal("Complete locally recovered transcript.", result);
    }

    [Fact]
    public void Smart_writing_failure_retains_completed_segments_when_local_recovery_is_unavailable()
    {
        var result = CommandRegistry.ResolveSmartWritingRecoveryText(
            "",
            ["First segment.", "Second segment."],
            out var partial);

        Assert.True(partial);
        Assert.Equal(
            $"First segment.{Environment.NewLine}{Environment.NewLine}Second segment.",
            result);
    }

    [Fact]
    public void Smart_writing_provider_failure_keeps_the_already_captured_transcript()
    {
        var result = CommandRegistry.ResolveSmartWritingTranscriptRecovery(
            "Um, this captured speech must survive.");

        Assert.Equal("this captured speech must survive.", result);
    }

    [Theory]
    [InlineData(RecordingMode.Dictate, 7, 7, "  first part second part  ", "first part second part")]
    [InlineData(RecordingMode.Dictate, 7, 8, "first part", "")]
    [InlineData(RecordingMode.Command, 7, 7, "close it", "")]
    public void Only_the_current_raw_dictation_can_recover_its_cumulative_local_preview(
        RecordingMode mode,
        int operationId,
        int currentOperationId,
        string preview,
        string expected) =>
        Assert.Equal(
            expected,
            VoiceCaptureService.RecoverRawDictationPreview(
                mode,
                operationId,
                currentOperationId,
                preview));

    [Fact]
    public void Smart_writing_preserves_normal_thinking_pauses_but_has_a_bounded_final_silence()
    {
        Assert.True(VoiceCaptureService.UsesSilenceAutoStop(RecordingMode.VoiceAssistant));
        Assert.True(VoiceCaptureService.UsesSilenceAutoStop(RecordingMode.Dictate));
        Assert.InRange(
            VoiceCaptureService.SmartWritingFinalSilence,
            TimeSpan.FromSeconds(7),
            TimeSpan.FromSeconds(12));
        Assert.True(VoiceCaptureService.SmartWritingFinalSilence > TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Smart_writing_can_finish_from_a_stable_preview_above_a_noisy_floor()
    {
        var now = DateTime.UtcNow;
        Assert.True(VoiceCaptureService.HasSmartWritingFinalPreviewPause(
            now - TimeSpan.FromSeconds(9),
            now - TimeSpan.FromSeconds(3),
            now));
        Assert.False(VoiceCaptureService.HasSmartWritingFinalPreviewPause(
            now - TimeSpan.FromSeconds(5),
            now - TimeSpan.FromSeconds(3),
            now));
        Assert.False(VoiceCaptureService.HasSmartWritingFinalPreviewPause(
            now - TimeSpan.FromSeconds(9),
            now - TimeSpan.FromSeconds(1),
            now));
        Assert.False(VoiceCaptureService.HasSmartWritingFinalPreviewPause(
            DateTime.MinValue,
            now - TimeSpan.FromSeconds(3),
            now));
    }

    [Fact]
    public void Smart_writing_can_retain_the_final_result_on_clipboard_after_insertion()
    {
        var retained = "";

        Assert.True(CommandRegistry.RetainFinalTextOnClipboard(
            "final Gemini text",
            value =>
            {
                retained = value;
                return true;
            }));
        Assert.Equal("final Gemini text", retained);
    }

    [Fact]
    public void Smart_writing_verifies_and_retries_the_final_clipboard_copy()
    {
        var attempts = 0;
        var clipboard = "old";

        Assert.True(CommandRegistry.EnsureFinalTextOnClipboard(
            "final Gemini text",
            value =>
            {
                attempts++;
                if (attempts == 1)
                    return false;
                clipboard = value;
                return true;
            },
            () => clipboard));
        Assert.Equal(2, attempts);
        Assert.Equal("final Gemini text", clipboard);
    }

    [Fact]
    public void Smart_writing_rejects_an_unverified_final_clipboard_copy()
    {
        Assert.False(CommandRegistry.EnsureFinalTextOnClipboard(
            "final Gemini text",
            _ => true,
            () => "different text"));
    }

    [Theory]
    [InlineData(VoiceUiPhase.Thinking, "Polishing", true)]
    [InlineData(VoiceUiPhase.Thinking, "Understanding command", false)]
    [InlineData(VoiceUiPhase.Done, "Copied instead", false)]
    [InlineData(VoiceUiPhase.Error, "Smart Writing timed out", false)]
    [InlineData(VoiceUiPhase.Cancelled, "Cancelled", false)]
    public void Smart_writing_terminal_cleanup_only_repairs_a_stale_polishing_surface(
        VoiceUiPhase phase,
        string title,
        bool expected) =>
        Assert.Equal(expected, CommandRegistry.NeedsSmartWritingTerminalCleanup(phase, title));

    [Fact]
    public void Raw_dictation_never_uses_AI_post_processing()
    {
        Assert.False(VoiceCaptureService.RecordingModeUsesAiPostProcessing(RecordingMode.Dictate));
        Assert.True(VoiceCaptureService.RecordingModeUsesAiPostProcessing(RecordingMode.VoiceAssistant));
        Assert.True(VoiceCaptureService.RecordingModeUsesAiPostProcessing(RecordingMode.JarvisPreflight));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeyboardWtf.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the keyboard.wtf repository root.");
    }

    private static byte[] CreatePcmWave(TimeSpan duration)
    {
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(output, new WaveFormat(16_000, 16, 1)))
        {
            var remaining = checked((long)Math.Round(duration.TotalSeconds * 32_000d));
            var buffer = new byte[64 * 1024];
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                writer.Write(buffer, 0, count);
                remaining -= count;
            }
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData(0, -1, false)]
    [InlineData(1, -1, true)]
    [InlineData(1, -2, false)]
    [InlineData(2, 1, true)]
    [InlineData(2, 2, false)]
    public void Microphone_selection_is_validated_before_recording(
        int deviceCount,
        int selectedIndex,
        bool expected)
    {
        Assert.Equal(
            expected,
            VoiceCaptureService.IsMicrophoneSelectionAvailable(
                deviceCount,
                selectedIndex,
                out _));
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(-5, -1)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    public void Audio_capture_preserves_the_Windows_default_mapper(
        int configuredIndex,
        int expectedDeviceNumber)
    {
        Assert.Equal(
            expectedDeviceNumber,
            AudioRecorderService.ResolveDeviceNumber(configuredIndex));
    }

    [Fact]
    public void Insertion_target_rejects_a_different_window_or_title()
    {
        var expected = new TextInsertionTarget((IntPtr)10, 20, "Draft - Notepad");
        Assert.True(TextInsertionTarget.Matches(
            expected,
            new TextInsertionTarget((IntPtr)10, 20, "Draft - Notepad")));
        Assert.False(TextInsertionTarget.Matches(
            expected,
            new TextInsertionTarget((IntPtr)11, 20, "Draft - Notepad")));
        Assert.False(TextInsertionTarget.Matches(
            expected,
            new TextInsertionTarget((IntPtr)10, 20, "Another tab")));
    }

    [Fact]
    public async Task Type_out_restores_clipboard_after_verified_paste()
    {
        var expected = new TextInsertionTarget((IntPtr)10, 20, "Draft - Notepad");
        var clipboardText = "original";
        var restored = false;
        var pasteCalls = 0;
        var destination = CreateDestination(
            () => expected,
            () => new ClipboardHelper.Snapshot(),
            text => { clipboardText = text; return true; },
            () => clipboardText,
            _ => { restored = true; clipboardText = "original"; return true; },
            () => { pasteCalls++; return true; },
            (_, _) => Task.CompletedTask);

        Assert.True(await destination.SendAsync("polished", expected, CancellationToken.None));
        Assert.Equal(1, pasteCalls);
        Assert.True(restored);
        Assert.Equal("original", clipboardText);
    }

    [Fact]
    public async Task Type_out_blocks_paste_and_restores_clipboard_when_focus_changes()
    {
        var expected = new TextInsertionTarget((IntPtr)10, 20, "Draft - Notepad");
        var current = expected;
        var clipboardText = "original";
        var restored = false;
        var pasteCalls = 0;
        var destination = CreateDestination(
            () => current,
            () => new ClipboardHelper.Snapshot(),
            text => { clipboardText = text; return true; },
            () => clipboardText,
            _ => { restored = true; clipboardText = "original"; return true; },
            () => { pasteCalls++; return true; },
            (_, _) =>
            {
                current = new TextInsertionTarget((IntPtr)30, 40, "Chrome");
                return Task.CompletedTask;
            });

        Assert.False(await destination.SendAsync("polished", expected, CancellationToken.None));
        Assert.Equal(0, pasteCalls);
        Assert.True(restored);
        Assert.Equal("original", clipboardText);
    }

    [Fact]
    public async Task Type_out_cancellation_prevents_late_paste_and_restores_clipboard()
    {
        var expected = new TextInsertionTarget((IntPtr)10, 20, "Draft - Notepad");
        var clipboardText = "original";
        var restored = false;
        var pasteCalls = 0;
        var destination = CreateDestination(
            () => expected,
            () => new ClipboardHelper.Snapshot(),
            text => { clipboardText = text; return true; },
            () => clipboardText,
            _ => { restored = true; clipboardText = "original"; return true; },
            () => { pasteCalls++; return true; },
            (_, token) => Task.FromException(new OperationCanceledException(token)));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            destination.SendAsync("polished", expected, CancellationToken.None));
        Assert.Equal(0, pasteCalls);
        Assert.True(restored);
        Assert.Equal("original", clipboardText);
    }

    [Fact]
    public void Type_out_uses_the_complete_native_INPUT_layout()
    {
        var expectedSize = IntPtr.Size == 8 ? 40 : 28;
        Assert.Equal(expectedSize, TypeOutDestination.NativeInputSize);
    }

    private static TypeOutDestination CreateDestination(
        Func<TextInsertionTarget> captureTarget,
        Func<ClipboardHelper.Snapshot> captureClipboard,
        Func<string, bool> setClipboard,
        Func<string> getClipboard,
        Func<ClipboardHelper.Snapshot, bool> restoreClipboard,
        Func<bool> sendPaste,
        Func<int, CancellationToken, Task> delay) =>
        new(
            captureTarget,
            captureClipboard,
            setClipboard,
            getClipboard,
            restoreClipboard,
            sendPaste,
            delay);
}
