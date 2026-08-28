using KeyboardWtf.Helpers;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class SpeechPronunciationTests
{
    [Theory]
    [InlineData("Welcome to keyboard.wtf.", "Welcome to keyboard dot W T F.")]
    [InlineData("Open Keyboard WTF settings.", "Open keyboard dot W T F settings.")]
    [InlineData("WTF is the acronym.", "W T F is the acronym.")]
    [InlineData("A waterfall stays unchanged.", "A waterfall stays unchanged.")]
    public void ForSynthesis_applies_only_bounded_pronunciation_overrides(
        string input,
        string expected)
    {
        Assert.Equal(expected, SpeechPronunciation.ForSynthesis(input));
    }

    [Fact]
    public void Live_instruction_preserves_non_audio_text_and_tool_values()
    {
        Assert.Contains("Speech-performance rule only", SpeechPronunciation.LiveAudioInstruction);
        Assert.Contains("keep written text, transcriptions, URLs, and tool arguments unchanged", SpeechPronunciation.LiveAudioInstruction);
        Assert.Contains("keyboard dot W T F", SpeechPronunciation.LiveAudioInstruction);
    }
}
