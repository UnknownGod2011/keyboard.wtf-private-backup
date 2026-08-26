namespace KeyboardWtf.Services;

using System.Reflection;
using System.Windows.Forms;
using KeyboardWtf.Destinations;
using KeyboardWtf.Models;
using KeyboardWtf.Services.Ai;
using KeyboardWtf.Services.BrowserAutomationV2;
using KeyboardWtf.Services.PaidStaging;

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
    private readonly BrowserNativeHostInstaller _browserNativeHostInstaller = new();
    private readonly HashSet<string> _reportedHotkeyFailures = new(StringComparer.OrdinalIgnoreCase);

    private NotificationService _notifications;
    private VoiceCaptureService _capture;
    private CommandRegistry _commands;
    private VoiceOverlayForm _overlay;
    private GeminiLiveConversationService _liveConversation;
    private JarvisAutomationService _jarvisAutomation;
    private BrowserHelperBridge _browserHelper;
    private BrowserAutomationPlanner _browserPlanner;
    private BrowserAutomationCoordinator _browserAutomation;
    private PaidAccountService _paidAccount;
    private JarvisTimerService _timers;
    private System.Windows.Forms.Timer _startupHotkeyRetryTimer;
    private int _startupHotkeyRetryCount;
    private bool _disposed;
    private int _restartRequested;

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
        _paidAccount = new PaidAccountService();
        if (!_paidAccount.PreserveExistingRegistrations)
        {
            SyncStartupRegistration();
            RegisterBrowserNativeHost();
        }
        else
        {
            AppLog.Info("Isolated paid candidate: startup and native-host registrations were left unchanged");
        }
        ApplySecretEnvironmentOverrides();
        AiProviderRegistry.Initialize(_claudeApi, _paidAccount);
        RegisterDestinations();

        _capture = new VoiceCaptureService(_audioRecorder, _speechRecognition, _notifications, _router);
        _liveConversation = new GeminiLiveConversationService(
            _notifications,
            _settings,
            _intentMemory,
            _paidAccount);
        _browserHelper = new BrowserHelperBridge();
        _browserHelper.Start();
        _browserPlanner = new BrowserAutomationPlanner(_paidAccount);
        _browserAutomation = new BrowserAutomationCoordinator(
            _settings,
            _notifications,
            _browserHelper,
            _browserPlanner,
            _paidAccount);
        _timers = new JarvisTimerService(_notifications, _jarvisHistory);
        _browserAutomation.AnnouncementRequested += HandleLifecycleAnnouncement;
        _timers.AnnouncementRequested += HandleLifecycleAnnouncement;
        _jarvisAutomation = new JarvisAutomationService(
            _notifications,
            _settings,
            _jarvisHistory,
            _learnedMappings,
            () => OpenSettings(),
            _timers,
            _paidAccount,
            _browserHelper);
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
        _liveConversation.SetSessionEndingObserver(_commands.HandleJarvisSessionEndingAsync);
        _overlay = new VoiceOverlayForm(_audioRecorder);
        _overlay.InitializeUiThreadHandle();

        WebSettingsService.Instance.Initialize(
            this,
            _settings,
            _hotkeys,
            _intentMemory,
            _jarvisHistory,
            _learnedMappings,
            _jarvisAutomation,
            _browserAutomation,
            _paidAccount);
        WebSettingsService.Instance.Start();
        if (_paidAccount.Enabled)
            _ = InitializePaidAccountAsync();

        _hotkeys.RegistrationFailed += (name, reason) =>
        {
            if (MarkHotkeyFailureNotified(_reportedHotkeyFailures, name, reason))
                _notifications.Warning("Hotkey unavailable", $"{name}: {reason}");
            else
                AppLog.Info($"Hotkey remains unavailable during bounded startup retry: {name}");
        };
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

    public bool TryUpdateHotkeys(HotkeySettings requested, out string error)
    {
        if (!_hotkeys.TryReplaceAll(
                requested,
                _settings.Current.Hotkeys,
                _commands,
                requireAllRequested: false,
                out error))
            return false;
        _settings.SaveHotkeys(requested);
        return true;
    }

    public bool TryResetHotkeys(out string error)
    {
        var defaults = HotkeySettings.Defaults();
        if (!_hotkeys.TryReplaceAll(
                defaults,
                _settings.Current.Hotkeys,
                _commands,
                requireAllRequested: true,
                out error))
            return false;
        _settings.SaveHotkeys(defaults);
        return true;
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

    internal static bool MarkHotkeyFailureNotified(
        ISet<string> reportedFailures,
        string name,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(reportedFailures);
        return reportedFailures.Add($"{name ?? ""}\u001f{reason ?? ""}");
    }

    public void OpenSettings(string section = "") =>
        WebSettingsService.Instance.OpenInBrowser(SettingsSectionRegistry.Resolve(section));

    private async Task InitializePaidAccountAsync()
    {
        try
        {
            await _paidAccount.InitializeAsync();
        }
        catch (Exception exception)
        {
            AppLog.Warning(exception, "Paid account initialization failed");
        }
    }

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

        // Start deterministic or browser work promptly, but still let the
        // Live conversation receive the turn. CommandRegistry supplies the
        // matching verified receipt to the model, which lets Jarvis say
        // "started" or "switched" rather than silently returning early.
        _commands?.TryPreRouteJarvisTranscript(text, fromLocalTextSubmission: true);

        await _liveConversation.SendTextTurnAsync(
            BrowserAutomationText.Truncate(text.Trim(), 4000),
            token);
    }

    public string StartupExecutablePath => ResolveStartupExecutablePath();

    public bool RequestRestart(out string error)
    {
        error = "";
        if (Interlocked.Exchange(ref _restartRequested, 1) != 0)
            return true;

        _commands?.CancelCurrent();
        if (!WindowsAppLifecycleService.StartRestartHelper(out error))
        {
            Interlocked.Exchange(ref _restartRequested, 0);
            return false;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(350);
            Application.Exit();
        });
        return true;
    }

    public void RequestExitForUpdate()
    {
        _commands?.CancelCurrent();
        _ = Task.Run(async () =>
        {
            await Task.Delay(350);
            Application.Exit();
        });
    }

    private void RegisterBrowserNativeHost()
    {
        try
        {
            var executable = Environment.ProcessPath ?? Application.ExecutablePath;
            _browserNativeHostInstaller.Register(executable);
            AppLog.Info("Chrome Helper native host registration is ready");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Chrome Helper native host registration failed: {ex.Message}");
        }
    }

    private void HandleLifecycleAnnouncement(JarvisLifecycleAnnouncement announcement)
    {
        if (_disposed || announcement == null)
            return;
        _ = DeliverLifecycleAnnouncementAsync(announcement);
    }

    private async Task DeliverLifecycleAnnouncementAsync(
        JarvisLifecycleAnnouncement announcement)
    {
        var deliveredByJarvis = false;
        try
        {
            deliveredByJarvis = _liveConversation != null
                && await _liveConversation.TryAnnounceLifecycleAsync(announcement);
            if (deliveredByJarvis)
            {
                AppLog.Info(
                    $"Lifecycle announcement delivered through Jarvis Live: source={announcement.Source} state={announcement.State}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                ex,
                $"Lifecycle delivery failed: source={announcement.Source} state={announcement.State}");
        }

        if (!deliveredByJarvis
            && !_disposed
            && !string.IsNullOrWhiteSpace(announcement.FallbackSpeech))
        {
            // The Live conversation owns normal spoken updates. Local TTS is
            // retained only so a timer or background task is not silently
            // lost when Jarvis Live is disconnected.
            AppLog.Info(
                $"Lifecycle announcement used local speech fallback: source={announcement.Source} state={announcement.State}");
            TtsService.Instance.Speak(announcement.FallbackSpeech, "en");
        }
    }

    public void SyncStartupRegistration() =>
        WindowsStartupService.Sync(_settings.Current.StartWithWindows, StartupExecutablePath);

    private void OpenFirstRunSetup()
    {
        if (_paidAccount?.Enabled == true)
        {
            if (_settings.IsFirstRun)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(900);
                    WebSettingsService.Instance.OpenInBrowser("#getting-started");
                });
            return;
        }
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
        if (_paidAccount?.Enabled == true && !_paidAccount.EmergencyByokEnabled)
            return;
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

            KeyboardWtfState.IsDownloadingModels = true;
            void ReportModelStatus(string status)
            {
                KeyboardWtfState.ModelDownloadStatus = status;
                AppLog.Info($"Model download: {status}");
            }

            var voskReady = ModelManager.IsVoskPresent
                || await ModelManager.DownloadVoskModelAsync(ReportModelStatus);

            if (voskReady && ModelManager.IsVoskPresent && !_speechRecognition.IsVoskLoaded)
                _speechRecognition.LoadVoskModel(ModelManager.VoskModelPath);
            else if (!voskReady)
                _notifications?.Warning(
                    "Dictation model unavailable",
                    "The local Vosk model could not be downloaded. Check your connection and retry in Settings.");

            // Vosk is available after the small first download, so Dictation
            // can start while the more accurate Whisper model continues.
            var whisperReady = ModelManager.IsWhisperPresent
                || await ModelManager.DownloadWhisperModelAsync(ReportModelStatus);

            if (whisperReady && ModelManager.IsWhisperPresent && !_speechRecognition.IsWhisperLoaded)
                _speechRecognition.LoadWhisperModel(ModelManager.WhisperModelPath);
            else if (!whisperReady)
                _notifications?.Warning(
                    "Accurate speech model unavailable",
                    "Whisper could not be downloaded. Fast local Dictation remains available when Vosk is ready.");
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
        _timers?.Dispose();
        _browserAutomation?.Dispose();
        _paidAccount?.Dispose();
        _jarvisAutomation?.Dispose();
        _overlay?.Dispose();
    }
}
