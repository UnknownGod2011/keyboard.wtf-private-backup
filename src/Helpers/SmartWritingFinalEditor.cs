namespace KeyboardWtf.Helpers;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Applies a deliberately conservative final pass to Gemini Smart Writing.
/// Semantic rewriting remains Gemini's job; this removes only standalone
/// hesitation tokens that occasionally survive the editorial instruction.
/// Quoted speech, URLs, and uppercase abbreviations are left untouched.
/// </summary>
internal static class SmartWritingFinalEditor
{
    private static readonly Regex Url = new(
        @"(?:https?://|www\.)\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex StandaloneHesitation = new(
        @"(?:[,;:\u2014\u2013-]\s*)?(?<![\p{L}\p{N}_])(?<word>u+h+|u+m+|e+r+m+|e+r+|h+m+)(?![\p{L}\p{N}_])(?:\s*[,;:\u2014\u2013-])?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HorizontalWhitespace = new(
        @"[\t ]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SpaceBeforePunctuation = new(
        @"\s+([,.;:!?])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DuplicateSeparators = new(
        @"(?:\s*[,;:]\s*){2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static string Clean(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder(value.Length);
        var segmentStart = 0;
        var inQuote = false;
        char closingQuote = '\0';

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!inQuote && TryGetClosingQuote(character, out closingQuote))
            {
                builder.Append(CleanUnquoted(value[segmentStart..index]));
                inQuote = true;
                segmentStart = index;
                continue;
            }

            if (inQuote && character == closingQuote)
            {
                builder.Append(value[segmentStart..(index + 1)]);
                inQuote = false;
                closingQuote = '\0';
                segmentStart = index + 1;
            }
        }

        if (segmentStart < value.Length)
        {
            builder.Append(inQuote
                ? value[segmentStart..]
                : CleanUnquoted(value[segmentStart..]));
        }

        return builder.ToString().Trim();
    }

    private static string CleanUnquoted(string value)
    {
        if (value.Length == 0)
            return value;

        var builder = new StringBuilder(value.Length);
        var cursor = 0;
        foreach (Match match in Url.Matches(value))
        {
            builder.Append(CleanPlainText(value[cursor..match.Index]));
            builder.Append(match.Value);
            cursor = match.Index + match.Length;
        }
        builder.Append(CleanPlainText(value[cursor..]));
        return builder.ToString();
    }

    private static string CleanPlainText(string value)
    {
        var cleaned = StandaloneHesitation.Replace(value, match =>
        {
            var word = match.Groups["word"].Value;
            return IsUppercaseAbbreviation(word) ? match.Value : " ";
        });
        cleaned = DuplicateSeparators.Replace(cleaned, ", ");
        cleaned = HorizontalWhitespace.Replace(cleaned, " ");
        cleaned = SpaceBeforePunctuation.Replace(cleaned, "$1");
        return cleaned;
    }

    private static bool TryGetClosingQuote(char value, out char closing)
    {
        closing = value switch
        {
            '"' => '"',
            '\u201c' => '\u201d',
            _ => '\0',
        };
        return closing != '\0';
    }

    private static bool IsUppercaseAbbreviation(string value) =>
        value.Length > 1
        && value.All(character => !char.IsLetter(character) || char.IsUpper(character));
}
