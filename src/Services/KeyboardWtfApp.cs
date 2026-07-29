namespace KeyboardWtf.Services;

using System.Reflection;
using System.Windows.Forms;
using KeyboardWtf.Destinations;
using KeyboardWtf.Models;
using KeyboardWtf.Services.Ai;
using KeyboardWtf.Services.BrowserAutomationV2;

public sealed class KeyboardWtfApp : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly SettingsService _settings = new();
    private readonly AudioRecorderService _audioRecorder = new();
    private readonly SpeechRecognitionService _speechRecognition = new();
    private readonly ClaudeApiService _claudeApi = new();
    private readonly DestinationRouter _router = new();
    private readonly HotkeyService _hotkeys = new();
    private readonly IntentMemoryService _intentMemory = new();
    private readonly JarvisActionHistoryService _jarvisHistory = new();
    private readonly LearnedMappingService _learnedMappings = new();

    private NotificationService _notifications;
    private VoiceCaptureService _capture;
    private CommandRegistry _commands;
    private VoiceOverlayForm _overlay;
    private GeminiLiveConversationService _liveConversation;
    private JarvisAutomationService _jarvisAutomation;
    private BrowserHelperBridge _browserHelper;
    private BrowserAutomationPlanner _browserPlanner;
    private BrowserAutomationCoordinator _browserAutomation;
    private BrowserAutomationProgressForm _browserProgress;
    private System.Windows.Forms.Timer _startupHotkeyRetryTimer;
    private int _startupHotkeyRetryCount;
    private bool _disposed;

    public KeyboardWtfApp(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
    }

    public SettingsService Settings => _settings;
    public SpeechRecognitionService SpeechRecognition => _speechRecognition;
    public CommandRegistry Commands => _commands;
    public BrowserAutomationCoordinator BrowserAutomation => _browserAutomation;

    public void Start()
    {
        AppLog.Init();
        AppResources.Init(Assembly.GetExecutingAssembly());

        _notifications = new NotificationService(_notifyIcon);
        _settings.Load();
        _intentMemory.Load();
        _jarvisHistory.Load();
        _learnedMappings.Load();
        SyncStartupRegistration();
        ApplySecretEnvironmentOverrides();
        AiProviderRegistry.Initialize(_claudeApi);
        RegisterDestinations();

        _capture = new VoiceCaptureService(_audioRecorder, _speechRecognition, _notifications, _router);
        _liveConversation = new GeminiLiveConversationService(_notifications, _settings, _intentMemory);
        _browserHelper = new BrowserHelperBridge();
        _browserHelper.Start();
        _browserPlanner = new BrowserAutomationPlanner();
        _browserAutomation = new BrowserAutomationCoordinator(
            _settings,
            _notifications,
            _browserHelper,
            _browserPlanner);
        _jarvisAutomation = new JarvisAutomationService(
            _notifications,
            _settings,
            _jarvisHistory,
            _learnedMappings,
            OpenSettings);
        _commands = new CommandRegistry(
            this,
            _capture,
            _router,
            _settings,
            _notifications,
            _liveConversation,
            _jarvisAutomation,
            _jarvisHistory,
            _browserAutomation);
        _liveConversation.SetToolExecutor(_commands.ExecuteJarvisToolAsync);
        _liveConversation.SetUserTranscriptObserver(_commands.ObserveJarvisTranscript);
        _liveConversation.SetSessionEndingObserver(
            () => _browserAutomation.CancelAsync("Jarvis conversation ended"));
        _overlay = new VoiceOverlayForm(_audioRecorder);
        _browserProgress = new BrowserAutomationProgressForm(_browserAutomation);

        WebSettingsService.Instance.Initialize(
            this,
            _settings,
            _hotkeys,
            _intentMemory,
            _jarvisHistory,
            _learnedMappings,
            _jarvisAutomation,
            _browserAutomation);
        WebSettingsService.Instance.Start();

        _hotkeys.RegistrationFailed += (name, reason) => _notifications.Warning("Hotkey unavailable", $"{name}: {reason}");
        _hotkeys.RegistrationFallbackUsed += (name, requested, fallback) =>
            _notifications.Warning(
                "Hotkey conflict handled",
                $"{name}: {requested} is in use, so keyboard.wtf is using {fallback}. Change it in Settings if preferred.");
        if (!RegisterHotkeys()
            && !HotkeyService.HasDuplicates(_settings.Current.Hotkeys, out _))
            ScheduleStartupHotkeyRetry();
        LoadModelsAsync();
        StartPiperInstallInBackground();
        OpenFirstRunSetup();

        _notifications.Info("keyboard.wtf", "Stop typing. Say it.");
    }

    public bool RegisterHotkeys()
    {
        if (HotkeyService.HasDuplicates(_settings.Current.Hotkeys, out var duplicate))
        {
            _notifications.Warning("Duplicate hotkey", $"{duplicate} is assigned more than once. Reset or change it in settings.");
            return false;
        }

        return _hotkeys.RegisterDefaults(_settings.Current.Hotkeys, _commands);
    }

    private void ScheduleStartupHotkeyRetry()
    {
        _startupHotkeyRetryTimer?.Dispose();
        _startupHotkeyRetryCount = 0;
        _startupHotkeyRetryTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _startupHotkeyRetryTimer.Tick += (_, _) =>
        {
            _startupHotkeyRetryCount++;
            AppLog.Info($"Retrying hotkey registration after startup ({_startupHotkeyRetryCount}/3)");
            if (RegisterHotkeys() || _startupHotkeyRetryCount >= 3)
            {
                _startupHotkeyRetryTimer.Stop();
                _startupHotkeyRetryTimer.Dispose();
                _startupHotkeyRetryTimer = null;
            }
        };
        _startupHotkeyRetryTimer.Start();
    }

    public void OpenSettings() => WebSettingsService.Instance.OpenInBrowser();

    public async Task SendJarvisTextTurnAsync(string text, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("A Jarvis command is required.", nameof(text));
        if (_liveConversation == null)
            throw new InvalidOperationException("Jarvis is not initialized.");

        if (!_liveConversation.IsActive)
            await _liveConversation.StartAsync();
        if (!_liveConversation.IsActive)
            throw new InvalidOperationException("Jarvis could not start. Check the Gemini key, microphone, and app logs.");

        if (_commands?.TryPreRouteJarvisTranscript(text) == true)
            return;

        await _liveConversation.SendTextTurnAsync(
            BrowserAutomationText.Truncate(text.Trim(), 4000),
            token);
    }

    public string StartupExecutablePath => ResolveStartupExecutablePath();

    public void SyncStartupRegistration() =>
        WindowsStartupService.Sync(_settings.Current.StartWithWindows, StartupExecutablePath);

    private static void OpenFirstRunSetup()
    {
        if (!string.IsNullOrWhiteSpace(KeyboardWtfState.GeminiApiKey))
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(900);
            WebSettingsService.Instance.OpenInBrowser("#set-api-key");
        });
    }

    public void ToggleAi()
    {
        _settings.SaveUseAi(!KeyboardWtfState.UseAi);
        _notifications.Info("AI formatting", KeyboardWtfState.UseAi ? "Enabled." : "Disabled.");
    }

    public void SetDefaultDestination(string destination)
    {
        _settings.SaveDefaultDestination(destination);
        _notifications.Info("Default destination", destination);
    }

    private static void RegisterDestinations()
    {
        if (DestinationRegistry.All.Count > 0)
            return;

        if (OperatingSystem.IsWindows())
        {
            DestinationRegistry.Register(new ClipboardDestination());
            DestinationRegistry.Register(new TypeOutDestination());
        }

        DestinationRegistry.Register(new EmailDestination());
        DestinationRegistry.Register(new SlackWebhookDestination());
        DestinationRegistry.Register(new DiscordWebhookDestination());
        DestinationRegistry.Register(new TeamsDestination());
        DestinationRegistry.Register(new CalendarDestination());
        DestinationRegistry.Register(new WhatsAppDestination());
        DestinationRegistry.Register(new NotionDestination());
        DestinationRegistry.Register(new TelegramDestination());
    }

    private void ApplySecretEnvironmentOverrides()
    {
        var geminiKey = Environment.GetEnvironmentVariable("KEYBOARD_WTF_GEMINI_API_KEY");
        if (!string.IsNullOrWhiteSpace(geminiKey) && string.IsNullOrWhiteSpace(KeyboardWtfState.GeminiApiKey))
        {
            _settings.SaveGeminiApiKey(geminiKey.Trim());
            _settings.SaveAiProvider(AiProvider.Gemini);
            AppLog.Info("Gemini API key imported from environment and saved encrypted");
        }
    }

    private async void LoadModelsAsync()
    {
        try
        {
            AppLog.Info($"Models directory: {ModelManager.ModelsBaseDir}");
            if (OperatingSystem.IsWindows())
            {
                var runtimeWhisperDll = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "whisper.dll");
                var whisperDll = File.Exists(runtimeWhisperDll)
                    ? runtimeWhisperDll
                    : Path.Combine(AppContext.BaseDirectory, "whisper.dll");
                if (File.Exists(whisperDll))
                    Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = whisperDll;
            }

            if (ModelManager.IsVoskPresent)
                _speechRecognition.LoadVoskModel(ModelManager.VoskModelPath);
            if (ModelManager.IsWhisperPresent)
                _speechRecognition.LoadWhisperModel(ModelManager.WhisperModelPath);

            var needsDownload = !ModelManager.IsVoskPresent || !ModelManager.IsWhisperPresent;
            if (!needsDownload)
                return;

            KeyboardWtfState.IsDownloadingModels = true;
            await ModelManager.EnsureModelsAsync(status =>
            {
                KeyboardWtfState.ModelDownloadStatus = status;
                AppLog.Info($"Model download: {status}");
            });

            if (ModelManager.IsVoskPresent && !_speechRecognition.IsVoskLoaded)
                _speechRecognition.LoadVoskModel(ModelManager.VoskModelPath);
            if (ModelManager.IsWhisperPresent && !_speechRecognition.IsWhisperLoaded)
                _speechRecognition.LoadWhisperModel(ModelManager.WhisperModelPath);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Model loading failed");
            _notifications?.Warning("Speech models", "Model loading failed. Vosk/Whisper can be retried in settings.");
        }
        finally
        {
            KeyboardWtfState.IsDownloadingModels = false;
            KeyboardWtfState.ModelDownloadStatus = null;
        }
    }

    private static void StartPiperInstallInBackground()
    {
        if (PiperInstaller.IsFullyInstalled)
            return;

        _ = Task.Run(async () =>
        {
            KeyboardWtfState.IsInstallingPiper = true;
            try
            {
                await PiperInstaller.EnsureInstalledAsync(status =>
                {
                    KeyboardWtfState.PiperInstallStatus = status;
                    AppLog.Info($"Piper install: {status}");
                });
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Piper install failed");
            }
            finally
            {
                KeyboardWtfState.IsInstallingPiper = false;
                KeyboardWtfState.PiperInstallStatus = null;
            }
        });
    }

    private static string ResolveStartupExecutablePath()
    {
        var current = Environment.ProcessPath ?? Application.ExecutablePath;
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "keyboard.wtf",
            "keyboard.wtf.exe");
        return ResolveStartupExecutablePath(current, installed);
    }

    internal static string ResolveStartupExecutablePath(
        string current,
        string installed)
    {
        if (string.IsNullOrWhiteSpace(current))
            return installed ?? "";

        var directory = new DirectoryInfo(Path.GetDirectoryName(current) ?? "");
        for (var depth = 0; depth < 12 && directory != null; depth++, directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                // Validation binaries run directly from the source tree. They
                // must never replace the user's installed/startup executable.
                return !string.IsNullOrWhiteSpace(installed) && File.Exists(installed)
                    ? installed
                    : current;
            }
        }

        return current;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WebSettingsService.Instance?.Dispose();
        _hotkeys.Dispose();
        _startupHotkeyRetryTimer?.Dispose();
        _audioRecorder.Dispose();
        _speechRecognition.Dispose();
        _claudeApi.Dispose();
        _liveConversation?.Dispose();
        _browserProgress?.Dispose();
        _browserAutomation?.Dispose();
        _jarvisAutomation?.Dispose();
        _overlay?.Dispose();
    }
}
