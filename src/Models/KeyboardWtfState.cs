namespace KeyboardWtf.Models;

public enum SpeechEngine
{
    Vosk,
    Whisper
}

public enum RecordingMode
{
    None,
    Dictate,
    VoiceNote,
    PushToTalk,
    QuickSend,
    VoiceAssistant,
    Command,
    JarvisPreflight
}

public enum VoiceUiPhase
{
    Idle,
    Listening,
    Transcribing,
    Thinking,
    Executing,
    Speaking,
    Done,
    Cancelled,
    Error
}

public enum WhisperModelSize
{
    Base,   // ~140 MB - fast, lower accuracy with accents
    Small   // ~460 MB - slower, much better with accents
}

public enum DestinationCategory
{
    Raw,   // Clipboard, Type: transcript sent as-is, no AI filtering
    Ai     // Email, Slack, Discord, Teams, Calendar: AI reformats per destination's AiPrompt
}

public enum CaseTransform
{
    None,
    Upper,
    Lower,
    Title
}

public enum AssistantTone
{
    Balanced,
    Concise,
    Friendly,
    Professional
}

public enum AiProvider
{
    Claude,
    OpenAi,
    Gemini,
    DeepSeek,
    Perplexity
}

public class ChatMessage
{
    public string Role { get; set; }
    public string Content { get; set; }
}

// Central runtime state shared by the tray app, hotkeys, capture pipeline, and settings service.
public static class KeyboardWtfState
{
    public static SpeechEngine SelectedEngine { get; set; } = SpeechEngine.Whisper;
    public static string SelectedLanguage { get; set; } = "auto";
    public static int SelectedMicrophoneIndex { get; set; } = -1; // -1 = system default
    public static int MaxRecordingSeconds { get; set; } = 30;
    public static WhisperModelSize SelectedWhisperModel { get; set; } = WhisperModelSize.Small;
    public static string VoiceNoteSavePath { get; set; } // null/empty = use default
    public static string EffectiveVoiceNoteSavePath =>
        string.IsNullOrWhiteSpace(VoiceNoteSavePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "keyboard.wtf")
            : VoiceNoteSavePath;

    public const string DefaultVoiceNoteFilenamePattern = "yyyy-MM-dd_HH-mm-ss";
    public static string VoiceNoteFilenamePattern { get; set; } // null/empty = use default

    // 5 entries is enough context for "go back and resend that" use cases without
    // making transcript history unnecessarily large.
    public const int MaxHistorySize = 5;
    public static List<string> TranscriptHistory { get; } = new();
    public static bool AppendMode { get; set; }

    private static string _currentTranscript = string.Empty;
    public static string CurrentTranscript
    {
        get => _currentTranscript;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                (TranscriptHistory.Count == 0 || TranscriptHistory[0] != value))
            {
                TranscriptHistory.Insert(0, value);
                if (TranscriptHistory.Count > MaxHistorySize)
                    TranscriptHistory.RemoveAt(TranscriptHistory.Count - 1);
            }
            _currentTranscript = value;
            TranscriptChanged?.Invoke();
        }
    }
    public static event Action TranscriptChanged;

    // Append mode is intentionally single-level: recordings concatenate into one flat string
    // rather than forming a list. This keeps Send/AI formatting simple — one prompt, one result.
    // Future: multi-segment transcripts with per-segment timestamps would enable selective editing.
    public static void SetTranscript(string transcript)
    {
        if (AppendMode && !string.IsNullOrWhiteSpace(_currentTranscript) && !string.IsNullOrWhiteSpace(transcript))
            CurrentTranscript = _currentTranscript.TrimEnd() + " " + transcript;
        else
            CurrentTranscript = transcript;
    }

    // Two independent indices, one per dial. Each cycles within its own category's destinations.
    public static int SelectedRawIndex { get; set; }
    public static int SelectedAiIndex { get; set; }
    // Whichever dial the user last scrolled is the "active" one that SendAction targets.
    public static DestinationCategory ActiveCategory { get; set; } = DestinationCategory.Ai;
    public static Dictionary<string, string> FormattedOutputs { get; } = new();
    public static bool IsRecording { get; set; }
    public static RecordingMode CurrentRecordingMode { get; set; } = RecordingMode.None;
    public static string ActiveQuickSendTarget { get; set; } // destination name during a QuickSend recording
    public static bool IsProcessing { get; set; }
    public static bool IsProcessingAi { get; set; }
    public static VoiceUiPhase UiPhase { get; private set; } = VoiceUiPhase.Idle;
    public static string UiTitle { get; private set; } = "keyboard.wtf";
    public static string UiDetail { get; private set; } = "Ready";
    public static DateTime UiPhaseStartedUtc { get; private set; } = DateTime.UtcNow;
    public static double InputLevelDb { get; set; } = -96;
    public static bool IsDownloadingModels { get; set; }
    public static string ModelDownloadStatus { get; set; }
    public static bool IsInstallingPiper { get; set; }
    public static string PiperInstallStatus { get; set; }
    public static string LastSendResult { get; set; }
    public static string LastSendDestination { get; set; }
    public static string AssistantName { get; set; } = "Jarvis";
    public static AssistantTone AssistantTone { get; set; } = AssistantTone.Balanced;
    public static string GeminiVoice { get; set; } = "Kore";

    public static void SetUi(VoiceUiPhase phase, string title, string detail)
    {
        UiPhase = phase;
        UiTitle = title ?? "keyboard.wtf";
        UiDetail = detail ?? "";
        UiPhaseStartedUtc = DateTime.UtcNow;
    }

    // Noise gate: silence frames below -38 dBFS before sending to speech engines
    public static bool UseNoiseGate { get; set; } = true;

    // Transcription post-processing
    // Empty string means "off" rather than null so settings serialization is clean (no null checks in GET /api/settings).
    public static string TranslateTargetLanguage { get; set; } = ""; // "" = off, "en"/"de"/"fr"/"es"/"it" etc.
    public static bool UseFillerWordCleaner { get; set; } = false;
    public static CaseTransform SelectedCaseTransform { get; set; } = CaseTransform.None;
    public static string DetectedLanguage { get; set; }

    // AI toggle + custom prompts per destination
    public static bool UseAi { get; set; } = true;
    public static Dictionary<string, string> CustomPrompts { get; } = new();

    public static string GetEffectivePrompt(string destinationName, string defaultPrompt)
    {
        if (CustomPrompts.TryGetValue(destinationName, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;
        return defaultPrompt;
    }

    // AI provider selection + keys
    public static AiProvider SelectedAiProvider { get; set; } = AiProvider.Claude;
    public static string ClaudeApiKey { get; set; }
    public static string OpenAiApiKey { get; set; }
    public static string GeminiApiKey { get; set; }
    public static string DeepSeekApiKey { get; set; }
    public static string PerplexityApiKey { get; set; }
    public static bool IsClaudeAvailable => !string.IsNullOrEmpty(ClaudeApiKey);

    // Destination API keys/URLs
    public static string SlackWebhookUrl { get; set; }
    public static string DiscordWebhookUrl { get; set; }
    public static string TeamsWebhookUrl { get; set; }
}
