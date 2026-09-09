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
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public List<JarvisWorkflowActionSettings> Actions { get; set; } = new();

    // Retained for Settings UI and backward compatibility with workflows
    // saved before the ordered-action representation was introduced.
    public string Apps { get; set; } = "";
    public string Urls { get; set; } = "";
    public string Folder { get; set; } = "";
}

public sealed class JarvisWorkflowActionSettings
{
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
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
    public List<string> DeletedJarvisWorkflowNames { get; set; } = new();
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
    private readonly object _saveLock = new();

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

    public bool IsFirstRun { get; private set; }

    public AppSettings Current { get; private set; } = new();

    public bool HasBrowserAutomationConsent =>
        Current.BrowserAutomationConsentVersion
        >= CurrentBrowserAutomationConsentVersion;

    public void Load()
    {
        Directory.CreateDirectory(AppDataDir);
        var hadSettingsFile = File.Exists(SettingsPath);
        IsFirstRun = !hadSettingsFile;
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
        if (string.Equals(Current.AssistantName, "Cursivis", StringComparison.OrdinalIgnoreCase))
            Current.AssistantName = "Jarvis";
        Current.CustomPrompts ??= new Dictionary<string, string>();
        Current.JarvisWorkflows ??= new List<JarvisWorkflowSettings>();
        Current.DeletedJarvisWorkflowNames ??= new List<string>();
        MigrateLegacyWorkflows();
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
        lock (_saveLock)
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var temporaryPath = SettingsPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
    }

    public void ApplyToState()
    {
        KeyboardWtfState.SelectedEngine = Current.SpeechEngine;
        KeyboardWtfState.SelectedLanguage = Current.Language ?? "auto";
        KeyboardWtfState.SelectedMicrophoneIndex = Current.MicrophoneDevice;
        KeyboardWtfState.MaxRecordingSeconds = NormalizeMaxRecordingSeconds(Current.MaxRecordingSeconds);
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
    public void SaveMaxRecordingSeconds(int value) { Current.MaxRecordingSeconds = NormalizeMaxRecordingSeconds(value); KeyboardWtfState.MaxRecordingSeconds = Current.MaxRecordingSeconds; Save(); }

    internal static int NormalizeMaxRecordingSeconds(int value) =>
        value == 0 ? 0 : Math.Clamp(value, 5, 3600);
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
        var actions = SplitWorkflowItems(apps)
            .Select(target => new JarvisWorkflowActionSettings { Kind = "auto", Target = target })
            .Concat(SplitWorkflowItems(urls)
                .Select(target => new JarvisWorkflowActionSettings { Kind = "url", Target = target }))
            .Concat(string.IsNullOrWhiteSpace(folder)
                ? Array.Empty<JarvisWorkflowActionSettings>()
                : [new JarvisWorkflowActionSettings { Kind = "folder", Target = folder }])
            .ToArray();
        return SaveWorkflow(name, [name], actions);
    }

    public JarvisWorkflowSettings SaveWorkflow(
        string name,
        IEnumerable<string> aliases,
        IEnumerable<JarvisWorkflowActionSettings> actions)
    {
        var cleanName = CleanInline(name, 48);
        if (string.IsNullOrWhiteSpace(cleanName))
            throw new InvalidOperationException("Workflow name is required.");

        var cleanAliases = (aliases ?? Array.Empty<string>())
            .Prepend(cleanName)
            .Select(value => CleanInline(value, 48))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        var cleanActions = (actions ?? Array.Empty<JarvisWorkflowActionSettings>())
            .Select(CleanWorkflowAction)
            .Where(action => action != null)
            .Take(24)
            .ToList();
        if (cleanActions.Count == 0)
            throw new InvalidOperationException("A workflow needs at least one app, website, or folder.");

        Current.JarvisWorkflows ??= new List<JarvisWorkflowSettings>();
        Current.DeletedJarvisWorkflowNames ??= new List<string>();
        Current.DeletedJarvisWorkflowNames.RemoveAll(item =>
            cleanAliases.Any(alias => string.Equals(
                FuzzyMatcher.Normalize(item),
                FuzzyMatcher.Normalize(alias),
                StringComparison.Ordinal)));
        var workflow = Current.JarvisWorkflows.FirstOrDefault(w =>
            string.Equals(FuzzyMatcher.Normalize(w.Name), FuzzyMatcher.Normalize(cleanName), StringComparison.Ordinal)
            || (w.Aliases ?? []).Any(alias => string.Equals(
                FuzzyMatcher.Normalize(alias),
                FuzzyMatcher.Normalize(cleanName),
                StringComparison.Ordinal)));
        if (workflow == null)
        {
            workflow = new JarvisWorkflowSettings { Name = cleanName };
            Current.JarvisWorkflows.Add(workflow);
        }

        workflow.Version = JarvisWorkflowSettings.CurrentVersion;
        workflow.Name = cleanName;
        workflow.Aliases = cleanAliases;
        workflow.Actions = cleanActions;
        SynchronizeLegacyWorkflowFields(workflow);
        Save();
        return workflow;
    }

