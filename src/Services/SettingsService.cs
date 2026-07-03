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

public enum TaskClarificationMode
{
    SmartClarify,
    AlwaysAskAmbiguous,
    LetAiDecide,
}

public enum SmartBrowserBackendMode
{
    Auto,
    BrowserCompanionExtension,
    BrowserUseGemini,
    DirectPlaywright,
    BrowserOSExternal,
    StagehandExperimental,
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
    public string Jarvis { get; set; } = HotkeyService.JarvisDefault;
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
    public int SettingsSchemaVersion { get; set; } = SettingsService.CurrentSchemaVersion;
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
    public bool ShowTaskPopup { get; set; } = true;
    public bool AskForClarificationWhenLowConfidence { get; set; } = true;
    public TaskClarificationMode TaskClarificationMode { get; set; } = TaskClarificationMode.SmartClarify;
    public bool BrowserAgentEnabled { get; set; } = true;
    public SmartBrowserBackendMode SmartBrowserBackend { get; set; } = SmartBrowserBackendMode.Auto;
    public bool BrowserCompanionEnabled { get; set; } = true;
    public bool BrowserUseEnabled { get; set; } = true;
    public bool AllowAutomationProfileFallback { get; set; }
    public bool BrowserOSEnabled { get; set; }
    public bool LearnBrowserWorkflows { get; set; } = true;
    public int MaxBrowserTaskSeconds { get; set; } = 180;
    public bool BrowserAgentDeveloperDiagnostics { get; set; }
}

public sealed class SettingsService
{
    public const int CurrentSchemaVersion = 2;
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

    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "keyboard.wtf");

    public SettingsService(string settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(AppDataDir, "settings.json")
            : settingsPath;
    }

    public string SettingsPath { get; }

    public AppSettings Current { get; private set; } = new();

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

        var sourceSchemaVersion = hadSettingsFile
            && settingsJson.Contains("\"SettingsSchemaVersion\"", StringComparison.OrdinalIgnoreCase)
                ? Current.SettingsSchemaVersion
                : 0;
        Current.Hotkeys ??= HotkeySettings.Defaults();
        var migratedHotkeys = MigrateLegacyHotkeys(sourceSchemaVersion);
        if (hadSettingsFile && (sourceSchemaVersion < CurrentSchemaVersion || migratedHotkeys))
            BackupBeforeMigration();
        Current.SettingsSchemaVersion = CurrentSchemaVersion;
        Current.CustomPrompts ??= new Dictionary<string, string>();
        Current.JarvisWorkflows ??= new List<JarvisWorkflowSettings>();
        if (!hadSettingsFile || !settingsJson.Contains("\"StartWithWindows\"", StringComparison.OrdinalIgnoreCase))
            Current.StartWithWindows = true;
        Current.GeminiVoice = SanitizeGeminiVoice(Current.GeminiVoice);
        ApplyToState();
        Save();
    }

    private bool MigrateLegacyHotkeys(int sourceSchemaVersion)
    {
        var changed = false;
        if (Current.Hotkeys == null)
        {
            Current.Hotkeys = HotkeySettings.Defaults();
            return true;
        }

        if (string.Equals(Current.Hotkeys.PushToTalk, "Ctrl+Alt+Space", StringComparison.OrdinalIgnoreCase))
        {
            Current.Hotkeys.PushToTalk = HotkeySettings.Defaults().PushToTalk;
            changed = true;
        }
        if (string.Equals(Current.Hotkeys.Dictation, "Ctrl+Alt+V", StringComparison.OrdinalIgnoreCase))
        {
            Current.Hotkeys.Dictation = HotkeySettings.Defaults().Dictation;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(Current.Hotkeys.Jarvis))
        {
            Current.Hotkeys.Jarvis = !string.IsNullOrWhiteSpace(Current.Hotkeys.QuickSend)
                ? Current.Hotkeys.QuickSend
                : !string.IsNullOrWhiteSpace(Current.Hotkeys.CommandMode)
                    ? Current.Hotkeys.CommandMode
                    : HotkeySettings.Defaults().Jarvis;
            changed = true;
        }
        if (string.Equals(Current.Hotkeys.Jarvis, "Ctrl+Alt+S", StringComparison.OrdinalIgnoreCase))
        {
            Current.Hotkeys.Jarvis = HotkeySettings.Defaults().Jarvis;
            changed = true;
        }
        if (sourceSchemaVersion < 2
            && string.Equals(Current.Hotkeys.Jarvis, "Ctrl+Alt+Q", StringComparison.OrdinalIgnoreCase))
        {
            Current.Hotkeys.Jarvis = HotkeyService.JarvisDefault;
            changed = true;
            AppLog.Info($"Migrated the legacy Jarvis hotkey from Ctrl+Alt+Q to {HotkeyService.JarvisDefault}");
        }
        if (string.IsNullOrWhiteSpace(Current.Hotkeys.Cancel))
        {
            Current.Hotkeys.Cancel = HotkeySettings.Defaults().Cancel;
            changed = true;
        }

        changed |= !string.IsNullOrWhiteSpace(Current.Hotkeys.CommandMode)
            || !string.IsNullOrWhiteSpace(Current.Hotkeys.QuickSend);
        Current.Hotkeys.CommandMode = "";
        Current.Hotkeys.QuickSend = "";
        return changed;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Current, JsonOptions));
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private void BackupBeforeMigration()
    {
        try
        {
            var backupPath = SettingsPath + ".pre-v2.bak";
            if (!File.Exists(backupPath))
                File.Copy(SettingsPath, backupPath, overwrite: false);
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Could not create the pre-v2 settings backup");
        }
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
    public void SaveShowTaskPopup(bool value) { Current.ShowTaskPopup = value; Save(); }
    public void SaveAskForClarificationWhenLowConfidence(bool value)
    {
        Current.AskForClarificationWhenLowConfidence = value;
        Save();
    }
    public void SaveTaskClarificationMode(TaskClarificationMode value)
    {
        Current.TaskClarificationMode = value;
        Save();
    }
    public void SaveBrowserAgentEnabled(bool value)
    {
        Current.BrowserAgentEnabled = value;
        Save();
    }
    public void SaveSmartBrowserBackend(SmartBrowserBackendMode value)
    {
        Current.SmartBrowserBackend = value;
        Save();
    }
    public void SaveBrowserCompanionEnabled(bool value)
    {
        Current.BrowserCompanionEnabled = value;
        Save();
    }
    public void SaveBrowserUseEnabled(bool value)
    {
        Current.BrowserUseEnabled = value;
        Save();
    }
    public void SaveAllowAutomationProfileFallback(bool value)
    {
        Current.AllowAutomationProfileFallback = value;
        Save();
    }
    public void SaveBrowserOSEnabled(bool value)
    {
        Current.BrowserOSEnabled = value;
        Save();
    }
    public void SaveLearnBrowserWorkflows(bool value)
    {
        Current.LearnBrowserWorkflows = value;
        Save();
    }
    public void SaveMaxBrowserTaskSeconds(int value)
    {
        Current.MaxBrowserTaskSeconds = Math.Clamp(value, 30, 900);
        Save();
    }
    public void SaveBrowserAgentDeveloperDiagnostics(bool value)
    {
        Current.BrowserAgentDeveloperDiagnostics = value;
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
