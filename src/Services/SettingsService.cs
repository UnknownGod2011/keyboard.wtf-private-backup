namespace KeyboardWtf.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;

public enum VoiceTextDeliveryMode
{
    TypeIntoActiveApp,
    ClipboardOnly,
    AskEveryTime
}

public enum JarvisPermissionMode
{
    AlwaysAsk,
    AutoExecute
}

public sealed class JarvisWorkflowSettings
{
    public string Name { get; set; } = "";
    public string Apps { get; set; } = "";
    public string Urls { get; set; } = "";
    public string Folder { get; set; } = "";
}

public sealed class HotkeySettings
{
    public string PushToTalk { get; set; } = "Ctrl+Alt+K";
    public string Dictation { get; set; } = "Ctrl+Alt+D";
    public string Jarvis { get; set; } = "Ctrl+Alt+Q";
    public string CommandMode { get; set; } = "";
    public string QuickSend { get; set; } = "";
    public string Cancel { get; set; } = "Ctrl+Alt+X";
    public string OpenSettings { get; set; } = "Ctrl+Alt+,";

    public Dictionary<string, string> ToDictionary() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PushToTalk"] = PushToTalk,
        ["Dictation"] = Dictation,
        ["Jarvis"] = Jarvis,
        ["Cancel"] = Cancel,
        ["OpenSettings"] = OpenSettings,
    };

    public static HotkeySettings Defaults() => new();
}

public sealed class AppSettings
{
    public SpeechEngine SpeechEngine { get; set; } = SpeechEngine.Whisper;
    public string Language { get; set; } = "auto";
    public int MicrophoneDevice { get; set; } = -1;
    public int MaxRecordingSeconds { get; set; } = 30;
    public WhisperModelSize WhisperModelSize { get; set; } = WhisperModelSize.Small;
    public AiProvider AiProvider { get; set; } = AiProvider.Gemini;
    public string ClaudeApiKey { get; set; }
    public string OpenAiApiKey { get; set; }
    public string GeminiApiKey { get; set; }
    public string DeepSeekApiKey { get; set; }
    public string PerplexityApiKey { get; set; }
    public string SlackWebhookUrl { get; set; }
    public string DiscordWebhookUrl { get; set; }
    public string VoiceNoteSavePath { get; set; }
    public string VoiceNoteFilenamePattern { get; set; }
    public bool UseNoiseGate { get; set; } = true;
    public bool UseAi { get; set; } = true;
    public string TranslateTargetLanguage { get; set; } = "";
    public bool UseFillerWordCleaner { get; set; }
    public CaseTransform CaseTransform { get; set; } = CaseTransform.None;
    public Dictionary<string, string> CustomPrompts { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = HotkeySettings.Defaults();
    public string DefaultDestination { get; set; } = "Clipboard";
    public VoiceTextDeliveryMode TextDeliveryMode { get; set; } = VoiceTextDeliveryMode.TypeIntoActiveApp;
    public string AssistantName { get; set; } = "Jarvis";
    public AssistantTone AssistantTone { get; set; } = AssistantTone.Balanced;
    public string GeminiVoice { get; set; } = "Kore";
    public List<JarvisWorkflowSettings> JarvisWorkflows { get; set; } = new();
    public bool StartWithWindows { get; set; } = true;
    public JarvisPermissionMode JarvisPermissionMode { get; set; } = JarvisPermissionMode.AlwaysAsk;
    public string PreferredBrowser { get; set; } = "";
    public bool BrowserAutomationEnabled { get; set; }
    public int BrowserAutomationConsentVersion { get; set; }
    public string BrowserAutomationModel { get; set; } = "gemini-3.1-flash-lite-preview";
    public bool BrowserAutomationMemoryEnabled { get; set; }
    public string UpdateChannel { get; set; } = "stable";
}

public sealed class SettingsService
{
    public const int CurrentBrowserAutomationConsentVersion = 1;

    public static readonly IReadOnlyDictionary<string, string> GeminiVoices =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kore"] = "Firm",
            ["Orus"] = "Firm",
            ["Puck"] = "Upbeat",
            ["Charon"] = "Informative",
            ["Aoede"] = "Breezy",
            ["Zephyr"] = "Bright",
            ["Leda"] = "Youthful",
            ["Fenrir"] = "Excitable",
            ["Achird"] = "Friendly",
            ["Gacrux"] = "Mature",
            ["Sulafat"] = "Warm",
            ["Iapetus"] = "Clear",
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string AppDataDir { get; } = ResolveAppDataDir(
        ConfiguredAppDataOverride(),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static string ConfiguredAppDataOverride()
    {
#if KEYBOARD_WTF_PAID_PRODUCTION_CANDIDATE
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "keyboard.wtf-paid-production-candidate");
#else
        return Environment.GetEnvironmentVariable("KEYBOARD_WTF_APPDATA_DIR");
#endif
    }