    public JarvisWorkflowSettings UpdateWorkflow(
        string currentName,
        string newName,
        IEnumerable<string> aliases,
        IEnumerable<JarvisWorkflowActionSettings> actions)
    {
        var currentNormalized = FuzzyMatcher.Normalize(currentName);
        if (string.IsNullOrWhiteSpace(currentNormalized))
            throw new InvalidOperationException("Which workflow should I update?");

        lock (_saveLock)
        {
            Current.JarvisWorkflows ??= new List<JarvisWorkflowSettings>();
            Current.DeletedJarvisWorkflowNames ??= new List<string>();
            var workflow = Current.JarvisWorkflows.FirstOrDefault(candidate =>
                string.Equals(
                    FuzzyMatcher.Normalize(candidate.Name),
                    currentNormalized,
                    StringComparison.Ordinal)
                || (candidate.Aliases ?? []).Any(alias => string.Equals(
                    FuzzyMatcher.Normalize(alias),
                    currentNormalized,
                    StringComparison.Ordinal)));
            if (workflow == null)
                throw new InvalidOperationException($"Workflow {currentName} was not found.");

            var cleanName = CleanInline(newName, 48);
            if (string.IsNullOrWhiteSpace(cleanName))
                throw new InvalidOperationException("Workflow name is required.");
            var cleanAliases = (aliases ?? Array.Empty<string>())
                .Prepend(cleanName)
                .Select(value => CleanInline(value, 48))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            var cleanActions = (actions ?? Array.Empty<JarvisWorkflowActionSettings>())
                .Select(CleanWorkflowAction)
                .Where(action => action != null)
                .Take(24)
                .ToList();
            if (cleanActions.Count == 0)
                throw new InvalidOperationException("A workflow needs at least one app, website, or folder.");

            var newIdentities = cleanAliases
                .Select(FuzzyMatcher.Normalize)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);
            var collision = Current.JarvisWorkflows
                .Where(candidate => !ReferenceEquals(candidate, workflow))
                .FirstOrDefault(candidate => (candidate.Aliases ?? [])
                    .Prepend(candidate.Name)
                    .Select(FuzzyMatcher.Normalize)
                    .Any(newIdentities.Contains));
            if (collision != null)
                throw new InvalidOperationException($"That name or alias already belongs to workflow {collision.Name}.");

            var previousIdentities = (workflow.Aliases ?? [])
                .Prepend(workflow.Name)
                .Select(value => CleanInline(value, 48))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var previous in previousIdentities)
            {
                var normalized = FuzzyMatcher.Normalize(previous);
                if (newIdentities.Contains(normalized)
                    || Current.DeletedJarvisWorkflowNames.Any(item => string.Equals(
                        FuzzyMatcher.Normalize(item),
                        normalized,
                        StringComparison.Ordinal)))
                    continue;
                Current.DeletedJarvisWorkflowNames.Add(previous);
            }
            Current.DeletedJarvisWorkflowNames.RemoveAll(item =>
                newIdentities.Contains(FuzzyMatcher.Normalize(item)));

