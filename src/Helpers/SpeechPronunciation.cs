namespace KeyboardWtf.Helpers;

using System.Text.RegularExpressions;

internal static partial class SpeechPronunciation
{
    internal const string LiveAudioInstruction =
        "Speech-performance rule only: keep written text, transcriptions, URLs, and tool arguments unchanged. " +
        "When speaking keyboard.wtf, pronounce it as keyboard dot W T F. " +
        "When speaking the acronym WTF on its own, say the individual letters W T F, never as a word.";

    internal static string ForSynthesis(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var spoken = KeyboardWtfPattern().Replace(text, "keyboard dot W T F");
        return WtfPattern().Replace(spoken, "W T F");
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])keyboard(?:\.|\s+)wtf(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyboardWtfPattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])wtf(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WtfPattern();
}