    internal static string ResolveAppDataDir(string overridePath, string localAppData)
    {
        var root = Path.GetFullPath(localAppData);
        var defaultPath = Path.Combine(root, "keyboard.wtf");
        if (string.IsNullOrWhiteSpace(overridePath))
            return defaultPath;

        var requested = Path.GetFullPath(overridePath.Trim());
        var relative = Path.GetRelativePath(root, requested);
        if (relative is "."
            || Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "KEYBOARD_WTF_APPDATA_DIR must be a child of the current user's LocalAppData directory.");
        }

        return requested;
    }

    public SettingsService(string settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(AppDataDir, "settings.json")
            : settingsPath;
    }

    public string SettingsPath { get; }

    public AppSettings Current { get; private set; } = new();

    public bool HasBrowserAutomationConsent =>
        Current.BrowserAutomationConsentVersion
        >= CurrentBrowserAutomationConsentVersion;

    public void Load()
    {
        Directory.CreateDirectory(AppDataDir);
        var hadSettingsFile = File.Exists(SettingsPath);
        var settingsJson = "";
        if (File.Exists(SettingsPath))
        {
            try
            {
                settingsJson = File.ReadAllText(SettingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(settingsJson, JsonOptions) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Settings load failed; using defaults");
                Current = new AppSettings();
            }
        }

        Current.Hotkeys ??= HotkeySettings.Defaults();
        MigrateLegacyHotkeys();
        Current.CustomPrompts ??= new Dictionary<string, string>();
        Current.JarvisWorkflows ??= new List<JarvisWorkflowSettings>();
        Current.UpdateChannel = string.Equals(Current.UpdateChannel, "beta", StringComparison.OrdinalIgnoreCase)
            ? "beta"
            : "stable";
        if (!hadSettingsFile || !settingsJson.Contains("\"StartWithWindows\"", StringComparison.OrdinalIgnoreCase))
            Current.StartWithWindows = true;
        Current.GeminiVoice = SanitizeGeminiVoice(Current.GeminiVoice);
        if (string.IsNullOrWhiteSpace(Current.BrowserAutomationModel))
            Current.BrowserAutomationModel = "gemini-3.1-flash-lite-preview";
        ApplyToState();
        Save();
    }

    private void MigrateLegacyHotkeys()
    {
        if (Current.Hotkeys == null)
        {
            Current.Hotkeys = HotkeySettings.Defaults();
            return;
        }

        if (string.Equals(Current.Hotkeys.PushToTalk, "Ctrl+Alt+Space", StringComparison.OrdinalIgnoreCase))
            Current.Hotkeys.PushToTalk = HotkeySettings.Defaults().PushToTalk;
        if (string.Equals(Current.Hotkeys.Dictation, "Ctrl+Alt+V", StringComparison.OrdinalIgnoreCase))
            Current.Hotkeys.Dictation = HotkeySettings.Defaults().Dictation;
        if (string.IsNullOrWhiteSpace(Current.Hotkeys.Jarvis))
        {
            Current.Hotkeys.Jarvis = !string.IsNullOrWhiteSpace(Current.Hotkeys.QuickSend)
                ? Current.Hotkeys.QuickSend
                : !string.IsNullOrWhiteSpace(Current.Hotkeys.CommandMode)
                    ? Current.Hotkeys.CommandMode
                    : HotkeySettings.Defaults().Jarvis;
        }
        if (string.Equals(Current.Hotkeys.Jarvis, "Ctrl+Alt+S", StringComparison.OrdinalIgnoreCase))
            Current.Hotkeys.Jarvis = HotkeySettings.Defaults().Jarvis;
        if (string.IsNullOrWhiteSpace(Current.Hotkeys.Cancel))
            Current.Hotkeys.Cancel = HotkeySettings.Defaults().Cancel;

        // A side-by-side production candidate briefly used F13-F17 to avoid
        // colliding with an already-running legacy build. Most customer
        // keyboards cannot produce F13-F24, so those implementation-only
        // values must never survive into normal user settings.
        var defaults = HotkeySettings.Defaults();
        if (IsInaccessibleFunctionKey(Current.Hotkeys.PushToTalk))
            Current.Hotkeys.PushToTalk = defaults.PushToTalk;
        if (IsInaccessibleFunctionKey(Current.Hotkeys.Dictation))
            Current.Hotkeys.Dictation = defaults.Dictation;
        if (IsInaccessibleFunctionKey(Current.Hotkeys.Jarvis))
            Current.Hotkeys.Jarvis = defaults.Jarvis;
        if (IsInaccessibleFunctionKey(Current.Hotkeys.Cancel))
            Current.Hotkeys.Cancel = defaults.Cancel;
        if (IsInaccessibleFunctionKey(Current.Hotkeys.OpenSettings))
            Current.Hotkeys.OpenSettings = defaults.OpenSettings;

        Current.Hotkeys.CommandMode = "";
        Current.Hotkeys.QuickSend = "";
    }

    internal static bool IsInaccessibleFunctionKey(string hotkey) =>
        (hotkey ?? string.Empty)
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.StartsWith("F", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(part.AsSpan(1), out var number)
                && number is >= 13 and <= 24);

    public void Save()
    {
        Directory.CreateDirectory(AppDataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, JsonOptions));
    }