            workflow.Version = JarvisWorkflowSettings.CurrentVersion;
            workflow.Name = cleanName;
            workflow.Aliases = cleanAliases;
            workflow.Actions = cleanActions;
            SynchronizeLegacyWorkflowFields(workflow);
            Save();
            return workflow;
        }
    }

    public bool DeleteWorkflow(string name)
    {
        var normalized = FuzzyMatcher.Normalize(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;
        var matched = Current.JarvisWorkflows?.Where(w =>
            string.Equals(FuzzyMatcher.Normalize(w.Name), normalized, StringComparison.Ordinal)
            || (w.Aliases ?? []).Any(alias => string.Equals(
                FuzzyMatcher.Normalize(alias), normalized, StringComparison.Ordinal)))
            .ToArray() ?? [];
        var removed = matched.Length > 0;
        if (removed)
            Current.JarvisWorkflows.RemoveAll(workflow => matched.Contains(workflow));
        Current.DeletedJarvisWorkflowNames ??= new List<string>();
        var tombstones = matched
            .SelectMany(workflow => (workflow.Aliases ?? []).Prepend(workflow.Name))
            .Append(name)
            .Select(value => CleanInline(value, 48))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var tombstoneAdded = false;
        foreach (var tombstone in tombstones)
        {
            var tombstoneNormalized = FuzzyMatcher.Normalize(tombstone);
            if (Current.DeletedJarvisWorkflowNames.Any(item =>
                    string.Equals(FuzzyMatcher.Normalize(item), tombstoneNormalized, StringComparison.Ordinal)))
                continue;
            Current.DeletedJarvisWorkflowNames.Add(tombstone);
            tombstoneAdded = true;
        }
        if (removed || tombstoneAdded)
            Save();
        return removed || tombstoneAdded;
    }

    public bool IsWorkflowDeleted(string name)
    {
        var normalized = FuzzyMatcher.Normalize(name);
        return !string.IsNullOrWhiteSpace(normalized)
            && (Current.DeletedJarvisWorkflowNames ?? []).Any(item =>
                string.Equals(FuzzyMatcher.Normalize(item), normalized, StringComparison.Ordinal));
    }

    internal JarvisWorkflowSettings ReadPersistedWorkflow(string trigger)
    {
        var normalized = FuzzyMatcher.Normalize(trigger);
        if (string.IsNullOrWhiteSpace(normalized) || !File.Exists(SettingsPath))
            return null;
        lock (_saveLock)
        {
            var persisted = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath),
                JsonOptions);
            return persisted?.JarvisWorkflows?.FirstOrDefault(workflow =>
                string.Equals(FuzzyMatcher.Normalize(workflow.Name), normalized, StringComparison.Ordinal)
                || (workflow.Aliases ?? []).Any(alias => string.Equals(
                    FuzzyMatcher.Normalize(alias), normalized, StringComparison.Ordinal)));
        }
    }

    internal string ReadPersistedPreferredBrowser()
    {
        if (!File.Exists(SettingsPath))
            return "";
        lock (_saveLock)
        {
            var persisted = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath),
                JsonOptions);
            return BrowserLauncherService.NormalizeBrowser(persisted?.PreferredBrowser);
        }
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

    private void MigrateLegacyWorkflows()
    {
        foreach (var workflow in Current.JarvisWorkflows ?? [])
        {
            workflow.Aliases ??= new List<string>();
            workflow.Actions ??= new List<JarvisWorkflowActionSettings>();
            if (!workflow.Aliases.Any(alias => string.Equals(
                    FuzzyMatcher.Normalize(alias),
                    FuzzyMatcher.Normalize(workflow.Name),
                    StringComparison.Ordinal)))
            {
                workflow.Aliases.Insert(0, CleanInline(workflow.Name, 48));
            }
            if (workflow.Actions.Count == 0)
            {
                workflow.Actions.AddRange(SplitWorkflowItems(workflow.Apps)
                    .Select(target => new JarvisWorkflowActionSettings { Kind = "auto", Target = target }));
                workflow.Actions.AddRange(SplitWorkflowItems(workflow.Urls)
                    .Select(target => new JarvisWorkflowActionSettings { Kind = "url", Target = target }));
                if (!string.IsNullOrWhiteSpace(workflow.Folder))
                {
                    workflow.Actions.Add(new JarvisWorkflowActionSettings
                    {
                        Kind = "folder",
                        Target = CleanInline(workflow.Folder, 260),
                    });
                }
            }
            workflow.Actions = workflow.Actions
                .Select(CleanWorkflowAction)
                .Where(action => action != null)
                .Take(24)
                .ToList();
            workflow.Aliases = workflow.Aliases
                .Select(value => CleanInline(value, 48))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            workflow.Version = JarvisWorkflowSettings.CurrentVersion;
            SynchronizeLegacyWorkflowFields(workflow);
        }
    }

    private static JarvisWorkflowActionSettings CleanWorkflowAction(
        JarvisWorkflowActionSettings action)
    {
        var kind = (action?.Kind ?? "").Trim().ToLowerInvariant() switch
        {
            "application" or "native" or "native_app" => "app",
            "website" or "link" => "url",
            "directory" => "folder",
            "automatic" => "auto",
            var value => value,
        };
        if (kind is not ("app" or "auto" or "url" or "folder"))
            return null;
        var maxLength = kind == "url" ? 800 : kind == "folder" ? 260 : 120;
        var target = CleanInline(action.Target, maxLength);
        if (string.IsNullOrWhiteSpace(target))
            return null;
        if (kind == "url")
        {
            var candidate = target.Contains("://", StringComparison.Ordinal)
                ? target
                : "https://" + target;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return null;
            target = uri.AbsoluteUri;
        }
        return new JarvisWorkflowActionSettings { Kind = kind, Target = target };
    }

    private static void SynchronizeLegacyWorkflowFields(JarvisWorkflowSettings workflow)
    {
        workflow.Apps = CleanInline(string.Join(", ", workflow.Actions
            .Where(action => action.Kind is "app" or "auto")
            .Select(action => action.Target)), 300);
        workflow.Urls = CleanInline(string.Join(", ", workflow.Actions
            .Where(action => action.Kind == "url")
            .Select(action => action.Target)), 800);
        workflow.Folder = CleanInline(workflow.Actions
            .FirstOrDefault(action => action.Kind == "folder")?.Target, 260);
    }

    private static IEnumerable<string> SplitWorkflowItems(string value) =>
        (value ?? "").Split(
            [',', ';', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
