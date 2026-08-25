namespace KeyboardWtf.Tests;

using KeyboardWtf.Destinations;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

public sealed class VoiceModesRegressionTests
{
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
    public void Smart_writing_audio_capture_is_bounded_to_five_minutes() =>
        Assert.Equal(300, SmartWritingAudioCapture.MaximumSeconds);

    [Fact]
    public void Smart_writing_waits_for_explicit_stop_instead_of_ending_on_a_thinking_pause()
    {
        Assert.False(VoiceCaptureService.UsesSilenceAutoStop(RecordingMode.VoiceAssistant));
        Assert.True(VoiceCaptureService.UsesSilenceAutoStop(RecordingMode.Dictate));
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