    public void ApplyToState()
    {
        KeyboardWtfState.SelectedEngine = Current.SpeechEngine;
        KeyboardWtfState.SelectedLanguage = Current.Language ?? "auto";
        KeyboardWtfState.SelectedMicrophoneIndex = Current.MicrophoneDevice;
        KeyboardWtfState.MaxRecordingSeconds = Math.Clamp(Current.MaxRecordingSeconds, 5, 300);
        KeyboardWtfState.SelectedWhisperModel = Current.WhisperModelSize;
        KeyboardWtfState.SelectedAiProvider = Current.AiProvider;
        KeyboardWtfState.ClaudeApiKey = UnprotectAndMigrate(Current.ClaudeApiKey, value => Current.ClaudeApiKey = value);
        KeyboardWtfState.OpenAiApiKey = UnprotectAndMigrate(Current.OpenAiApiKey, value => Current.OpenAiApiKey = value);
        KeyboardWtfState.GeminiApiKey = UnprotectAndMigrate(Current.GeminiApiKey, value => Current.GeminiApiKey = value);
        KeyboardWtfState.DeepSeekApiKey = UnprotectAndMigrate(Current.DeepSeekApiKey, value => Current.DeepSeekApiKey = value);
        KeyboardWtfState.PerplexityApiKey = UnprotectAndMigrate(Current.PerplexityApiKey, value => Current.PerplexityApiKey = value);
        KeyboardWtfState.SlackWebhookUrl = UnprotectAndMigrate(Current.SlackWebhookUrl, value => Current.SlackWebhookUrl = value);
        KeyboardWtfState.DiscordWebhookUrl = UnprotectAndMigrate(Current.DiscordWebhookUrl, value => Current.DiscordWebhookUrl = value);
        KeyboardWtfState.VoiceNoteSavePath = string.IsNullOrWhiteSpace(Current.VoiceNoteSavePath) ? null : Current.VoiceNoteSavePath;
        KeyboardWtfState.VoiceNoteFilenamePattern = string.IsNullOrWhiteSpace(Current.VoiceNoteFilenamePattern) ? null : Current.VoiceNoteFilenamePattern;
        KeyboardWtfState.UseNoiseGate = Current.UseNoiseGate;
        KeyboardWtfState.UseAi = Current.UseAi;
        KeyboardWtfState.TranslateTargetLanguage = Current.TranslateTargetLanguage ?? "";
        KeyboardWtfState.UseFillerWordCleaner = Current.UseFillerWordCleaner;
        KeyboardWtfState.SelectedCaseTransform = Current.CaseTransform;
        KeyboardWtfState.AssistantName = SanitizeAssistantName(Current.AssistantName);
        KeyboardWtfState.AssistantTone = Current.AssistantTone;
        KeyboardWtfState.GeminiVoice = SanitizeGeminiVoice(Current.GeminiVoice);
        KeyboardWtfState.CustomPrompts.Clear();
        foreach (var pair in Current.CustomPrompts)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
                KeyboardWtfState.CustomPrompts[pair.Key] = pair.Value;
        }
    }

    private static string UnprotectAndMigrate(string stored, Action<string> updateStored)
    {
        var plaintext = SecureStore.Unprotect(stored, out var requiresResave);
        if (requiresResave && !string.IsNullOrEmpty(plaintext))
            updateStored(SecureStore.Protect(plaintext));
        return plaintext;
    }

    public void SaveSpeechEngine(SpeechEngine value) { Current.SpeechEngine = value; KeyboardWtfState.SelectedEngine = value; Save(); }
    public void SaveLanguage(string value) { Current.Language = value ?? "auto"; KeyboardWtfState.SelectedLanguage = Current.Language; Save(); }
    public void SaveMicrophoneDevice(int value) { Current.MicrophoneDevice = value; KeyboardWtfState.SelectedMicrophoneIndex = value; Save(); }
    public void SaveMaxRecordingSeconds(int value) { Current.MaxRecordingSeconds = Math.Clamp(value, 5, 300); KeyboardWtfState.MaxRecordingSeconds = Current.MaxRecordingSeconds; Save(); }
    public void SaveWhisperModelSize(WhisperModelSize value) { Current.WhisperModelSize = value; KeyboardWtfState.SelectedWhisperModel = value; Save(); }
    public void SaveAiProvider(AiProvider value) { Current.AiProvider = value; KeyboardWtfState.SelectedAiProvider = value; Save(); }
    public void SaveClaudeApiKey(string value) { Current.ClaudeApiKey = SecureStore.Protect(value); KeyboardWtfState.ClaudeApiKey = value; Save(); }
    public void SaveOpenAiApiKey(string value) { Current.OpenAiApiKey = SecureStore.Protect(value); KeyboardWtfState.OpenAiApiKey = value; Save(); }
    public void SaveGeminiApiKey(string value) { Current.GeminiApiKey = SecureStore.Protect(value); KeyboardWtfState.GeminiApiKey = value; Save(); }
    public void SaveDeepSeekApiKey(string value) { Current.DeepSeekApiKey = SecureStore.Protect(value); KeyboardWtfState.DeepSeekApiKey = value; Save(); }
    public void SavePerplexityApiKey(string value) { Current.PerplexityApiKey = SecureStore.Protect(value); KeyboardWtfState.PerplexityApiKey = value; Save(); }
    public void SaveSlackWebhookUrl(string value) { Current.SlackWebhookUrl = SecureStore.Protect(value); KeyboardWtfState.SlackWebhookUrl = value; Save(); }
    public void SaveDiscordWebhookUrl(string value) { Current.DiscordWebhookUrl = SecureStore.Protect(value); KeyboardWtfState.DiscordWebhookUrl = value; Save(); }
    public void SaveVoiceNoteSavePath(string value) { Current.VoiceNoteSavePath = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); KeyboardWtfState.VoiceNoteSavePath = Current.VoiceNoteSavePath; Save(); }
    public void SaveVoiceNoteFilenamePattern(string value) { Current.VoiceNoteFilenamePattern = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); KeyboardWtfState.VoiceNoteFilenamePattern = Current.VoiceNoteFilenamePattern; Save(); }
    public void SaveUseNoiseGate(bool value) { Current.UseNoiseGate = value; KeyboardWtfState.UseNoiseGate = value; Save(); }
    public void SaveUseAi(bool value) { Current.UseAi = value; KeyboardWtfState.UseAi = value; Save(); }
    public void SaveTranslateTargetLanguage(string value) { Current.TranslateTargetLanguage = value ?? ""; KeyboardWtfState.TranslateTargetLanguage = Current.TranslateTargetLanguage; Save(); }
    public void SaveUseFillerWordCleaner(bool value) { Current.UseFillerWordCleaner = value; KeyboardWtfState.UseFillerWordCleaner = value; Save(); }
    public void SaveCaseTransform(CaseTransform value) { Current.CaseTransform = value; KeyboardWtfState.SelectedCaseTransform = value; Save(); }
    public void SaveAssistantName(string value) { Current.AssistantName = SanitizeAssistantName(value); KeyboardWtfState.AssistantName = Current.AssistantName; Save(); }
    public void SaveAssistantTone(AssistantTone value) { Current.AssistantTone = value; KeyboardWtfState.AssistantTone = value; Save(); }
    public void SaveGeminiVoice(string value) { Current.GeminiVoice = SanitizeGeminiVoice(value); KeyboardWtfState.GeminiVoice = Current.GeminiVoice; Save(); }
    public void SaveStartWithWindows(bool value) { Current.StartWithWindows = value; Save(); }
    public void SaveJarvisPermissionMode(JarvisPermissionMode value) { Current.JarvisPermissionMode = value; Save(); }
    public void SaveBrowserAutomationEnabled(bool value)
    {
        Current.BrowserAutomationEnabled = value && HasBrowserAutomationConsent;
        Save();
    }
    public void SaveBrowserAutomationConsent(bool accepted)
    {
        Current.BrowserAutomationConsentVersion = accepted
            ? CurrentBrowserAutomationConsentVersion
            : 0;
        if (!accepted)
            Current.BrowserAutomationEnabled = false;
        Save();
    }
    public void SaveBrowserAutomationModel(string value)
    {
        var model = (value ?? "").Trim();
        Current.BrowserAutomationModel = model.Length is > 0 and <= 80
            ? model
            : "gemini-3.1-flash-lite-preview";
        Save();
    }
    public void SaveBrowserAutomationMemoryEnabled(bool value)
    {
        Current.BrowserAutomationMemoryEnabled = value;
        Save();
    }
    public void SaveUpdateChannel(string channel)
    {
        Current.UpdateChannel = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase)
            ? "beta"
            : "stable";
        Save();
    }
    public void SavePreferredBrowser(string value)
    {
        Current.PreferredBrowser = BrowserLauncherService.NormalizeBrowser(value);
        Save();
    }

    public JarvisWorkflowSettings SaveWorkflow(string name, string apps, string urls, string folder)
    {
        var cleanName = CleanInline(name, 48);
        if (string.IsNullOrWhiteSpace(cleanName))
            throw new InvalidOperationException("Workflow name is required.");
        if (string.IsNullOrWhiteSpace(apps)
            && string.IsNullOrWhiteSpace(urls)
            && string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("A workflow needs at least one app, website, or folder.");

        Current.JarvisWorkflows ??= new List<JarvisWorkflowSettings>();
        var workflow = Current.JarvisWorkflows.FirstOrDefault(w =>
            string.Equals(w.Name, cleanName, StringComparison.OrdinalIgnoreCase));
        if (workflow == null)
        {
            workflow = new JarvisWorkflowSettings { Name = cleanName };
            Current.JarvisWorkflows.Add(workflow);
        }

        workflow.Apps = CleanInline(apps, 300);
        workflow.Urls = CleanInline(urls, 800);
        workflow.Folder = CleanInline(folder, 260);
        Save();
        return workflow;
    }

    public bool DeleteWorkflow(string name)
    {
        var removed = Current.JarvisWorkflows?.RemoveAll(w =>
            string.Equals(w.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            Save();
        return removed;
    }

    public void SaveCustomPrompt(string destinationName, string prompt)
    {
        var trimmed = string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            Current.CustomPrompts.Remove(destinationName);
            KeyboardWtfState.CustomPrompts.Remove(destinationName);
        }
        else
        {
            Current.CustomPrompts[destinationName] = trimmed;
            KeyboardWtfState.CustomPrompts[destinationName] = trimmed;
        }
        Save();
    }

    public void SaveHotkeys(HotkeySettings hotkeys)
    {
        Current.Hotkeys = hotkeys ?? HotkeySettings.Defaults();
        Save();
    }

    public void ResetHotkeys()
    {
        Current.Hotkeys = HotkeySettings.Defaults();
        Save();
    }

    public void SaveDefaultDestination(string destination)
    {
        Current.DefaultDestination = string.IsNullOrWhiteSpace(destination) ? "Clipboard" : destination;
        Save();
    }

    public void SaveTextDeliveryMode(VoiceTextDeliveryMode mode)
    {
        Current.TextDeliveryMode = mode;
        Save();
    }

    private static string SanitizeAssistantName(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Jarvis" : value.Trim();
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        if (string.IsNullOrWhiteSpace(name))
            return "Jarvis";
        return name.Length <= 32 ? name : name[..32];
    }

    private static string SanitizeGeminiVoice(string value) =>
        GeminiVoices.ContainsKey(value ?? "") ? value.Trim() : "Kore";

    private static string CleanInline(string value, int maxLength)
    {
        var clean = string.Join(" ", (value ?? "")
            .ReplaceLineEndings(" ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }
}
