namespace KeyboardWtf.Services;

public static class SettingsSectionRegistry
{
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["voice-capture"] = "voice-capture",
            ["voice capture"] = "voice-capture",
            ["general"] = "voice-capture",
            ["account-billing"] = "account-billing",
            ["account billing"] = "account-billing",
            ["account"] = "account-billing",
            ["billing"] = "account-billing",
            ["hotkeys"] = "hotkeys",
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
}
