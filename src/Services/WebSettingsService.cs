namespace KeyboardWtf.Services;

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KeyboardWtf.Models;
using NAudio.Wave;

public sealed class WebSettingsService : IDisposable
{
    private static readonly JsonSerializerOptions CompanionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static WebSettingsService _instance;
    private static readonly object _lock = new();

    private HttpListener _listener;
    private CancellationTokenSource _cts;
    private Task _serverTask;
    private bool _disposed;

    private KeyboardWtfApp _app;
    private SettingsService _settings;
    private HotkeyService _hotkeys;
    private IntentMemoryService _intentMemory;
    private JarvisActionHistoryService _jarvisHistory;
    private LearnedMappingService _learnedMappings;
    private JarvisAutomationService _jarvisAutomation;
    private JarvisTaskManager _taskManager;
    private BrowserAgentService _browserAgent;
    private BrowserCompanionBridgeService _browserCompanion;
    private string _settingsHtml;

    // Live microphone meter state
    private WaveInEvent _liveMic;
    private volatile int _liveMicPeak;
    private readonly object _liveMicLock = new();

    public int Port { get; private set; }
    public bool IsRunning { get; private set; }

    private WebSettingsService() { }

    public static WebSettingsService Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new WebSettingsService();
                }
            }
            return _instance;
        }
    }

    public void Initialize(
        KeyboardWtfApp app,
        SettingsService settings,
        HotkeyService hotkeys,
        IntentMemoryService intentMemory,
        JarvisActionHistoryService jarvisHistory,
        LearnedMappingService learnedMappings,
        JarvisAutomationService jarvisAutomation,
        JarvisTaskManager taskManager,
        BrowserAgentService browserAgent,
        BrowserCompanionBridgeService browserCompanion = null)
    {
        _app = app;
        _settings = settings;
        _hotkeys = hotkeys;
        _intentMemory = intentMemory;
        _jarvisHistory = jarvisHistory;
        _learnedMappings = learnedMappings;
        _jarvisAutomation = jarvisAutomation;
        _taskManager = taskManager;
        _browserAgent = browserAgent;
        _browserCompanion = browserCompanion;
        _settingsHtml = LoadSettingsHtml();
    }

    public void Start()
    {
        if (IsRunning) return;

        Port = FindAvailablePort(8090);
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");

        try
        {
            _listener.Start();
            IsRunning = true;
            _serverTask = Task.Run(() => ListenLoop(_cts.Token));
            AppLog.Info($"WebSettingsService started on port {Port}");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to start web server: {ex.Message}");
            _listener = null;
        }
    }

    public void OpenInBrowser(string fragment = "")
    {
        if (!IsRunning) Start();
        if (!IsRunning) return;

        var suffix = string.IsNullOrWhiteSpace(fragment)
            ? ""
            : fragment.StartsWith("#", StringComparison.Ordinal) ? fragment : "#" + fragment;
        var url = $"http://localhost:{Port}/{suffix}";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to open browser: {ex.Message}");
        }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequest(context), ct);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                AppLog.Error($"Web server error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var req = context.Request;
        var resp = context.Response;

        try
        {
            // Security: only serve localhost
            if (!req.IsLocal)
            {
                resp.StatusCode = 403;
                resp.Close();
                return;
            }

            var path = req.Url.AbsolutePath;
            var method = req.HttpMethod;

            if (path == "/" && method == "GET")
            {
                await ServeHtml(resp);
            }
            else if (path == "/browser-agent/mock-form" && method == "GET")
            {
                await ServeBrowserAgentMockForm(resp);
            }
            else if (path == "/browser-agent/mock-store" && method == "GET")
            {
                await ServeBrowserAgentMockStore(resp);
            }
            else if (path == "/api/settings" && method == "GET")
            {
                await ServeSettings(resp);
            }
            else if (path == "/api/browser-companion/status" && method == "GET")
            {
                await ServeBrowserCompanionStatus(resp);
            }
            else if (path == "/api/browser-companion/graph" && method == "GET")
            {
                if (!HasSettingsActionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, new { ok = false, error = "Settings action header required." });
                    return;
                }
                await ServeBrowserCompanionGraph(resp);
            }
            else if (path == "/api/browser-companion/command" && method == "POST")
            {
                if (!HasSettingsActionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, new { ok = false, error = "Settings action header required." });
                    return;
                }
                await RunBrowserCompanionCommand(req, resp);
            }
            else if (path == "/api/browser-companion/test" && method == "POST")
            {
                if (!HasSettingsActionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, new { ok = false, error = "Settings action header required." });
                    return;
                }
                await TestBrowserCompanionConnection(resp);
            }
            else if (path == "/api/browser-companion/register" && method == "POST")
            {
                if (!HasCompanionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, new { ok = false, error = "Browser Companion header required." });
                    return;
                }
                await RegisterBrowserCompanion(req, resp);
            }
            else if (path == "/api/browser-companion/graph" && method == "POST")
            {
                if (!HasCompanionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, new { ok = false, error = "Browser Companion header required." });
                    return;
                }
                await ReceiveBrowserCompanionGraph(req, resp);
            }
            else if (path == "/api/browser-companion/result" && method == "POST")
            {
                if (!HasCompanionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, new { ok = false, error = "Browser Companion header required." });
                    return;
                }
                await ReceiveBrowserCompanionResult(req, resp);
            }
            else if (path == "/api/browser-companion/next-command" && method == "GET")
            {
                if (!HasCompanionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, new { ok = false, error = "Browser Companion header required." });
                    return;
                }
                await ServeBrowserCompanionCommand(req, resp);
            }
            else if (path == "/api/settings" && method == "POST")
            {
                await SaveSettings(req, resp);
            }
            else if (path == "/api/hotkeys/reset" && method == "POST")
            {
                _settings?.ResetHotkeys();
                _app?.RegisterHotkeys();
                await WriteJson(resp, new { ok = true });
            }
            else if (path == "/api/intent-memory/delete" && method == "POST")
            {
                await DeleteIntentMemory(req, resp);
            }
            else if (path == "/api/intent-memory/remember" && method == "POST")
            {
                await RememberIntentMemory(req, resp);
            }
            else if (path == "/api/intent-memory/update" && method == "POST")
            {
                await UpdateIntentMemory(req, resp);
            }
            else if (path == "/api/intent-memory/clear" && method == "POST")
            {
                _intentMemory?.Clear();
                await WriteJson(resp, new { ok = true });
            }
            else if (path == "/api/jarvis-history/clear" && method == "POST")
            {
                _jarvisHistory?.Clear();
                await WriteJson(resp, new { ok = true });
            }
            else if (path == "/api/learned-mappings/delete" && method == "POST")
            {
                await DeleteLearnedMapping(req, resp);
            }
            else if (path == "/api/learned-mappings/clear" && method == "POST")
            {
                var removed = _learnedMappings?.Clear() ?? 0;
                await WriteJson(resp, new { ok = true, removed });
            }
            else if (path == "/api/jarvis-workflow/delete" && method == "POST")
            {
                await DeleteJarvisWorkflow(req, resp);
            }
            else if (path == "/api/jarvis/stop" && method == "POST")
            {
                _app?.Commands?.CancelCurrent();
                await WriteJson(resp, new { ok = true });
            }
            else if (path == "/api/browser-agent/demo" && method == "POST")
            {
                await RunBrowserAgentDemo(req, resp);
            }
            else if (path == "/api/browser-agent/start" && method == "POST")
            {
                if (!HasSettingsActionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Settings action header required." }));
                    return;
                }
                await StartBrowserAgentTask(req, resp);
            }
            else if (path == "/api/browser-agent/observe" && method == "POST")
            {
                if (!HasSettingsActionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Settings action header required." }));
                    return;
                }
                await ObserveBrowserAgentTask(req, resp);
            }
            else if (path == "/api/browser-agent/action" && method == "POST")
            {
                if (!HasSettingsActionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Settings action header required." }));
                    return;
                }
                await ActBrowserAgentTask(req, resp);
            }
            else if (path == "/api/browser-agent/finish" && method == "POST")
            {
                if (!HasSettingsActionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Settings action header required." }));
                    return;
                }
                await FinishBrowserAgentTask(req, resp);
            }
            else if (path == "/api/browser-agent/cancel" && method == "POST")
            {
                await CancelBrowserAgentTask(req, resp);
            }
            else if (path == "/api/browser-agent/confirm" && method == "POST")
            {
                if (!HasSettingsActionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Settings action header required." }));
                    return;
                }
                await ConfirmBrowserAgentTask(req, resp);
            }
            else if (path == "/api/microphones" && method == "GET")
            {
                await ServeMicrophones(resp);
            }
            else if (path == "/api/mic-test" && method == "POST")
            {
                await RunMicTest(resp);
            }
            else if (path == "/api/mic-test-start" && method == "POST")
            {
                await StartLiveMicTest(resp);
            }
            else if (path == "/api/mic-level" && method == "GET")
            {
                await GetLiveMicLevel(resp);
            }
            else if (path == "/api/mic-test-stop" && method == "POST")
            {
                await StopLiveMicTest(resp);
            }
            else if (path == "/api/camera-test" && method == "POST")
            {
                if (!HasSettingsActionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Settings action header required." }));
                    return;
                }
                await RunCameraTest(resp);
            }
            else if (path == "/api/camera-open" && method == "POST")
            {
                if (!HasSettingsActionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Settings action header required." }));
                    return;
                }
                await WriteJson(
                    resp,
                    JsonSerializer.Serialize(
                        _jarvisAutomation?.OpenCameraFromSettings()
                        ?? new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["message"] = "Camera service is unavailable.",
                        }));
            }
            else if (path == "/api/screen-guidance-test" && method == "POST")
            {
                if (!HasSettingsActionHeader(req))
                {
                    resp.StatusCode = 403;
                    await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Settings action header required." }));
                    return;
                }
                await RunScreenGuidanceTest(resp);
            }
            else if (path == "/api/download-models" && method == "POST")
            {
                await DownloadModels(resp);
            }
            else if (path == "/api/tts/install" && method == "POST")
            {
                await InstallPiper(resp);
            }
            else if (path == "/api/tts/test" && method == "POST")
            {
                await TestTts(req, resp);
            }
            else if (path == "/api/test-claude" && method == "POST")
            {
                await TestClaude(resp);
            }
            else if (path == "/api/test-ai" && method == "POST")
            {
                await TestAiProvider(req, resp);
            }
            else if (path == "/api/open-voice-notes" && method == "POST")
            {
                await OpenVoiceNotesFolder(resp);
            }
            else if (path == "/api/pick-voice-notes-folder" && method == "POST")
            {
                await PickVoiceNotesFolder(resp);
            }
            else if (path == "/api/preview-filename" && method == "POST")
            {
                await PreviewFilenameEndpoint(req, resp);
            }
            else if (path == "/api/exit" && method == "POST")
            {
                await WriteJson(resp, new { ok = true });
                _ = Task.Run(async () =>
                {
                    await Task.Delay(250);
                    System.Windows.Forms.Application.Exit();
                });
            }
            else
            {
                resp.StatusCode = 404;
                resp.Close();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Request handler error: {ex.Message}");
            try
            {
                resp.StatusCode = 500;
                resp.Close();
            }
            catch { }
        }
    }

    private async Task ServeHtml(HttpListenerResponse resp)
    {
        var html = _settingsHtml ?? "<h1>Settings page not found</h1>";
        var buffer = Encoding.UTF8.GetBytes(html);
        resp.ContentType = "text/html; charset=utf-8";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private BrowserBackendStatus BrowserCompanionStatusSnapshot() =>
        _browserCompanion?.DetailedStatus(
            Port,
            _settings?.Current.BrowserUseEnabled == true,
            _settings?.Current.AllowAutomationProfileFallback == true)
        ?? new BrowserBackendStatus
        {
            Initialized = true,
            Available = false,
            BrowserChannel = "Browser Companion Extension",
            ConnectionState = "Error",
            BridgePort = Port,
            BridgeUrl = Port > 0 ? $"http://localhost:{Port}" : "",
            BrowserUseFallbackEnabled = _settings?.Current.BrowserUseEnabled == true,
            AutomationProfileFallbackEnabled = _settings?.Current.AllowAutomationProfileFallback == true,
            LastError = "Browser Companion bridge is unavailable.",
        };

    private static IReadOnlyList<string> TopVisibleControls(BrowserCompanionControlGraph graph) =>
        graph?.Controls?
            .Where(control => control.Visible && !control.Sensitive)
            .Select(control => string.Join(
                " ",
                new[]
                {
                    control.ControlType,
                    control.Role,
                    control.Text,
                    control.AriaLabel,
                    control.Title,
                    control.Placeholder,
                }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray()
        ?? Array.Empty<string>();

    private async Task ServeBrowserCompanionStatus(HttpListenerResponse resp)
    {
        var status = BrowserCompanionStatusSnapshot();
        var graph = _browserCompanion?.LastGraph;
        await WriteJson(resp, new
        {
            ok = true,
            status,
            graph = graph == null
                ? null
                : new
                {
                    graph.Url,
                    graph.Title,
                    graph.PageType,
                    graph.GraphVersion,
                    graph.CapturedAt,
                    controlCount = graph.Controls.Count,
                },
        });
    }

    private async Task TestBrowserCompanionConnection(HttpListenerResponse resp)
    {
        if (_browserCompanion == null)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new
            {
                ok = false,
                connectionState = "Error",
                error = "Browser Companion bridge is unavailable.",
            });
            return;
        }

        var status = BrowserCompanionStatusSnapshot();
        if (!status.Available)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new
            {
                ok = false,
                status,
                connectionState = status.ConnectionState,
                error = status.LastError,
            });
            return;
        }

        var result = await _browserCompanion.ExecuteAsync(
            new BrowserCompanionCommand
            {
                Type = "getGraph",
                TimeoutMs = 12000,
            },
            TimeSpan.FromSeconds(12),
            CancellationToken.None);
        var graph = result.Graph ?? _browserCompanion.LastGraph;
        var topControls = TopVisibleControls(graph);
        if (!result.Ok)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new
            {
                ok = false,
                status = BrowserCompanionStatusSnapshot(),
                connectionState = BrowserCompanionStatusSnapshot().ConnectionState,
                activeTabUrl = graph?.Url ?? status.ActiveTabUrl,
                controlsFound = graph?.Controls?.Count ?? 0,
                topControls,
                error = result.Message,
                result.Status,
            });
            return;
        }

        await WriteJson(resp, new
        {
            ok = true,
            status = BrowserCompanionStatusSnapshot(),
            connectionState = "Connected",
            activeTabUrl = graph?.Url ?? "",
            pageTitle = graph?.Title ?? "",
            pageType = graph?.PageType ?? "",
            controlsFound = graph?.Controls?.Count ?? 0,
            topControls,
            lastError = BrowserCompanionStatusSnapshot().LastError,
        });
    }

    private async Task ServeBrowserCompanionGraph(HttpListenerResponse resp)
    {
        await WriteJson(resp, new
        {
            ok = _browserCompanion?.LastGraph != null,
            graph = _browserCompanion?.LastGraph,
        });
    }

    private async Task RunBrowserCompanionCommand(HttpListenerRequest req, HttpListenerResponse resp)
    {
        if (_browserCompanion == null)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = "Browser Companion bridge is unavailable." });
            return;
        }

        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var timeoutMs = doc.RootElement.TryGetProperty("timeoutMs", out var timeoutEl)
            ? Math.Clamp(timeoutEl.GetInt32(), 1000, 60000)
            : 15000;
        var command = new BrowserCompanionCommand
        {
            TaskId = GetJsonString(doc.RootElement, "taskId") ?? GetJsonString(doc.RootElement, "task_id"),
            Type = GetJsonString(doc.RootElement, "type"),
            Url = GetJsonString(doc.RootElement, "url"),
            ControlId = GetJsonString(doc.RootElement, "controlId") ?? GetJsonString(doc.RootElement, "control_id"),
            Text = GetJsonString(doc.RootElement, "text"),
            TimeoutMs = timeoutMs,
        };
        var result = await _browserCompanion.ExecuteAsync(
            command,
            TimeSpan.FromMilliseconds(timeoutMs),
            CancellationToken.None);
        if (!result.Ok)
            resp.StatusCode = 400;
        await WriteJson(resp, result);
    }

    private async Task RegisterBrowserCompanion(HttpListenerRequest req, HttpListenerResponse resp)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        _browserCompanion?.Register(
            GetJsonString(doc.RootElement, "sessionId") ?? GetJsonString(doc.RootElement, "session_id"),
            GetJsonString(doc.RootElement, "extensionVersion") ?? GetJsonString(doc.RootElement, "extension_version"),
            GetJsonString(doc.RootElement, "contentScriptVersion") ?? GetJsonString(doc.RootElement, "content_script_version"),
            GetJsonString(doc.RootElement, "browserName") ?? GetJsonString(doc.RootElement, "browser_name"),
            GetJsonString(doc.RootElement, "activeTabUrl") ?? GetJsonString(doc.RootElement, "active_tab_url"));
        await WriteJson(resp, new { ok = true });
    }

    private async Task ReceiveBrowserCompanionGraph(HttpListenerRequest req, HttpListenerResponse resp)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        BrowserCompanionControlGraph graph;
        if (string.IsNullOrWhiteSpace(body))
        {
            graph = null;
        }
        else
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("graph", out var graphElement)
                || doc.RootElement.TryGetProperty("Graph", out graphElement))
            {
                _browserCompanion?.Register(
                    GetJsonString(doc.RootElement, "sessionId") ?? GetJsonString(doc.RootElement, "session_id"),
                    GetJsonString(doc.RootElement, "extensionVersion") ?? GetJsonString(doc.RootElement, "extension_version"),
                    GetJsonString(doc.RootElement, "contentScriptVersion") ?? GetJsonString(doc.RootElement, "content_script_version"),
                    GetJsonString(doc.RootElement, "browserName") ?? GetJsonString(doc.RootElement, "browser_name"),
                    graphElement.ValueKind == JsonValueKind.Object
                        ? GetJsonString(graphElement, "url") ?? GetJsonString(graphElement, "Url")
                        : "");
                graph = graphElement.Deserialize<BrowserCompanionControlGraph>(CompanionJsonOptions);
            }
            else
            {
                graph = doc.RootElement.Deserialize<BrowserCompanionControlGraph>(CompanionJsonOptions);
            }
        }
        _browserCompanion?.SubmitGraph(graph);
        await WriteJson(resp, new { ok = true });
    }

    private async Task ReceiveBrowserCompanionResult(HttpListenerRequest req, HttpListenerResponse resp)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        var result = JsonSerializer.Deserialize<BrowserCompanionCommandResult>(
            string.IsNullOrWhiteSpace(body) ? "{}" : body,
            CompanionJsonOptions);
        _browserCompanion?.Complete(result);
        await WriteJson(resp, new { ok = true });
    }

    private async Task ServeBrowserCompanionCommand(HttpListenerRequest req, HttpListenerResponse resp)
    {
        var sessionId = req.QueryString["sessionId"] ?? req.QueryString["session_id"] ?? "";
        var command = _browserCompanion?.NextCommand(sessionId);
        await WriteJson(resp, new
        {
            ok = true,
            command,
        });
    }

    private async Task ServeSettings(HttpListenerResponse resp)
    {
        // TTS info is Windows-only (SAPI/Piper); give null on non-Windows to keep the API shape.
        var ttsBackend = OperatingSystem.IsWindows() ? TtsService.Instance.ActiveBackend.ToString() : "None";

        var settings = new
        {
            speechEngine = KeyboardWtfState.SelectedEngine.ToString(),
            language = KeyboardWtfState.SelectedLanguage,
            microphoneDevice = KeyboardWtfState.SelectedMicrophoneIndex,
            maxRecordingSeconds = KeyboardWtfState.MaxRecordingSeconds,
            whisperModelSize = KeyboardWtfState.SelectedWhisperModel.ToString(),
            voskLoaded = _app?.SpeechRecognition?.IsVoskLoaded ?? false,
            whisperLoaded = _app?.SpeechRecognition?.IsWhisperLoaded ?? false,
            voskPresent = ModelManager.IsVoskPresent,
            whisperPresent = ModelManager.IsWhisperPresent,
            modelsPath = ModelManager.ModelsBaseDir,
            claudeApiKey = MaskApiKey(KeyboardWtfState.ClaudeApiKey),
            claudeConfigured = KeyboardWtfState.IsClaudeAvailable,
            openAiApiKey = MaskApiKey(KeyboardWtfState.OpenAiApiKey),
            openAiConfigured = !string.IsNullOrEmpty(KeyboardWtfState.OpenAiApiKey),
            slackWebhookUrl = MaskWebhookUrl(KeyboardWtfState.SlackWebhookUrl),
            slackConfigured = !string.IsNullOrEmpty(KeyboardWtfState.SlackWebhookUrl),
            discordWebhookUrl = MaskWebhookUrl(KeyboardWtfState.DiscordWebhookUrl),
            discordConfigured = !string.IsNullOrEmpty(KeyboardWtfState.DiscordWebhookUrl),
            voiceNoteSavePath = KeyboardWtfState.VoiceNoteSavePath ?? "",
            voiceNoteEffectivePath = KeyboardWtfState.EffectiveVoiceNoteSavePath,
            voiceNoteFilenamePattern = KeyboardWtfState.VoiceNoteFilenamePattern ?? "",
            voiceNoteDefaultPattern = KeyboardWtfState.DefaultVoiceNoteFilenamePattern,
            voiceNoteFilenamePreview = PreviewFilename(KeyboardWtfState.VoiceNoteFilenamePattern ?? KeyboardWtfState.DefaultVoiceNoteFilenamePattern),

            // AI provider (new: multi-provider)
            aiProvider = KeyboardWtfState.SelectedAiProvider.ToString(),
            geminiApiKey = MaskApiKey(KeyboardWtfState.GeminiApiKey),
            geminiConfigured = !string.IsNullOrEmpty(KeyboardWtfState.GeminiApiKey),
            deepSeekApiKey = MaskApiKey(KeyboardWtfState.DeepSeekApiKey),
            deepSeekConfigured = !string.IsNullOrEmpty(KeyboardWtfState.DeepSeekApiKey),
            perplexityApiKey = MaskApiKey(KeyboardWtfState.PerplexityApiKey),
            perplexityConfigured = !string.IsNullOrEmpty(KeyboardWtfState.PerplexityApiKey),

            // Noise gate + transcript post-processing
            useNoiseGate = KeyboardWtfState.UseNoiseGate,
            translateTargetLanguage = KeyboardWtfState.TranslateTargetLanguage ?? "",
            useFillerWordCleaner = KeyboardWtfState.UseFillerWordCleaner,
            caseTransform = KeyboardWtfState.SelectedCaseTransform.ToString(),

            // AI toggle + custom prompts
            useAi = KeyboardWtfState.UseAi,
            customPromptEmail = KeyboardWtfState.CustomPrompts.TryGetValue("Email", out var cpEmail) ? cpEmail : "",
            customPromptSlack = KeyboardWtfState.CustomPrompts.TryGetValue("Slack", out var cpSlack) ? cpSlack : "",
            customPromptDiscord = KeyboardWtfState.CustomPrompts.TryGetValue("Discord", out var cpDiscord) ? cpDiscord : "",
            customPromptTeams = KeyboardWtfState.CustomPrompts.TryGetValue("Teams", out var cpTeams) ? cpTeams : "",
            customPromptCalendar = KeyboardWtfState.CustomPrompts.TryGetValue("Calendar", out var cpCal) ? cpCal : "",

            // Text-to-Speech
            ttsActiveBackend = ttsBackend,
            ttsPath = PiperInstaller.ActivePiperDir ?? PiperInstaller.TtsRoot,
            ttsBundledPath = PiperInstaller.BundledTtsRoot,
            piperBinaryPresent = PiperInstaller.IsBinaryPresent,
            piperEnVoicePresent = PiperInstaller.IsEnVoicePresent,
            piperDeVoicePresent = PiperInstaller.IsDeVoicePresent,
            piperBinarySource = PiperInstaller.PiperBinarySource,       // "Bundled" | "Downloaded" | "None"
            piperEnVoiceSource = PiperInstaller.EnVoiceSource,
            piperDeVoiceSource = PiperInstaller.DeVoiceSource,
            piperFullyInstalled = PiperInstaller.IsFullyInstalled,
            isInstallingPiper = KeyboardWtfState.IsInstallingPiper,
            piperInstallStatus = KeyboardWtfState.PiperInstallStatus,

            hotkeys = _settings?.Current.Hotkeys ?? HotkeySettings.Defaults(),
            activeHotkeys = _hotkeys?.ActiveRegistrations
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            defaultDestination = _settings?.Current.DefaultDestination ?? "Clipboard",
            textDeliveryMode = (_settings?.Current.TextDeliveryMode ?? VoiceTextDeliveryMode.TypeIntoActiveApp).ToString(),
            assistantName = KeyboardWtfState.AssistantName,
            assistantTone = KeyboardWtfState.AssistantTone.ToString(),
            geminiVoice = KeyboardWtfState.GeminiVoice,
            geminiVoices = SettingsService.GeminiVoices.Select(pair => new { name = pair.Key, style = pair.Value }).ToArray(),
            startWithWindows = _settings?.Current.StartWithWindows ?? true,
            startupExecutablePath = _app?.StartupExecutablePath ?? "",
            currentExecutablePath = Environment.ProcessPath ?? Application.ExecutablePath,
            currentProcessId = Environment.ProcessId,
            currentVersion = typeof(WebSettingsService).Assembly.GetName().Version?.ToString() ?? "",
            runtime = new
            {
                jarvisActive = _app?.IsJarvisActive ?? false,
                taskPanelVisible = _app?.IsTaskPanelVisible ?? false,
                taskPanelTabCount = _app?.VisibleTaskTabCount ?? 0,
                isRecording = KeyboardWtfState.IsRecording,
                isProcessing = KeyboardWtfState.IsProcessing,
                recordingMode = KeyboardWtfState.CurrentRecordingMode.ToString(),
                uiPhase = KeyboardWtfState.UiPhase.ToString(),
                uiTitle = KeyboardWtfState.UiTitle,
                uiDetail = KeyboardWtfState.UiDetail,
                inputLevelDb = Math.Round(KeyboardWtfState.InputLevelDb, 1),
            },
            startupRegistered = WindowsStartupService.IsRegisteredFor(_app?.StartupExecutablePath ?? ""),
            startupRegisteredCommand = WindowsStartupService.RegisteredCommand(),
            jarvisPermissionMode = (_settings?.Current.JarvisPermissionMode ?? JarvisPermissionMode.AlwaysAsk).ToString(),
            showTaskPopup = _settings?.Current.ShowTaskPopup ?? true,
            askForClarificationWhenLowConfidence =
                _settings?.Current.AskForClarificationWhenLowConfidence ?? true,
            taskClarificationMode =
                (_settings?.Current.TaskClarificationMode ?? TaskClarificationMode.SmartClarify).ToString(),
            browserAgentEnabled = _settings?.Current.BrowserAgentEnabled ?? true,
            smartBrowserBackend = (_settings?.Current.SmartBrowserBackend ?? SmartBrowserBackendMode.Auto).ToString(),
            browserCompanionEnabled = _settings?.Current.BrowserCompanionEnabled ?? true,
            browserUseEnabled = _settings?.Current.BrowserUseEnabled ?? true,
            allowAutomationProfileFallback = _settings?.Current.AllowAutomationProfileFallback ?? false,
            browserOsEnabled = _settings?.Current.BrowserOSEnabled ?? false,
            learnBrowserWorkflows = _settings?.Current.LearnBrowserWorkflows ?? true,
            maxBrowserTaskSeconds = _settings?.Current.MaxBrowserTaskSeconds ?? 180,
            browserAgentDeveloperDiagnostics = _settings?.Current.BrowserAgentDeveloperDiagnostics ?? false,
            browserAgentStatus = _browserAgent?.Status ?? new BrowserBackendStatus(),
            browserCompanionStatus = BrowserCompanionStatusSnapshot(),
            jarvisTasks = _taskManager?.Snapshot() ?? Array.Empty<JarvisTask>(),
            intentMemory = _intentMemory?.Snapshot() ?? new IntentMemorySnapshot(),
            jarvisWorkflows = _settings?.Current.JarvisWorkflows ?? new List<JarvisWorkflowSettings>(),
            jarvisActionHistory = _jarvisHistory?.Snapshot() ?? Array.Empty<JarvisActionEntry>(),
            pendingSensitiveAction = _app?.Commands?.PendingSensitiveAction,
            preferredBrowser = _settings?.Current.PreferredBrowser ?? "",
            learnedMappings = _learnedMappings?.Snapshot() ?? Array.Empty<LearnedMappingEntry>(),
            featureStatus = new
            {
                smartAppSearch = "Available",
                smartFileSearch = "Available",
                browserTargeting = "Available",
                screenGuidance = string.IsNullOrWhiteSpace(KeyboardWtfState.GeminiApiKey)
                    ? "Needs Gemini API key"
                    : (_settings?.Current.JarvisPermissionMode == JarvisPermissionMode.AutoExecute
                        ? "Available in auto-execute"
                        : "Available with confirmation"),
                cameraCapture = "One-shot photo capture available",
                virtualDesktops = "Available",
                windowsRecording = "Game Bar + region shortcuts available",
                liveDictation = _app?.SpeechRecognition?.IsVoskLoaded == true
                    ? "Live partial transcript available"
                    : "Final transcript available; Vosk enables live preview",
                routineMemory = "Saved trigger phrases available",
                browserAgent = _settings?.Current.BrowserAgentEnabled == true
                    ? (_browserAgent?.Status.Initialized == true
                        ? $"Experimental - {_browserAgent.Status.BrowserChannel}"
                        : "Experimental - starts on first task")
                    : "Disabled",
            }
        };

        var json = JsonSerializer.Serialize(settings);
        var buffer = Encoding.UTF8.GetBytes(json);
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private async Task SaveSettings(HttpListenerRequest req, HttpListenerResponse resp)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = await reader.ReadToEndAsync();

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("speechEngine", out var engineEl))
        {
            if (Enum.TryParse<SpeechEngine>(engineEl.GetString(), out var engine))
                _settings?.SaveSpeechEngine(engine);
        }

        if (root.TryGetProperty("language", out var langEl))
        {
            var lang = langEl.GetString();
            if (!string.IsNullOrEmpty(lang))
                _settings?.SaveLanguage(lang);
        }

        if (root.TryGetProperty("microphoneDevice", out var micEl))
        {
            var micIndex = micEl.GetInt32();
            _settings?.SaveMicrophoneDevice(micIndex);
        }

        if (root.TryGetProperty("maxRecordingSeconds", out var maxRecEl))
        {
            var maxRec = maxRecEl.GetInt32();
            if (maxRec is >= 5 and <= 300)
                _settings?.SaveMaxRecordingSeconds(maxRec);
        }

        if (root.TryGetProperty("whisperModelSize", out var modelSizeEl))
        {
            if (Enum.TryParse<WhisperModelSize>(modelSizeEl.GetString(), out var modelSize))
                _settings?.SaveWhisperModelSize(modelSize);
        }

        if (root.TryGetProperty("aiProvider", out var providerEl))
        {
            if (Enum.TryParse<AiProvider>(providerEl.GetString(), out var provider))
                _settings?.SaveAiProvider(provider);
        }

        if (root.TryGetProperty("claudeApiKey", out var claudeEl))
        {
            var key = claudeEl.GetString();
            if (!string.IsNullOrEmpty(key) && !key.StartsWith("••••"))
                _settings?.SaveClaudeApiKey(key);
        }

        if (root.TryGetProperty("openAiApiKey", out var openAiEl))
        {
            var key = openAiEl.GetString();
            if (!string.IsNullOrEmpty(key) && !key.StartsWith("••••"))
                _settings?.SaveOpenAiApiKey(key);
        }

        if (root.TryGetProperty("geminiApiKey", out var geminiEl))
        {
            var key = geminiEl.GetString();
            if (!string.IsNullOrEmpty(key) && !key.StartsWith("••••"))
                _settings?.SaveGeminiApiKey(key);
        }

        if (root.TryGetProperty("deepSeekApiKey", out var dsEl))
        {
            var key = dsEl.GetString();
            if (!string.IsNullOrEmpty(key) && !key.StartsWith("••••"))
                _settings?.SaveDeepSeekApiKey(key);
        }

        if (root.TryGetProperty("perplexityApiKey", out var pxEl))
        {
            var key = pxEl.GetString();
            if (!string.IsNullOrEmpty(key) && !key.StartsWith("••••"))
                _settings?.SavePerplexityApiKey(key);
        }

        if (root.TryGetProperty("slackWebhookUrl", out var slackEl))
        {
            var url = slackEl.GetString();
            if (url != null && !url.StartsWith("••••"))
                _settings?.SaveSlackWebhookUrl(url);
        }

        if (root.TryGetProperty("discordWebhookUrl", out var discordEl))
        {
            var url = discordEl.GetString();
            if (url != null && !url.StartsWith("••••"))
                _settings?.SaveDiscordWebhookUrl(url);
        }

        if (root.TryGetProperty("voiceNoteSavePath", out var vnPathEl))
        {
            _settings?.SaveVoiceNoteSavePath(vnPathEl.GetString() ?? "");
        }

        if (root.TryGetProperty("voiceNoteFilenamePattern", out var vnPatternEl))
        {
            _settings?.SaveVoiceNoteFilenamePattern(vnPatternEl.GetString() ?? "");
        }

        if (root.TryGetProperty("useNoiseGate", out var useNgEl))
            _settings?.SaveUseNoiseGate(useNgEl.GetBoolean());
        if (root.TryGetProperty("translateTargetLanguage", out var translateEl))
            _settings?.SaveTranslateTargetLanguage(translateEl.GetString() ?? "");
        if (root.TryGetProperty("useFillerWordCleaner", out var fillerEl))
            _settings?.SaveUseFillerWordCleaner(fillerEl.GetBoolean());
        if (root.TryGetProperty("caseTransform", out var caseEl)
            && Enum.TryParse<Models.CaseTransform>(caseEl.GetString(), out var caseVal))
            _settings?.SaveCaseTransform(caseVal);

        if (root.TryGetProperty("useAi", out var useAiEl))
        {
            _settings?.SaveUseAi(useAiEl.GetBoolean());
        }

        if (root.TryGetProperty("assistantName", out var assistantNameEl))
            _settings?.SaveAssistantName(assistantNameEl.GetString() ?? "Jarvis");

        if (root.TryGetProperty("assistantTone", out var assistantToneEl)
            && Enum.TryParse<AssistantTone>(assistantToneEl.GetString(), out var assistantTone))
            _settings?.SaveAssistantTone(assistantTone);
        if (root.TryGetProperty("geminiVoice", out var geminiVoiceEl))
            _settings?.SaveGeminiVoice(geminiVoiceEl.GetString() ?? "Kore");
        if (root.TryGetProperty("startWithWindows", out var startupEl))
        {
            _settings?.SaveStartWithWindows(startupEl.GetBoolean());
            _app?.SyncStartupRegistration();
        }
        if (root.TryGetProperty("jarvisPermissionMode", out var permissionEl)
            && Enum.TryParse<JarvisPermissionMode>(permissionEl.GetString(), out var permissionMode))
            _settings?.SaveJarvisPermissionMode(permissionMode);
        if (root.TryGetProperty("showTaskPopup", out var showTaskPopupEl))
            _settings?.SaveShowTaskPopup(showTaskPopupEl.GetBoolean());
        if (root.TryGetProperty("askForClarificationWhenLowConfidence", out var askClarificationEl))
            _settings?.SaveAskForClarificationWhenLowConfidence(askClarificationEl.GetBoolean());
        if (root.TryGetProperty("taskClarificationMode", out var clarificationModeEl)
            && Enum.TryParse<TaskClarificationMode>(clarificationModeEl.GetString(), out var clarificationMode))
            _settings?.SaveTaskClarificationMode(clarificationMode);
        if (root.TryGetProperty("browserAgentEnabled", out var browserAgentEnabledEl))
            _settings?.SaveBrowserAgentEnabled(browserAgentEnabledEl.GetBoolean());
        if (root.TryGetProperty("smartBrowserBackend", out var smartBrowserBackendEl)
            && Enum.TryParse<SmartBrowserBackendMode>(smartBrowserBackendEl.GetString(), out var smartBrowserBackend))
            _settings?.SaveSmartBrowserBackend(smartBrowserBackend);
        if (root.TryGetProperty("browserCompanionEnabled", out var browserCompanionEnabledEl))
            _settings?.SaveBrowserCompanionEnabled(browserCompanionEnabledEl.GetBoolean());
        if (root.TryGetProperty("browserUseEnabled", out var browserUseEnabledEl))
            _settings?.SaveBrowserUseEnabled(browserUseEnabledEl.GetBoolean());
        if (root.TryGetProperty("allowAutomationProfileFallback", out var automationFallbackEl))
            _settings?.SaveAllowAutomationProfileFallback(automationFallbackEl.GetBoolean());
        if (root.TryGetProperty("browserOsEnabled", out var browserOsEnabledEl))
            _settings?.SaveBrowserOSEnabled(browserOsEnabledEl.GetBoolean());
        if (root.TryGetProperty("learnBrowserWorkflows", out var learnBrowserWorkflowsEl))
            _settings?.SaveLearnBrowserWorkflows(learnBrowserWorkflowsEl.GetBoolean());
        if (root.TryGetProperty("maxBrowserTaskSeconds", out var maxBrowserTaskSecondsEl))
            _settings?.SaveMaxBrowserTaskSeconds(maxBrowserTaskSecondsEl.GetInt32());
        if (root.TryGetProperty("browserAgentDeveloperDiagnostics", out var browserAgentDiagnosticsEl))
            _settings?.SaveBrowserAgentDeveloperDiagnostics(browserAgentDiagnosticsEl.GetBoolean());
        if (root.TryGetProperty("preferredBrowser", out var preferredBrowserEl))
            _settings?.SavePreferredBrowser(preferredBrowserEl.GetString() ?? "");

        if (root.TryGetProperty("jarvisWorkflow", out var workflowEl))
        {
            _settings?.SaveWorkflow(
                GetJsonString(workflowEl, "name"),
                GetJsonString(workflowEl, "apps"),
                GetJsonString(workflowEl, "urls"),
                GetJsonString(workflowEl, "folder"));
        }

        foreach (var destName in new[] { "Email", "Slack", "Discord", "Teams", "Calendar" })
        {
            var propKey = "customPrompt" + destName;
            if (root.TryGetProperty(propKey, out var promptEl))
                _settings?.SaveCustomPrompt(destName, promptEl.GetString() ?? "");
        }

        if (root.TryGetProperty("defaultDestination", out var defaultDestEl))
            _settings?.SaveDefaultDestination(defaultDestEl.GetString() ?? "Clipboard");

        if (root.TryGetProperty("textDeliveryMode", out var deliveryEl)
            && Enum.TryParse<VoiceTextDeliveryMode>(deliveryEl.GetString(), out var deliveryMode))
            _settings?.SaveTextDeliveryMode(deliveryMode);

        if (root.TryGetProperty("hotkeys", out var hotkeysEl))
        {
            var hotkeys = new HotkeySettings
            {
                PushToTalk = GetHotkey(hotkeysEl, "pushToTalk", _settings?.Current.Hotkeys.PushToTalk ?? "Ctrl+Alt+K"),
                Dictation = GetHotkey(hotkeysEl, "dictation", _settings?.Current.Hotkeys.Dictation ?? "Ctrl+Alt+D"),
                Jarvis = GetHotkey(hotkeysEl, "jarvis",
                    GetHotkey(hotkeysEl, "quickSend", _settings?.Current.Hotkeys.Jarvis ?? HotkeyService.JarvisDefault)),
                CommandMode = "",
                QuickSend = "",
                Cancel = GetHotkey(hotkeysEl, "cancel", _settings?.Current.Hotkeys.Cancel ?? "Ctrl+Alt+X"),
                OpenSettings = GetHotkey(hotkeysEl, "openSettings", _settings?.Current.Hotkeys.OpenSettings ?? "Ctrl+Alt+,"),
            };

            if (HotkeyService.HasDuplicates(hotkeys, out var duplicate))
            {
                resp.StatusCode = 400;
                var error = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = false, error = $"Duplicate hotkey: {duplicate}" }));
                resp.ContentType = "application/json";
                resp.ContentLength64 = error.Length;
                await resp.OutputStream.WriteAsync(error);
                resp.Close();
                return;
            }

            _settings?.SaveHotkeys(hotkeys);
            _app?.RegisterHotkeys();
        }

        var buffer = Encoding.UTF8.GetBytes("{\"ok\":true}");
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private static async Task WriteJson(HttpListenerResponse resp, object payload)
    {
        var buffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private static async Task ServeBrowserAgentMockForm(HttpListenerResponse resp)
    {
        const string html =
            """
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8">
                <title>keyboard.wtf Browser Agent Mock Form</title>
                <style>
                  body { font: 16px Segoe UI, sans-serif; max-width: 680px; margin: 60px auto; padding: 24px; }
                  label { display: block; margin: 18px 0 6px; }
                  input, textarea { width: 100%; box-sizing: border-box; padding: 10px; }
                  button { margin: 18px 8px 0 0; padding: 10px 16px; }
                  #result { margin-top: 24px; padding: 14px; background: #f3f4f6; }
                </style>
              </head>
              <body>
                <h1>Safe local project preview</h1>
                <p>This page never submits or sends data. It exists only for local Browser Agent verification.</p>
                <label for="project">Project name</label>
                <input id="project" autocomplete="off">
                <label for="summary">Project summary</label>
                <textarea id="summary"></textarea>
                <button type="button" onclick="document.getElementById('result').textContent='Preview: '+document.getElementById('project').value">Preview</button>
                <button type="button">Choose project</button>
                <button type="button">Choose project</button>
                <div id="result">No preview yet.</div>
              </body>
            </html>
            """;
        var buffer = Encoding.UTF8.GetBytes(html);
        resp.ContentType = "text/html; charset=utf-8";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private static async Task ServeBrowserAgentMockStore(HttpListenerResponse resp)
    {
        const string html =
            """
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="utf-8">
                <title>keyboard.wtf Browser Agent Mock Store</title>
                <style>
                  body { font: 16px Segoe UI, sans-serif; max-width: 760px; margin: 54px auto; padding: 24px; color:#111827; }
                  .product { border:1px solid #e5e7eb; border-radius:18px; padding:18px; margin:14px 0; }
                  .name { font-weight:700; font-size:20px; }
                  .meta { color:#4b5563; margin-top:8px; }
                  button { margin-top:12px; padding:10px 14px; border-radius:10px; border:1px solid #d1d5db; background:white; }
                </style>
              </head>
              <body>
                <h1>Safe local store comparison</h1>
                <p>This mock page is local only. Cart and checkout buttons are intentionally inert.</p>
                <article class="product">
                  <div class="name">Alpha Keyboard</div>
                  <div class="meta">Price: $129 · Rating: 4.8 stars · Prime eligible</div>
                  <button type="button">Add to cart</button>
                </article>
                <article class="product">
                  <div class="name">Budget Keyboard</div>
                  <div class="meta">Price: $79 · Rating: 4.1 stars · Prime eligible</div>
                  <button type="button">Add to cart</button>
                </article>
                <article class="product">
                  <div class="name">Pro Creator Keyboard</div>
                  <div class="meta">Price: $149 · Rating: 4.9 stars · Prime eligible</div>
                  <button type="button">Add to cart</button>
                </article>
              </body>
            </html>
            """;
        var buffer = Encoding.UTF8.GetBytes(html);
        resp.ContentType = "text/html; charset=utf-8";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private async Task RunBrowserAgentDemo(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            if (_browserAgent == null)
                throw new InvalidOperationException("Browser Agent is unavailable.");
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var mode = GetJsonString(doc.RootElement, "mode");
            var url = $"http://localhost:{Port}/browser-agent/mock-form";
            var isClarification = mode.Equals("clarification", StringComparison.OrdinalIgnoreCase);
            var isConfirmation = mode.Equals("confirmation", StringComparison.OrdinalIgnoreCase);
            var isManualSafety = mode.Equals("manual-safety", StringComparison.OrdinalIgnoreCase);
            var isContext = mode.Equals("context", StringComparison.OrdinalIgnoreCase);
            var isRunning = mode.Equals("running", StringComparison.OrdinalIgnoreCase);
            var started = await _browserAgent.StartAsync(new BrowserTaskRequest
            {
                Title = isClarification
                    ? "Clarification state demo"
                    : isConfirmation
                        ? "Confirmation safety demo"
                        : isManualSafety
                            ? "Manual safety stop demo"
                            : isContext
                                ? "Selected context demo"
                                : isRunning
                                    ? "Running task demo"
                                    : "Safe mock form demo",
                OriginalUserCommand = isClarification
                    ? "Open the local mock form and demonstrate an ambiguous project choice."
                    : isConfirmation
                        ? "Open the local mock form and demonstrate that a submit action pauses for confirmation."
                        : isManualSafety
                            ? "Open the local mock form and demonstrate that checkout is never automated."
                            : isContext
                                ? "Use this selected text as context, then fill the local preview without submitting."
                                : isRunning
                                    ? "Open the local mock form and keep the task running for overlay verification."
                                    : "Fill the local mock form with keyboard.wtf and preview it without submitting.",
                StartUrl = url,
                ExpectedOutcome = "Preview: keyboard.wtf",
                UseSelectedText = isContext,
                ContextExplicitlyRequested = isContext,
            }, CancellationToken.None);
            if (!started.Ok)
            {
                resp.StatusCode = 400;
                await WriteJson(resp, started);
                return;
            }
            if (isRunning)
            {
                await WriteJson(resp, started);
                return;
            }

            BrowserAgentResult result;
            if (isClarification)
            {
                result = await _browserAgent.ActAsync(new BrowserAgentActionRequest
                {
                    TaskId = started.TaskId,
                    Action = BrowserAgentActionKind.Click,
                    Role = "button",
                    Target = "Choose project",
                }, CancellationToken.None);
            }
            else if (isConfirmation || isManualSafety)
            {
                result = await _browserAgent.ActAsync(new BrowserAgentActionRequest
                {
                    TaskId = started.TaskId,
                    Action = BrowserAgentActionKind.Click,
                    Role = "button",
                    Target = isConfirmation ? "Submit application" : "Checkout",
                }, CancellationToken.None);
            }
            else
            {
                result = await _browserAgent.ActAsync(new BrowserAgentActionRequest
                {
                    TaskId = started.TaskId,
                    Action = BrowserAgentActionKind.Fill,
                    Role = "label",
                    Target = "Project name",
                    Value = "keyboard.wtf",
                }, CancellationToken.None);
                if (result.Ok)
                {
                    result = await _browserAgent.ActAsync(new BrowserAgentActionRequest
                    {
                        TaskId = started.TaskId,
                        Action = BrowserAgentActionKind.Click,
                        Role = "button",
                        Target = "Preview",
                    }, CancellationToken.None);
                }
                if (result.Ok)
                {
                    result = await _browserAgent.FinishAsync(
                        started.TaskId,
                        "Preview: keyboard.wtf",
                        "Filled and verified the safe local preview without submitting.",
                        CancellationToken.None);
                }
            }
            await WriteJson(resp, result);
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private async Task StartBrowserAgentTask(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            if (_browserAgent == null)
                throw new InvalidOperationException("Browser Agent is unavailable.");
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var result = await _browserAgent.StartAsync(new BrowserTaskRequest
            {
                Title = GetJsonString(doc.RootElement, "title"),
                OriginalUserCommand = GetJsonString(doc.RootElement, "task"),
                StartUrl = GetJsonString(doc.RootElement, "startUrl")
                    ?? GetJsonString(doc.RootElement, "start_url"),
                ExpectedOutcome = GetJsonString(doc.RootElement, "expectedOutcome")
                    ?? GetJsonString(doc.RootElement, "expected_outcome"),
                UseSelectedText = GetJsonBool(doc.RootElement, "useSelectedText")
                    || GetJsonBool(doc.RootElement, "use_selected_text"),
                UseClipboard = GetJsonBool(doc.RootElement, "useClipboard")
                    || GetJsonBool(doc.RootElement, "use_clipboard"),
                ContextExplicitlyRequested = GetJsonBool(doc.RootElement, "contextExplicitlyRequested")
                    || GetJsonBool(doc.RootElement, "context_explicitly_requested"),
                AutomationProfileFallbackAllowed = GetJsonBool(doc.RootElement, "automationProfileFallbackAllowed")
                    || GetJsonBool(doc.RootElement, "automation_profile_fallback_allowed"),
            }, CancellationToken.None);
            if (!result.Ok)
                resp.StatusCode = 400;
            await WriteJson(resp, result);
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private async Task ObserveBrowserAgentTask(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            if (_browserAgent == null)
                throw new InvalidOperationException("Browser Agent is unavailable.");
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var result = await _browserAgent.ObserveAsync(GetJsonString(doc.RootElement, "taskId")
                ?? GetJsonString(doc.RootElement, "task_id"), CancellationToken.None);
            if (!result.Ok)
                resp.StatusCode = 400;
            await WriteJson(resp, result);
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private async Task ActBrowserAgentTask(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            if (_browserAgent == null)
                throw new InvalidOperationException("Browser Agent is unavailable.");
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var actionText = GetJsonString(doc.RootElement, "action");
            if (!Enum.TryParse<BrowserAgentActionKind>(
                    actionText?.Replace("_", "", StringComparison.Ordinal)
                        .Replace("-", "", StringComparison.Ordinal)
                        .Replace(" ", "", StringComparison.Ordinal),
                    true,
                    out var action))
            {
                throw new InvalidOperationException("Unsupported Browser Agent action.");
            }
            var result = await _browserAgent.ActAsync(new BrowserAgentActionRequest
            {
                TaskId = GetJsonString(doc.RootElement, "taskId") ?? GetJsonString(doc.RootElement, "task_id"),
                Action = action,
                Role = GetJsonString(doc.RootElement, "role"),
                Target = GetJsonString(doc.RootElement, "target"),
                Value = GetJsonString(doc.RootElement, "value"),
                FilePath = GetJsonString(doc.RootElement, "filePath") ?? GetJsonString(doc.RootElement, "file_path"),
                ExpectedText = GetJsonString(doc.RootElement, "expectedText") ?? GetJsonString(doc.RootElement, "expected_text"),
                ExplicitlyRequested = GetJsonBool(doc.RootElement, "explicitlyRequested")
                    || GetJsonBool(doc.RootElement, "explicitly_requested"),
            }, CancellationToken.None);
            if (!result.Ok)
                resp.StatusCode = result.ConfirmationRequired || result.ClarificationRequired ? 409 : 400;
            await WriteJson(resp, result);
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private async Task FinishBrowserAgentTask(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            if (_browserAgent == null)
                throw new InvalidOperationException("Browser Agent is unavailable.");
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var result = await _browserAgent.FinishAsync(
                GetJsonString(doc.RootElement, "taskId") ?? GetJsonString(doc.RootElement, "task_id"),
                GetJsonString(doc.RootElement, "expectedText") ?? GetJsonString(doc.RootElement, "expected_text"),
                GetJsonString(doc.RootElement, "outcome"),
                CancellationToken.None);
            if (!result.Ok)
                resp.StatusCode = 400;
            await WriteJson(resp, result);
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private async Task CancelBrowserAgentTask(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var result = await _browserAgent.CancelAsync(GetJsonString(doc.RootElement, "taskId"));
            await WriteJson(resp, result);
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private async Task ConfirmBrowserAgentTask(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var taskId = GetJsonString(doc.RootElement, "taskId")
                ?? GetJsonString(doc.RootElement, "task_id");
            var confirmed = !doc.RootElement.TryGetProperty("confirmed", out var confirmedEl)
                || confirmedEl.GetBoolean();
            _taskManager?.RespondToConfirmation(taskId, confirmed);
            await WriteJson(resp, new { ok = true });
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private static string GetHotkey(JsonElement element, string property, string fallback)
    {
        if (!element.TryGetProperty(property, out var value))
            return fallback;

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
    }

    private static bool GetJsonBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }

    private async Task DeleteIntentMemory(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.TryGetProperty("id", out var idEl)
                ? idEl.GetString()
                : doc.RootElement.TryGetProperty("key", out var keyEl)
                    ? keyEl.GetString()
                    : "";
            var removed = _intentMemory?.Forget(id) ?? false;
            await WriteJson(resp, new { ok = true, removed });
        }
        catch (Exception ex)
        {
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private async Task RememberIntentMemory(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var key = doc.RootElement.TryGetProperty("key", out var keyEl) ? keyEl.GetString() : "";
            var value = doc.RootElement.TryGetProperty("value", out var valueEl)
                ? valueEl.GetString()
                : doc.RootElement.TryGetProperty("text", out var textEl)
                    ? textEl.GetString()
                    : "";
            var typeName = GetJsonString(doc.RootElement, "type");
            var type = IntentMemoryType.Preference;
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                var normalized = typeName.Replace("_", "", StringComparison.Ordinal)
                    .Replace("-", "", StringComparison.Ordinal)
                    .Replace(" ", "", StringComparison.Ordinal);
                if (!Enum.TryParse(normalized, true, out type))
                    throw new InvalidOperationException("Unsupported memory type.");
            }
            var entry = _intentMemory?.RememberStructured(new IntentMemoryDraft
            {
                Type = type,
                SourcePhrase = value,
                TriggerPhrase = key,
                Summary = value,
                Action = GetJsonString(doc.RootElement, "action"),
                Target = GetJsonString(doc.RootElement, "target"),
            });
            await WriteJson(resp, new { ok = entry != null, entry });
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private async Task UpdateIntentMemory(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var id = GetJsonString(root, "id");
            IntentMemoryType? type = null;
            var typeName = GetJsonString(root, "type");
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                var normalized = typeName.Replace("_", "", StringComparison.Ordinal)
                    .Replace("-", "", StringComparison.Ordinal)
                    .Replace(" ", "", StringComparison.Ordinal);
                if (!Enum.TryParse<IntentMemoryType>(normalized, true, out var parsed))
                    throw new InvalidOperationException("Unsupported memory type.");
                type = parsed;
            }
            var entry = _intentMemory?.Update(id, new IntentMemoryUpdate
            {
                Type = type,
                TriggerPhrase = root.TryGetProperty("triggerPhrase", out var triggerEl)
                    ? triggerEl.GetString() ?? ""
                    : null,
                Summary = root.TryGetProperty("summary", out var summaryEl)
                    ? summaryEl.GetString() ?? ""
                    : null,
                Action = root.TryGetProperty("action", out var actionEl)
                    ? actionEl.GetString() ?? ""
                    : null,
                Target = root.TryGetProperty("target", out var targetEl)
                    ? targetEl.GetString() ?? ""
                    : null,
            });
            await WriteJson(resp, new
            {
                ok = entry != null,
                entry,
                error = entry == null ? "Memory not found." : null,
            });
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private async Task DeleteJarvisWorkflow(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var name = GetJsonString(doc.RootElement, "name");
            var removed = _settings?.DeleteWorkflow(name) ?? false;
            await WriteJson(resp, new
            {
                ok = removed,
                removed,
                error = removed ? null : "Workflow not found.",
            });
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private async Task DeleteLearnedMapping(HttpListenerRequest req, HttpListenerResponse resp)
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var id = GetJsonString(doc.RootElement, "id");
            var alias = GetJsonString(doc.RootElement, "alias");
            var removed = _learnedMappings?.Forget(
                string.IsNullOrWhiteSpace(id) ? alias : id) ?? false;
            await WriteJson(resp, new { ok = removed, removed });
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await WriteJson(resp, new { ok = false, error = ex.Message });
        }
    }

    private static string GetJsonString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.GetString() ?? ""
            : "";

    private async Task ServeMicrophones(HttpListenerResponse resp)
    {
        var devices = new List<object>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            devices.Add(new { index = i, name = caps.ProductName, channels = caps.Channels });
        }

        var json = JsonSerializer.Serialize(new
        {
            devices,
            selected = KeyboardWtfState.SelectedMicrophoneIndex
        });
        var buffer = Encoding.UTF8.GetBytes(json);
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private async Task RunMicTest(HttpListenerResponse resp)
    {
        string resultJson;
        try
        {
            var format = new WaveFormat(16000, 1);
            var deviceIndex = KeyboardWtfState.SelectedMicrophoneIndex;
            var waveIn = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = format,
                BufferMilliseconds = 50
            };

            var pcm = new MemoryStream();
            waveIn.DataAvailable += (s, e) => pcm.Write(e.Buffer, 0, e.BytesRecorded);

            waveIn.StartRecording();
            await Task.Delay(2000);
            waveIn.StopRecording();
            waveIn.Dispose();

            var pcmData = pcm.ToArray();
            pcm.Dispose();

            // Calculate peak amplitude from raw PCM (16-bit samples)
            var peak = 0;
            for (var i = 0; i < pcmData.Length - 1; i += 2)
            {
                var sample = Math.Abs((short)(pcmData[i] | (pcmData[i + 1] << 8)));
                if (sample > peak) peak = sample;
            }

            var peakDb = peak > 0 ? 20.0 * Math.Log10(peak / 32768.0) : -96.0;
            var byteCount = pcmData.Length;

            resultJson = JsonSerializer.Serialize(new
            {
                ok = true,
                durationMs = 2000,
                peakDb = Math.Round(peakDb, 1),
                bytesRecorded = byteCount,
                detected = peak > 500
            });
        }
        catch (Exception ex)
        {
            resultJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        var buffer = Encoding.UTF8.GetBytes(resultJson);
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private async Task InstallPiper(HttpListenerResponse resp)
    {
        string resultJson;
        try
        {
            // Kick off install on a background task; don't block the HTTP response waiting for
            // ~150 MB of downloads. UI polls /api/settings for status.
            if (!KeyboardWtfState.IsInstallingPiper)
            {
                await PiperInstaller.EnsureInstalledAsync(status =>
                {
                    KeyboardWtfState.PiperInstallStatus = status;
                    AppLog.Info($"Piper install: {status}");
                });
            }
            resultJson = JsonSerializer.Serialize(new { ok = true, installing = true });
        }
        catch (Exception ex)
        {
            resultJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        var buffer = Encoding.UTF8.GetBytes(resultJson);
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private async Task TestTts(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string resultJson;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                resultJson = JsonSerializer.Serialize(new { ok = false, error = "TTS is Windows-only" });
            }
            else
            {
                // Optional {"lang": "en" | "de"} body to force a specific voice. Omitted = use current language setting.
                string langOverride = null;
                try
                {
                    using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                    var body = await reader.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("lang", out var langEl))
                            langOverride = langEl.GetString();
                    }
                }
                catch { /* empty body is fine */ }

                var effective = string.IsNullOrEmpty(langOverride) ? (KeyboardWtfState.SelectedLanguage ?? "auto") : langOverride;
                var sample = effective == "de"
                    ? "Hallo, dies ist ein Test von keyboard.wtf."
                    : "Hello, this is a keyboard.wtf voice test.";
                TtsService.Instance.Speak(sample, langOverride);
                resultJson = JsonSerializer.Serialize(new
                {
                    ok = true,
                    backend = TtsService.Instance.ActiveBackend.ToString(),
                    language = effective
                });
            }
        }
        catch (Exception ex)
        {
            resultJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        var buffer = Encoding.UTF8.GetBytes(resultJson);
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private async Task DownloadModels(HttpListenerResponse resp)
    {
        string resultJson;
        try
        {
            await ModelManager.EnsureModelsAsync(status => AppLog.Info($"Model download: {status}"));

            // Reload models into the speech service
            if (ModelManager.IsVoskPresent && !(_app?.SpeechRecognition?.IsVoskLoaded ?? false))
                _app?.SpeechRecognition?.LoadVoskModel(ModelManager.VoskModelPath);

            if (ModelManager.IsWhisperPresent && !(_app?.SpeechRecognition?.IsWhisperLoaded ?? false))
                _app?.SpeechRecognition?.LoadWhisperModel(ModelManager.WhisperModelPath);

            resultJson = JsonSerializer.Serialize(new
            {
                ok = true,
                voskPresent = ModelManager.IsVoskPresent,
                whisperPresent = ModelManager.IsWhisperPresent,
                voskLoaded = _app?.SpeechRecognition?.IsVoskLoaded ?? false,
                whisperLoaded = _app?.SpeechRecognition?.IsWhisperLoaded ?? false
            });
        }
        catch (Exception ex)
        {
            resultJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        var buffer = Encoding.UTF8.GetBytes(resultJson);
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private async Task TestClaude(HttpListenerResponse resp)
    {
        string resultJson;
        try
        {
            if (!KeyboardWtfState.IsClaudeAvailable)
            {
                resultJson = JsonSerializer.Serialize(new { ok = false, error = "Claude API key not configured" });
            }
            else
            {
                var provider = Services.Ai.AiProviderRegistry.Get(AiProvider.Claude);
                var (success, message) = await provider.TestApiKeyAsync();
                resultJson = JsonSerializer.Serialize(new { ok = success, message });
            }
        }
        catch (Exception ex)
        {
            resultJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        var buffer = Encoding.UTF8.GetBytes(resultJson);
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    /// <summary>
    /// Generic test endpoint: accepts {"provider": "Claude" | "OpenAi" | ...} and runs that
    /// provider's smoke test. Returns {ok, message} matching what the Claude-specific endpoint does.
    /// </summary>
    private async Task TestAiProvider(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string resultJson;
        try
        {
            string providerName = null;
            try
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("provider", out var pEl))
                        providerName = pEl.GetString();
                }
            }
            catch { /* body is optional */ }

            if (string.IsNullOrEmpty(providerName) || !Enum.TryParse<AiProvider>(providerName, out var providerId))
            {
                resultJson = JsonSerializer.Serialize(new { ok = false, error = "Missing or invalid 'provider'" });
            }
            else
            {
                var provider = Services.Ai.AiProviderRegistry.Get(providerId);
                if (!provider.IsImplemented)
                {
                    resultJson = JsonSerializer.Serialize(new { ok = false, error = $"{provider.DisplayName}: coming soon" });
                }
                else if (!provider.IsAvailable)
                {
                    resultJson = JsonSerializer.Serialize(new { ok = false, error = $"{provider.DisplayName}: API key not configured" });
                }
                else
                {
                    var (success, message) = await provider.TestApiKeyAsync();
                    resultJson = JsonSerializer.Serialize(new { ok = success, message });
                }
            }
        }
        catch (Exception ex)
        {
            resultJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        var buffer = Encoding.UTF8.GetBytes(resultJson);
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private async Task StartLiveMicTest(HttpListenerResponse resp)
    {
        string resultJson;
        try
        {
            StopLiveMicInternal(); // idempotent: stop any existing session first

            var deviceIndex = KeyboardWtfState.SelectedMicrophoneIndex >= 0 ? KeyboardWtfState.SelectedMicrophoneIndex : 0;
            lock (_liveMicLock)
            {
                _liveMicPeak = 0;
                _liveMic = new WaveInEvent
                {
                    DeviceNumber = deviceIndex,
                    WaveFormat = new WaveFormat(16000, 1),
                    BufferMilliseconds = 30
                };
                _liveMic.DataAvailable += OnLiveMicData;
                _liveMic.StartRecording();
            }

            resultJson = JsonSerializer.Serialize(new { ok = true });
        }
        catch (Exception ex)
        {
            StopLiveMicInternal();
            resultJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            AppLog.Error($"Mic test start failed: {ex.Message}");
        }

        await WriteJson(resp, resultJson);
    }

    private void OnLiveMicData(object sender, WaveInEventArgs e)
    {
        var peak = 0;
        for (var i = 0; i < e.BytesRecorded - 1; i += 2)
        {
            var sample = Math.Abs((short)(e.Buffer[i] | (e.Buffer[i + 1] << 8)));
            if (sample > peak) peak = sample;
        }
        // Keep the highest peak observed since last poll (simple VU-meter behavior)
        if (peak > _liveMicPeak)
            _liveMicPeak = peak;
    }

    private async Task GetLiveMicLevel(HttpListenerResponse resp)
    {
        var active = _liveMic != null;
        // Read-and-reset so each poll reflects activity since last call (bouncy meter)
        var peak = System.Threading.Interlocked.Exchange(ref _liveMicPeak, 0);
        var peakDb = peak > 0 ? 20.0 * Math.Log10(peak / 32768.0) : -96.0;

        var json = JsonSerializer.Serialize(new
        {
            ok = active,
            peak,
            peakDb = Math.Round(peakDb, 1),
            detected = peak > 500
        });

        await WriteJson(resp, json);
    }

    private async Task StopLiveMicTest(HttpListenerResponse resp)
    {
        StopLiveMicInternal();
        await WriteJson(resp, "{\"ok\":true}");
    }

    private async Task RunCameraTest(HttpListenerResponse resp)
    {
        if (_jarvisAutomation == null)
        {
            await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Camera service is unavailable." }));
            return;
        }

        try
        {
            var result = await _jarvisAutomation.CapturePhotoFromSettingsAsync(_cts?.Token ?? CancellationToken.None);
            await WriteJson(resp, JsonSerializer.Serialize(result));
        }
        catch (OperationCanceledException)
        {
            await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Camera test cancelled." }));
        }
        catch (Exception ex)
        {
            await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    private async Task RunScreenGuidanceTest(HttpListenerResponse resp)
    {
        if (_jarvisAutomation == null)
        {
            await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Screen guidance is unavailable." }));
            return;
        }

        try
        {
            var result = await _jarvisAutomation.InspectScreenFromSettingsAsync(
                "Briefly describe the visible screen and identify the safest next action. Do not repeat private values.",
                _cts?.Token ?? CancellationToken.None);
            await WriteJson(resp, JsonSerializer.Serialize(result));
        }
        catch (OperationCanceledException)
        {
            await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = "Screen guidance test cancelled." }));
        }
        catch (Exception ex)
        {
            await WriteJson(resp, JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
        }
    }

    private void StopLiveMicInternal()
    {
        lock (_liveMicLock)
        {
            if (_liveMic == null) return;
            try
            {
                _liveMic.DataAvailable -= OnLiveMicData;
                _liveMic.StopRecording();
                _liveMic.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Error($"Mic test stop: {ex.Message}");
            }
            finally
            {
                _liveMic = null;
                _liveMicPeak = 0;
            }
        }
    }

    private static async Task WriteJson(HttpListenerResponse resp, string json)
    {
        var buffer = Encoding.UTF8.GetBytes(json);
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private static bool HasSettingsActionHeader(HttpListenerRequest request) =>
        string.Equals(
            request.Headers["X-Keyboard-Wtf-Settings-Action"],
            "explicit-user-action",
            StringComparison.Ordinal);

    private static bool HasCompanionHeader(HttpListenerRequest request) =>
        string.Equals(
            request.Headers[BrowserCompanionBridgeService.BridgeHeader],
            BrowserCompanionBridgeService.BridgeHeaderValue,
            StringComparison.Ordinal);

    private async Task PreviewFilenameEndpoint(HttpListenerRequest req, HttpListenerResponse resp)
    {
        string resultJson;
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var pattern = doc.RootElement.TryGetProperty("pattern", out var p) ? p.GetString() : null;
            resultJson = JsonSerializer.Serialize(new { ok = true, preview = PreviewFilename(pattern) });
        }
        catch (Exception ex)
        {
            resultJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        var buffer = Encoding.UTF8.GetBytes(resultJson);
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private static string PreviewFilename(string pattern)
    {
        var effective = string.IsNullOrWhiteSpace(pattern) ? KeyboardWtfState.DefaultVoiceNoteFilenamePattern : pattern;
        string raw;
        try { raw = DateTime.Now.ToString(effective); }
        catch { return "(invalid format)"; }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(raw.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        return (string.IsNullOrWhiteSpace(sanitized) ? DateTime.Now.ToString(KeyboardWtfState.DefaultVoiceNoteFilenamePattern) : sanitized) + ".wav";
    }

    private async Task PickVoiceNotesFolder(HttpListenerResponse resp)
    {
        string resultJson;
        try
        {
            var currentPath = (KeyboardWtfState.EffectiveVoiceNoteSavePath ?? "").Replace("'", "''");

            // Build the PowerShell script. Single-quoted PS strings: inner apostrophes are doubled above.
            var script =
                "Add-Type -AssemblyName System.Windows.Forms | Out-Null\n" +
                "$f = New-Object System.Windows.Forms.FolderBrowserDialog\n" +
                "$f.Description = 'Select Voice Notes save folder'\n" +
                "$f.UseDescriptionForTitle = $true\n" +
                $"$f.SelectedPath = '{currentPath}'\n" +
                "$f.ShowNewFolderButton = $true\n" +
                "if ($f.ShowDialog() -eq 'OK') { [Console]::Out.WriteLine($f.SelectedPath) }\n";

            // Base64 UTF-16LE encode the script to bypass all command-line quoting rules.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var psExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(psExe)) psExe = "powershell.exe";

            var psi = new ProcessStartInfo
            {
                FileName = psExe,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -STA -EncodedCommand {encoded}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            AppLog.Info($"FolderPicker: launching {psExe}");
            using var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("Failed to start folder picker");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await Task.Run(() => proc.WaitForExit(120000));
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            AppLog.Info($"FolderPicker exit={proc.ExitCode} stdout='{stdout}' stderr='{stderr}'");

            if (!string.IsNullOrWhiteSpace(stdout) && Directory.Exists(stdout))
            {
                _settings?.SaveVoiceNoteSavePath(stdout);
                resultJson = JsonSerializer.Serialize(new { ok = true, path = stdout });
            }
            else if (!string.IsNullOrWhiteSpace(stderr))
            {
                resultJson = JsonSerializer.Serialize(new { ok = false, error = stderr });
            }
            else
            {
                resultJson = JsonSerializer.Serialize(new { ok = false, cancelled = true });
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"PickVoiceNotesFolder failed: {ex.Message}");
            resultJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        await WriteJson(resp, resultJson);
    }

    private async Task OpenVoiceNotesFolder(HttpListenerResponse resp)
    {
        string resultJson;
        try
        {
            var folder = KeyboardWtfState.EffectiveVoiceNoteSavePath;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            resultJson = JsonSerializer.Serialize(new { ok = true, path = folder });
        }
        catch (Exception ex)
        {
            resultJson = JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }

        var buffer = Encoding.UTF8.GetBytes(resultJson);
        resp.ContentType = "application/json";
        resp.ContentLength64 = buffer.Length;
        await resp.OutputStream.WriteAsync(buffer);
        resp.Close();
    }

    private static string MaskApiKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (key.Length <= 8) return "••••";
        return "••••" + key[^4..];
    }

    private static string MaskWebhookUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (url.Length <= 20) return "••••";
        return url[..20] + "••••";
    }

    private string LoadSettingsHtml()
    {
        try
        {
            return AppResources.ReadTextFile("settings.html");
        }
        catch (Exception ex)
        {
            AppLog.Error($"Failed to load settings.html: {ex.Message}");
            return null;
        }
    }

    private int FindAvailablePort(int startPort)
    {
        for (var port = startPort; port < startPort + 200; port++)
        {
            if (port < 1024) continue;
            try
            {
                using var probe = new HttpListener();
                probe.Prefixes.Add($"http://localhost:{port}/");
                probe.Start();
                probe.Stop();
                return port;
            }
            catch { }
        }

        AppLog.Warning($"No free port found from {startPort}");
        return startPort;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopLiveMicInternal();
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();

        try { _serverTask?.Wait(TimeSpan.FromSeconds(3)); }
        catch { }

        _cts?.Dispose();
        IsRunning = false;

        AppLog.Info("WebSettingsService stopped");
    }
}
