namespace KeyboardWtf.Services;

using System.Text.RegularExpressions;

public static class SettingsSectionRegistry
{
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["getting-started"] = "getting-started",
            ["getting started"] = "getting-started",
            ["onboarding"] = "getting-started",
            ["voice-capture"] = "voice-capture",
            ["voice capture"] = "voice-capture",
            ["general"] = "voice-capture",
            ["account-billing"] = "account-billing",
            ["account billing"] = "account-billing",
            ["account"] = "account-billing",
            ["billing"] = "account-billing",
            ["hotkeys"] = "hotkeys",
            ["hotkey"] = "hotkeys",
            ["shortcuts"] = "hotkeys",
            ["assistant"] = "assistant",
            ["jarvis"] = "assistant",
            ["browser-automation"] = "browser-automation",
            ["browser automation"] = "browser-automation",
            ["browser helper"] = "browser-automation",
            ["how-to-use"] = "how-to-use",
            ["how-to-use-jarvis"] = "how-to-use",
            ["how to use jarvis"] = "how-to-use",
            ["support-maintenance"] = "support-maintenance",
            ["support"] = "support-maintenance",
            ["maintenance"] = "support-maintenance",
            ["speech-models"] = "speech-models",
            ["speech models"] = "speech-models",
            ["text-to-speech"] = "tts",
            ["text to speech"] = "tts",
            ["tts"] = "tts",
            ["voice-notes"] = "voice-notes",
            ["voice notes"] = "voice-notes",
            ["destinations"] = "destinations",
            ["whats-next"] = "roadmap",
            ["what's next"] = "roadmap",
            ["roadmap"] = "roadmap",
        };

    public static IReadOnlyCollection<string> CanonicalIds { get; } =
        Aliases.Values.Distinct(StringComparer.Ordinal).ToArray();

    public static string Resolve(string section)
    {
        var value = (section ?? "").Trim().TrimStart('#').Replace('_', '-');
        if (string.IsNullOrWhiteSpace(value))
            return "";
        value = value.Replace("keyboard.wtf", "", StringComparison.OrdinalIgnoreCase)
            .Replace("keyboard wtf", "", StringComparison.OrdinalIgnoreCase)
            .Replace("settings", "", StringComparison.OrdinalIgnoreCase)
            .Trim(' ', '-', '.');
        return Aliases.TryGetValue(value, out var canonical) ? canonical : "";
    }

    public static bool TryResolveKeyboardWtfNavigationRequest(
        string request,
        out string section)
    {
        section = "";
        var text = NormalizeForMatch(request);
        if (text.Length == 0
            || !Regex.IsMatch(
                text,
                @"^(?:please\s+)?(?:open|show(?:\s+me)?|display|go\s+to|navigate\s+to|take\s+me\s+to|bring\s+up)\b",
                RegexOptions.IgnoreCase)
            || !Regex.IsMatch(text, @"\bsettings\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        // An unqualified "open settings" remains Windows Settings. Only a
        // request that identifies keyboard.wtf/Jarvis itself owns this local
        // route; arbitrary localhost destinations never enter this path.
        var keyboardWtfScoped = Regex.IsMatch(
            text,
            @"\bkeyboard\s*wtf\b|\bjarvis\b.{0,60}\bsettings\b|\byour\b.{0,60}\bsettings\b",
            RegexOptions.IgnoreCase);
        if (!keyboardWtfScoped)
            return false;

        section = ResolveMention(request);
        return true;
    }

    internal static string ResolveMention(string request)
    {
        var padded = $" {NormalizeForMatch(request)} ";
        if (padded.Length <= 2)
            return "";

        // Longest aliases win. "Jarvis" is a product identity in a Settings
        // request, not an implicit request for the Assistant section.
        foreach (var alias in Aliases.Keys
                     .Where(value => !string.Equals(value, "jarvis", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(value => NormalizeForMatch(value).Length))
        {
            var normalizedAlias = NormalizeForMatch(alias);
            if (normalizedAlias.Length > 0
                && padded.Contains($" {normalizedAlias} ", StringComparison.OrdinalIgnoreCase))
            {
                return Aliases[alias];
            }
        }

        return "";
    }

    private static string NormalizeForMatch(string value) =>
        Regex.Replace((value ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
}
