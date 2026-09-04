namespace KeyboardWtf.Services;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using KeyboardWtf.Helpers;
using KeyboardWtf.Services.BrowserAutomationV2;
using KeyboardWtf.Services.PaidStaging;

public sealed class JarvisAutomationService : IDisposable
{
    private const int SwHide = 0;
    private const int SwRestore = 9;
    private const int SwMinimize = 6;
    private const int SwMaximize = 3;
    private const uint WmClose = 0x0010;
    private const byte VkVolumeMute = 0xAD;
    private const byte VkVolumeDown = 0xAE;
    private const byte VkVolumeUp = 0xAF;
    private const uint KeyeventfKeyup = 0x0002;
    private static readonly IReadOnlyDictionary<string, string> KnownWebApps =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gmail"] = "https://mail.google.com/",
            ["google drive"] = "https://drive.google.com/",
            ["drive"] = "https://drive.google.com/",
            ["google calendar"] = "https://calendar.google.com/",
            ["calendar"] = "https://calendar.google.com/",
            ["google sheets"] = "https://docs.google.com/spreadsheets/",
            ["sheets"] = "https://docs.google.com/spreadsheets/",
            ["youtube"] = "https://www.youtube.com/",
            ["github"] = "https://github.com/",
            ["chatgpt"] = "https://chatgpt.com/",
            ["devpost"] = "https://devpost.com/",
            ["instagram"] = "https://www.instagram.com/",
            ["vercel"] = "https://vercel.com/",
            ["whatsapp"] = "https://web.whatsapp.com/",
        };
    private static readonly LearnedMappingKind[] DurablePreferenceKinds =
    [
        LearnedMappingKind.App,
        LearnedMappingKind.Link,
        LearnedMappingKind.File,
        LearnedMappingKind.Folder,
        LearnedMappingKind.Browser,
    ];

    private readonly NotificationService _notifications;
    private readonly SettingsService _settings;
    private readonly JarvisActionHistoryService _history;
    private readonly LearnedMappingService _mappings;
    private readonly AppResolverService _apps;
    private readonly SmartFileSearchService _files;
    private readonly BrowserLauncherService _browsers;
    private readonly BrowserHelperBridge _browserHelper;
    private readonly GeminiScreenGuidanceService _screenGuidance;
    private readonly WebcamCaptureService _webcam = new();
    private readonly Action _openSettings;
    private readonly object _pendingLock = new();
    private readonly object _conversationStateLock = new();
    private readonly JarvisTimerService _timerService;
    private readonly bool _ownsTimerService;
    private readonly object _selectionLock = new();
    private readonly string _todosPath = Path.Combine(SettingsService.AppDataDir, "jarvis-todos.json");
    private PendingJarvisAction _pending;
    private SelectionSnapshot _lastSelection;
    private string _latestUserTranscript = "";
    private DateTimeOffset _latestUserTranscriptAt;
    private string _latestOpenTarget = "";
    private DateTimeOffset _latestOpenTargetAt;
    private string _latestWorkflowName = "";
    private DateTimeOffset _latestWorkflowAt;
    private bool _executingApprovedAction;
    private bool _disposed;

    public JarvisAutomationService(
        NotificationService notifications,
        SettingsService settings,
        JarvisActionHistoryService history,
        LearnedMappingService mappings,
        Action openSettings,
        JarvisTimerService timerService = null)
        : this(
            notifications,
            settings,
            history,
            mappings,
            openSettings,
            timerService,
            null,
            null)
    {
    }

    internal JarvisAutomationService(
        NotificationService notifications,
        SettingsService settings,
        JarvisActionHistoryService history,
        LearnedMappingService mappings,
        Action openSettings,
        JarvisTimerService timerService,
        PaidAccountService paidAccount = null,
        BrowserHelperBridge browserHelper = null)
    {
        _notifications = notifications;
        _settings = settings;
        _history = history;
        _mappings = mappings;
        _apps = new AppResolverService(mappings);
        _files = new SmartFileSearchService(mappings);
        _browsers = new BrowserLauncherService(_apps, settings);
        _browserHelper = browserHelper;
        _screenGuidance = new GeminiScreenGuidanceService(paidAccount);
        _openSettings = openSettings;
        _timerService = timerService ?? new JarvisTimerService(notifications, history);
        _ownsTimerService = timerService == null;
    }

    public async Task<Dictionary<string, object>> ExecuteAsync(
        string toolName,
        JsonElement args,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        toolName = (toolName ?? "").Trim();
        if (ShouldRequestPermission(toolName, args))
            return RequestRoutinePermission(toolName, args);

        return await ExecuteCoreAsync(toolName, args, token);
    }

    private async Task<Dictionary<string, object>> ExecuteCoreAsync(
        string toolName,
        JsonElement args,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return toolName switch
        {
            "open_app" => OpenApp(
                GetString(args, "app_name"),
                GetString(args, "target_kind")),
            "open_url" => OpenUrl(GetString(args, "url"), GetString(args, "browser")),
            "open_folder" => await OpenFolderAsync(
                GetString(args, "folder"),
                GetString(args, "location"),
                token),
            "open_path" => await OpenPathAsync(
                GetString(args, "path"),
                GetString(args, "location"),
                token),
            "window_action" => await WindowActionAsync(
                GetString(args, "action"),
                GetString(args, "app_name"),
                token),
            "browser_action" => BrowserAction(GetString(args, "action")),
            "web_search" => WebSearch(
                GetString(args, "query"),
                GetString(args, "engine") ?? GetString(args, "service"),
                GetString(args, "browser")),
            "get_desktop_context" => GetDesktopContext(),
            "get_clipboard_text" => GetClipboardText(),
            "get_selected_text" => await GetSelectedTextAsync(token),
            "replace_selected_text" => await ReplaceSelectedTextAsync(GetString(args, "text"), token),
            "type_text" => await TypeTextAsync(GetString(args, "text"), token),
            "press_key" => PressKey(GetString(args, "key")),
            "search_files" => await SearchFilesAsync(
                GetString(args, "query"),
                GetString(args, "location"),
                token),
            "save_note" => SaveNote(GetString(args, "text"), GetString(args, "title")),
            "add_todo" => AddTodo(GetString(args, "text")),
            "list_todos" => ListTodos(),
            "complete_todo" => CompleteTodo(GetString(args, "query")),
            "set_timer" => SetTimer(
                GetInt(args, "minutes"),
                GetInt(args, "seconds"),
                GetString(args, "label")),
            "cancel_timer" => CancelTimer(GetString(args, "query")),
            "list_timers" => ListTimers(),
            "system_control" => SystemControl(GetString(args, "action"), GetInt(args, "value")),
            "system_status" => SystemStatus(),
            "play_media" => PlayMedia(GetString(args, "query"), GetString(args, "service")),
            "open_service_page" => OpenServicePage(
                GetString(args, "service"),
                GetString(args, "page"),
                GetString(args, "query")),
            "open_camera" => OpenCamera(),
            "take_photo" => await TakePhotoAsync(token),
            "take_screenshot" => TakeScreenshot(),
            "inspect_screen" => await InspectScreenAsync(GetString(args, "request"), token),
            "virtual_desktop_action" => VirtualDesktopAction(GetString(args, "action")),
            "windows_recording_action" => WindowsRecordingAction(GetString(args, "action")),
            "open_gmail_draft" => OpenGmailDraft(
                GetString(args, "recipient"),
                GetString(args, "subject"),
                GetString(args, "body")),
            "prepare_whatsapp_message" => PrepareWhatsAppMessage(
                GetString(args, "message"),
                GetString(args, "contact_name"),
                GetString(args, "phone_number")),
            "copy_text" => CopyText(GetString(args, "text")),
            "create_workflow" => CreateWorkflow(args),
            "remember_routine" => RememberRoutine(args),
            "edit_workflow" => EditWorkflow(args),
            "list_workflows" => ListWorkflows(),
            "run_workflow" => await RunWorkflowAsync(GetString(args, "name"), token),
            "delete_workflow" => DeleteWorkflow(GetString(args, "name")),
            "remember_app_alias" => RememberAppAlias(
                GetString(args, "alias"),
                GetString(args, "app_name")),
            "remember_path_alias" => RememberPathAlias(
                GetString(args, "alias"),
                GetString(args, "path"),
                GetString(args, "kind")),
            "remember_link_alias" => RememberLinkAlias(
                GetString(args, "alias"),
                GetString(args, "url")),
            "remember_workflow_alias" => RememberWorkflowAlias(
                GetString(args, "alias"),
                GetString(args, "workflow_name")),
            "list_learned_mappings" => ListLearnedMappings(),
            "forget_learned_mapping" => ForgetLearnedMapping(
                GetString(args, "id_or_alias"),
                GetString(args, "kind")),
            "set_browser_preference" => SetBrowserPreference(GetString(args, "browser")),
            "request_sensitive_action" => RequestSensitiveAction(
                GetString(args, "action"),
                GetString(args, "target")),
            "confirm_sensitive_action" => await ConfirmPendingActionAsync(token),
            "cancel_sensitive_action" => CancelPendingAction(),
            _ => Result(false, $"{toolName} is not a supported desktop action yet.", supported: false),
        };
    }

    private bool ShouldRequestPermission(string toolName, JsonElement args)
    {
        return JarvisPermissionPolicy.RequiresConfirmation(
            toolName,
            GetString(args, "action"),
            toolName == "open_app" ? GetString(args, "app_name") : "",
            _settings.Current.JarvisPermissionMode,
            _executingApprovedAction);
    }

    private Dictionary<string, object> RequestRoutinePermission(string toolName, JsonElement args)
    {
        var description = DescribeRoutineAction(toolName, args);
        lock (_pendingLock)
        {
            _pending = new PendingJarvisAction
            {
                ToolName = toolName,
                Args = args.ValueKind == JsonValueKind.Undefined
                    ? JsonSerializer.SerializeToElement(new { })
                    : args.Clone(),
                Action = toolName,
                Description = description,
            };
        }

        Models.KeyboardWtfState.SetUi(Models.VoiceUiPhase.Thinking, "Permission required", description);
        _history.Add("permission_requested", description, true, true);
        return Result(
            true,
            $"{description}. Ask the user to say confirm or cancel.",
            confirmationRequired: true,
            extras: new()
            {
                ["description"] = description,
                ["permission_mode"] = _settings.Current.JarvisPermissionMode.ToString(),
                ["always_requires_confirmation"] = toolName is "open_camera" or "take_photo"
                    or "take_screenshot" or "inspect_screen"
                    || (toolName == "virtual_desktop_action"
                        && NormalizeWords(GetString(args, "action")) is "close" or "close desktop" or "close current desktop"),
            });
    }

    private static string DescribeRoutineAction(string toolName, JsonElement args) => toolName switch
    {
        "open_app" => $"Open {GetString(args, "app_name")}",
        "open_url" => $"Open {GetString(args, "url")}",
        "open_folder" => $"Open the {GetString(args, "folder")} folder",
        "open_path" => $"Open {GetString(args, "path")}",
        "window_action" => $"{GetString(args, "action")} the requested window",
        "browser_action" => $"{GetString(args, "action")} in the active browser",
        "web_search" => $"Search for {GetString(args, "query")}",
        "replace_selected_text" => "Replace the selected text",
        "type_text" => "Type text into the active field",
        "press_key" => $"Press {GetString(args, "key")}",
        "save_note" => "Save a local note",
        "add_todo" => "Add a local to-do",
        "complete_todo" => "Complete a local to-do",
        "set_timer" => "Start a timer",
        "cancel_timer" => "Cancel a timer",
        "system_control" => $"{GetString(args, "action")}",
        "play_media" => $"Open {GetString(args, "query")} on {GetString(args, "service")}",
        "open_service_page" => $"Open {GetString(args, "page")} on {GetString(args, "service")}",
        "open_camera" => "Open the Windows Camera app",
        "take_photo" => "Open Windows Camera for a manually reviewed photo",
        "take_screenshot" => "Capture and save the current screen",
        "inspect_screen" => "Inspect the current screen once with Gemini",
        "virtual_desktop_action" => $"{GetString(args, "action")} virtual desktop",
        "windows_recording_action" => $"{GetString(args, "action")} Windows screen recording",
        "open_gmail_draft" => $"Prepare a Gmail draft for {GetString(args, "recipient")}",
        "prepare_whatsapp_message" => "Prepare a WhatsApp message",
        "copy_text" => "Copy prepared text to the clipboard",
        "create_workflow" => "Save a Jarvis workflow",
        "remember_routine" => $"Remember the phrase {GetString(args, "trigger")} as a routine",
        "edit_workflow" => $"Update workflow {GetString(args, "name")}",
        "run_workflow" => $"Run the {GetString(args, "name")} workflow",
        "delete_workflow" => $"Delete the {GetString(args, "name")} workflow",
        "remember_app_alias" => $"Remember {GetString(args, "alias")} as {GetString(args, "app_name")}",
        "remember_path_alias" => $"Remember {GetString(args, "alias")} as a local path",
        "remember_link_alias" => $"Remember {GetString(args, "alias")} as a web link",
        "remember_workflow_alias" => $"Remember {GetString(args, "alias")} as workflow {GetString(args, "workflow_name")}",
        "forget_learned_mapping" => $"Forget {GetString(args, "id_or_alias")}",
        "set_browser_preference" => $"Use {GetString(args, "browser")} as the preferred browser",
        _ => $"Run {toolName.Replace('_', ' ')}",
    };

    public void ObserveUserTranscript(string text)
    {
        PendingJarvisAction pending;
        lock (_pendingLock)
        {
            _latestUserTranscript = Limit((text ?? "").Trim(), 1000);
            _latestUserTranscriptAt = DateTimeOffset.UtcNow;
            pending = _pending;
        }
        if (pending == null)
            return;

        var normalized = NormalizePhraseText(text);
        if (ContainsPhrase(normalized, "cancel", "no", "nope", "do not", "don't", "stop", "never mind"))
        {
            lock (_pendingLock)
                _pending = null;
            _history.Add("confirmation_cancelled", pending.Description, true);
            return;
        }

        if (normalized.Length <= 80
            && ContainsPhrase(normalized, "yes", "yeah", "yep", "sure", "confirm", "go ahead", "proceed", "do it", "okay do it", "ok do it"))
        {
            lock (_pendingLock)
            {
                if (_pending?.Id == pending.Id)
                    _pending.ConfirmedAt = DateTimeOffset.UtcNow;
            }
        }
    }

    public object PendingSnapshot()
    {
        lock (_pendingLock)
        {
            if (_pending == null)
                return null;
            return new
            {
                _pending.Id,
                _pending.Action,
                _pending.Target,
                _pending.Description,
                _pending.ToolName,
                _pending.IsSensitive,
                _pending.RequestedAt,
                _pending.ConfirmedAt,
            };
        }
    }

    private Dictionary<string, object> OpenApp(string appName, string targetKind = "")
    {
        var nativeOnly = IsExplicitNativeTarget(targetKind);
        var resolverQuery = nativeOnly ? NormalizeNativeAppQuery(appName) : appName;
        var name = NormalizeWords(resolverQuery);
        if (string.IsNullOrWhiteSpace(name))
            return Result(false, "Which app should I open?", needsClarification: true);
        RememberTransientOpenTarget(name);

        if (IsWindowsSettingsTarget(name))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:",
                UseShellExecute = true,
            });
            return Logged("open_app", "Windows Settings", true, "Opened Windows Settings.");
        }

        if (IsKeyboardWtfSettingsTarget(name))
        {
            _openSettings();
            return Logged("open_app", "keyboard.wtf settings", true, "Opened keyboard.wtf settings.");
        }

        if (!nativeOnly)
        {
            var learnedLink = _mappings.FindExact(LearnedMappingKind.Link, appName)
                ?? _mappings.FindExact(LearnedMappingKind.Link, name);
            if (learnedLink != null)
                return OpenUrl(learnedLink.Target, "");
        }

        if (name is "downloads" or "download folder" or "downloads folder"
            or "documents" or "documents folder" or "pictures" or "pictures folder")
        {
            var known = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return OpenKnownFolder(known);
        }

        if (!nativeOnly && KnownWebApps.TryGetValue(name, out var webUrl))
            return OpenUrl(webUrl, "");
        if (name is "camera" or "windows camera")
            return OpenCamera();
        var resolution = _apps.Resolve(resolverQuery);
        if (resolution.Status == AppResolutionStatus.Found)
        {
            if (_apps.TryLaunch(resolution.Best, out var error))
                return Logged(
                    "open_app",
                    $"{resolverQuery} -> {resolution.Best.Name} ({resolution.Best.Source})",
                    true,
                    $"Opened {resolution.Best.Name}.",
                    extras: new()
                    {
                        ["matched_name"] = resolution.Best.Name,
                        ["match_score"] = Math.Round(resolution.Best.Score, 3),
                        ["source"] = resolution.Best.Source,
                    });
            return Logged("open_app", resolverQuery, false, $"I found {resolution.Best.Name}, but Windows could not open it: {error}", supported: false);
        }

        if (resolution.Status == AppResolutionStatus.Ambiguous)
        {
            return Result(
                false,
                $"I found several possible apps for {resolverQuery}. Ask the user to choose one by name.",
                needsClarification: true,
                extras: new()
                {
                    ["candidates"] = resolution.Candidates.Select(candidate => new
                    {
                        name = candidate.Name,
                        source = candidate.Source,
                        score = Math.Round(candidate.Score, 3),
                    }).ToArray(),
                    ["searched"] = resolution.SearchSummary,
                });
        }

        if (!nativeOnly && string.Equals(
            AppResolverService.CanonicalizeNaturalName(resolverQuery),
            "Apple Music",
            StringComparison.OrdinalIgnoreCase))
        {
            var webResult = OpenUrl("https://music.apple.com/", "");
            webResult["message"] = "Apple Music was not installed, so I opened Apple Music on the web.";
            return webResult;
        }

        return Logged(
            "open_app",
            name,
            false,
            nativeOnly
                ? $"I could not find an installed {resolverQuery} application. I searched {resolution.SearchSummary}."
                : $"I could not find {resolverQuery}. I searched {resolution.SearchSummary}.",
            supported: false,
            extras: new() { ["searched"] = resolution.SearchSummary });
    }

    internal static bool IsExplicitNativeTarget(string targetKind) =>
        NormalizeWords(targetKind) is "native" or "native app" or "app" or "application";

    internal static string NormalizeNativeAppQuery(string value)
    {
        var query = Regex.Replace(
            (value ?? "").Trim(),
            @"^(?:open|launch|start)\s+",
            "",
            RegexOptions.IgnoreCase);
        return Regex.Replace(
            query,
            @"\s+(?:native\s+)?(?:app|application)$",
            "",
            RegexOptions.IgnoreCase).Trim();
    }

    internal static bool IsWindowsSettingsTarget(string value) =>
        NormalizeWords(value) is "settings" or "windows settings";

    internal static bool IsKeyboardWtfSettingsTarget(string value) =>
        NormalizeWords(value) is "keyboard.wtf settings" or "keyboard wtf settings"
            or "jarvis settings" or "your settings";

    internal static bool TryResolveKnownWebDestination(
        string value,
        out string label,
        out string url)
    {
        label = "";
        url = "";
        var candidate = (value ?? "").Trim().Trim('"', '\'', '.', ',', ';', ':');
        if (candidate.Length == 0)
            return false;

        if (TryNormalizeExplicitWebUrl(candidate, out url))
        {
            label = candidate;
            return true;
        }

        candidate = Regex.Replace(
            candidate,
            @"^(?:the\s+)?(?:website|web\s+site|site|webpage|page)\s+|\s+(?:website|web\s+site|site|webpage|page)$",
            "",
            RegexOptions.IgnoreCase).Trim();
        var normalized = NormalizePhraseText(candidate);
        if (KnownWebApps.TryGetValue(normalized, out url))
        {
            label = candidate;
            return true;
        }

        var compact = Regex.Replace(normalized, @"[^a-z0-9]", "");
        var exactCompact = KnownWebApps.FirstOrDefault(entry =>
            string.Equals(
                Regex.Replace(NormalizePhraseText(entry.Key), @"[^a-z0-9]", ""),
                compact,
                StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exactCompact.Key))
        {
            label = candidate;
            url = exactCompact.Value;
            return true;
        }

        return false;
    }

    internal static bool TryResolveKnownWebDestinationMention(
        string value,
        out string label,
        out string url)
    {
        label = "";
        url = "";
        var text = value ?? "";
        var domainMatches = Regex.Matches(
            text,
            @"(?<![\w@])(?:https?://)?(?:www\.)?[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?)+(?:/[^\s,;]*)?",
            RegexOptions.IgnoreCase);
        if (domainMatches.Count > 0
            && TryResolveKnownWebDestination(
                domainMatches[^1].Value,
                out label,
                out url))
        {
            return true;
        }

        var normalized = NormalizePhraseText(text);
        foreach (var entry in KnownWebApps.OrderByDescending(pair => pair.Key.Length))
        {
            if (!ContainsPhrase(normalized, entry.Key))
                continue;
            label = entry.Key;
            url = entry.Value;
            return true;
        }

        return false;
    }

    private static bool TryNormalizeExplicitWebUrl(string value, out string url)
    {
        url = "";
        var candidate = value.Trim();
        if (!candidate.Contains('.'))
            return false;
        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate;
        }
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }
        url = uri.ToString();
        return true;
    }

    private Dictionary<string, object> OpenKnownFolder(string folder)
    {
        var name = NormalizeWords(folder);
        var path = name switch
        {
            "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            "documents" or "my documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "pictures" or "photos" or "images" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "home" or "user folder" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "onedrive" or "one drive" => Environment.GetEnvironmentVariable("OneDrive"),
            "app data" or "keyboard data" => SettingsService.AppDataDir,
            "voice notes" => Models.KeyboardWtfState.EffectiveVoiceNoteSavePath,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;
        if (!SmartFileSearchService.TryOpenSafe(path, out var error))
            return Logged("open_folder", path, false, error, supported: false);
        return Logged("open_folder", path, true, $"Opened {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}.");
    }

    private async Task<Dictionary<string, object>> OpenFolderAsync(
        string folder,
        string location,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return Result(false, "Which folder should I open?", needsClarification: true);
        var known = OpenKnownFolder(folder);
        if (known != null)
            return known;

        var expanded = ExpandPath(folder);
        if (File.Exists(expanded))
        {
            if (SmartFileSearchService.TryOpenContainingFolder(expanded, out var containingError))
            {
                return Logged(
                    "open_folder",
                    expanded,
                    true,
                    $"Opened the folder containing {Path.GetFileName(expanded)}.");
            }
            return Logged("open_folder", expanded, false, containingError, supported: false);
        }
        if (Directory.Exists(expanded))
        {
            if (SmartFileSearchService.TryOpenSafe(expanded, out var error))
                return Logged("open_folder", expanded, true, $"Opened {Path.GetFileName(expanded.TrimEnd(Path.DirectorySeparatorChar))}.");
            return Logged("open_folder", expanded, false, error, supported: false);
        }

        var requestedLocation = ResolveRecentFileLocation(location);
        var result = await _files.SearchAsync(
            folder,
            requestedLocation,
            includeFiles: false,
            includeFolders: true,
            token);
        return OpenResolvedSearchResult("open_folder", folder, result);
    }

    private async Task<Dictionary<string, object>> OpenPathAsync(
        string path,
        string location,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result(false, "Which file or folder should I open?", needsClarification: true);
        var expanded = ExpandPath(path);
        if (File.Exists(expanded) || Directory.Exists(expanded))
        {
            if (SmartFileSearchService.IsRiskyFile(expanded))
                return Logged(
                    "open_path",
                    expanded,
                    false,
                    "Opening executable, script, installer, shortcut, or registry files by path is blocked. Ask me to open an installed app by name instead.",
                    supported: false);
            if (SmartFileSearchService.TryOpenSafe(expanded, out var error))
                return Logged("open_path", expanded, true, $"Opened {Path.GetFileName(expanded.TrimEnd(Path.DirectorySeparatorChar))}.");
            return Logged("open_path", expanded, false, error, supported: false);
        }

        var requestedLocation = ResolveRecentFileLocation(location);
        var link = string.IsNullOrWhiteSpace(requestedLocation)
            ? _mappings.FindExact(LearnedMappingKind.Link, path)
            : null;
        if (link != null)
            return OpenUrl(link.Target, "");

        var result = await _files.SearchAsync(
            path,
            requestedLocation,
            includeFiles: true,
            includeFolders: true,
            token);
        return OpenResolvedSearchResult("open_path", path, result);
    }

    private Dictionary<string, object> OpenResolvedSearchResult(
        string action,
        string query,
        FileSearchResult search)
    {
        if (search.Status == FileResolutionStatus.Found)
        {
            if (SmartFileSearchService.IsRiskyFile(search.Best.Path))
                return Logged(
                    action,
                    search.Best.Path,
                    false,
                    "I found a matching executable, script, installer, shortcut, or registry file, but automatic opening is blocked.",
                    supported: false);
            if (SmartFileSearchService.TryOpenSafe(search.Best.Path, out var error))
            {
                return Logged(
                    action,
                    $"{query} -> {search.Best.Path}",
                    true,
                    $"Opened {search.Best.Name}.",
                    extras: new()
                    {
                        ["path"] = search.Best.Path,
                        ["score"] = Math.Round(search.Best.Score, 3),
                        ["searched"] = search.SearchSummary,
                    });
            }
            return Logged(action, search.Best.Path, false, error, supported: false);
        }

        if (search.Status == FileResolutionStatus.Ambiguous)
        {
            return Result(
                false,
                $"I found several plausible matches for {query}. Ask the user to choose one.",
                needsClarification: true,
                extras: new()
                {
                    ["matches"] = search.Candidates.Select(candidate => new
                    {
                        candidate.Name,
                        candidate.Path,
                        type = candidate.IsDirectory ? "folder" : "file",
                        modified_at = candidate.ModifiedAt,
                        score = Math.Round(candidate.Score, 3),
                    }).ToArray(),
                    ["searched"] = search.SearchSummary,
                    ["scanned_entries"] = search.ScannedEntries,
                });
        }

        var timeout = search.TimedOut ? " The bounded search reached its time limit." : "";
        return Logged(
            action,
            query,
            false,
            $"No matching safe file or folder was found in {search.SearchSummary}.{timeout}",
            supported: false,
            extras: new()
            {
                ["searched"] = search.SearchSummary,
                ["scanned_entries"] = search.ScannedEntries,
                ["timed_out"] = search.TimedOut,
            });
    }

    private async Task<Dictionary<string, object>> WindowActionAsync(
        string action,
        string appName,
        CancellationToken token)
    {
        var normalized = NormalizeWords(action);
        if (normalized is "close" or "quit" or "exit"
            && !IsExplicitBrowserApplication(appName))
        {
            var tabResult = await TryCloseMatchingBrowserTabAsync(appName, token);
            if (tabResult != null)
                return tabResult;
        }

        return WindowAction(action, appName);
    }

    private Dictionary<string, object> WindowAction(string action, string appName)
    {
        var normalized = NormalizeWords(action);
        if (normalized is "list" or "list windows")
        {
            var windows = EnumerateWindows()
                .Take(15)
                .Select(w => new { w.Title, w.ProcessName })
                .ToArray();
            return Result(true, windows.Length == 0 ? "No normal app windows were found." : "Open windows listed.",
                extras: new() { ["windows"] = windows });
        }

        var handle = string.IsNullOrWhiteSpace(appName)
            ? GetForegroundWindow()
            : FindWindowByName(appName);
        if (handle == IntPtr.Zero)
            return Logged("window_action", $"{action} {appName}", false, "I could not find that window.", supported: false);

        switch (normalized)
        {
            case "switch":
            case "focus":
            case "activate":
                ShowWindow(handle, SwRestore);
                SetForegroundWindow(handle);
                return Logged("window_action", $"focus {appName}", true, "Switched to the window.");
            case "minimize":
                ShowWindow(handle, SwMinimize);
                return Logged("window_action", $"minimize {appName}", true, "Window minimized.");
            case "maximize":
                ShowWindow(handle, SwMaximize);
                return Logged("window_action", $"maximize {appName}", true, "Window maximized.");
            case "restore":
                ShowWindow(handle, SwRestore);
                return Logged("window_action", $"restore {appName}", true, "Window restored.");
            case "close":
            case "quit":
            case "exit":
                if (!IsExplicitBrowserApplication(appName)
                    && IsBrowserWindow(handle))
                {
                    return Logged(
                        "window_action",
                        $"close {appName}",
                        false,
                        "A matching browser tab was not verified, so the browser was left open.",
                        supported: false);
                }
                var title = GetWindowTitle(handle);
                var label = string.IsNullOrWhiteSpace(title)
                    ? string.IsNullOrWhiteSpace(appName) ? "the active window" : appName
                    : Limit(title, 80);
                PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
                return Logged("window_action", $"close {label}", true, $"Closed {label}.");
            default:
                return Result(false, "Supported window actions are list, switch, minimize, maximize, restore, and close.", supported: false);
        }
    }

    internal void ResetConversationTransientContext()
    {
        lock (_conversationStateLock)
        {
            _latestOpenTarget = "";
            _latestOpenTargetAt = default;
            _latestWorkflowName = "";
            _latestWorkflowAt = default;
        }
    }

    private void RememberTransientOpenTarget(string target)
    {
        var clean = Limit((target ?? "").Trim(), 120);
        if (string.IsNullOrWhiteSpace(clean))
            return;
        lock (_conversationStateLock)
        {
            _latestOpenTarget = clean;
            _latestOpenTargetAt = DateTimeOffset.UtcNow;
        }
    }

    private string RecentOpenTarget()
    {
        lock (_conversationStateLock)
        {
            return DateTimeOffset.UtcNow - _latestOpenTargetAt <= TimeSpan.FromMinutes(5)
                ? _latestOpenTarget
                : "";
        }
    }

    private void RememberWorkflowContext(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        lock (_conversationStateLock)
        {
            _latestWorkflowName = name.Trim();
            _latestWorkflowAt = DateTimeOffset.UtcNow;
        }
    }

    private string RecentWorkflowName()
    {
        lock (_conversationStateLock)
        {
            return DateTimeOffset.UtcNow - _latestWorkflowAt <= TimeSpan.FromMinutes(10)
                ? _latestWorkflowName
                : "";
        }
    }

    private async Task<Dictionary<string, object>> TryCloseMatchingBrowserTabAsync(
        string target,
        CancellationToken token)
    {
        target = (target ?? "").Trim();
        if (_browserHelper == null || string.IsNullOrWhiteSpace(target))
            return null;
        var initial = _browserHelper.Status;
        if (!initial.Connected || initial.WindowId is null)
            return null;

        var browserWindow = EnumerateWindows()
            .FirstOrDefault(window => string.Equals(
                window.ProcessName,
                "chrome",
                StringComparison.OrdinalIgnoreCase));
        if (browserWindow == null || browserWindow.Handle == IntPtr.Zero)
            return null;

        ShowWindow(browserWindow.Handle, SwRestore);
        SetForegroundWindow(browserWindow.Handle);
        await Task.Delay(120, token);

        var selection = ResolveBrowserTarget(target);
        var active = _browserHelper.Status;
        if (!BrowserTargetMatches(selection, active.ActiveTitle, active.ActiveUrl))
        {
            SendKeysOnUiThread("^+a");
            await Task.Delay(180, token);
            SendKeysOnUiThread(EscapeSendKeysText(selection));
            await Task.Delay(100, token);
            SendKeysOnUiThread("{ENTER}");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
            do
            {
                await Task.Delay(120, token);
                active = _browserHelper.Status;
                if (BrowserTargetMatches(selection, active.ActiveTitle, active.ActiveUrl))
                    break;
            }
            while (DateTime.UtcNow < deadline);
        }

        if (!BrowserTargetMatches(selection, active.ActiveTitle, active.ActiveUrl))
        {
            SendKeysOnUiThread("{ESC}");
            return null;
        }

        var closingTabId = active.TabId;
        var closingUrl = active.ActiveUrl;
        SendKeysOnUiThread("^w");
        var verifyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
        do
        {
            await Task.Delay(120, token);
            var current = _browserHelper.Status;
            if (current.TabId != closingTabId
                || !string.Equals(current.ActiveUrl, closingUrl, StringComparison.Ordinal))
            {
                return Logged(
                    "close_browser_tab",
                    target,
                    true,
                    $"Closed the matching {target} browser tab.");
            }
        }
        while (DateTime.UtcNow < verifyDeadline);

        return Logged(
            "close_browser_tab",
            target,
            false,
            "Chrome did not confirm that the matching tab closed.",
            supported: false);
    }

    private string ResolveBrowserTarget(string target)
    {
        var mapping = _mappings.FindExact(LearnedMappingKind.Link, target);
        return mapping?.Target ?? target;
    }

    internal static bool BrowserTargetMatches(string target, string title, string url)
    {
        var needle = NormalizeWords(target);
        if (needle.Length < 2)
            return false;
        var titleWords = NormalizeWords(title);
        if (titleWords.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var targetUri)
            || !Uri.TryCreate(url, UriKind.Absolute, out var activeUri))
            return NormalizeWords(url).Contains(needle, StringComparison.OrdinalIgnoreCase);
        return string.Equals(
            NormalizeHost(targetUri.Host),
            NormalizeHost(activeUri.Host),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsExplicitBrowserApplication(string value) =>
        NormalizeWords(value) is "chrome" or "google chrome" or "browser" or "the browser"
            or "edge" or "microsoft edge" or "brave" or "firefox" or "opera" or "vivaldi" or "arc";

    private static bool IsBrowserWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(handle, out var processId);
        try
        {
            return IsBrowserProcess(Process.GetProcessById((int)processId).ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeSendKeysText(string value) =>
        Regex.Replace((value ?? "").Trim(), @"([+^%~(){}\[\]])", "{$1}");

    private static string NormalizeHost(string host) =>
        (host ?? "").StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? host[4..]
            : host ?? "";

    internal Dictionary<string, object> ExecuteBrowserShortcut(string action) =>
        BrowserAction(action);

    internal static bool IsDirectBrowserShortcutRequest(string request) =>
        TryGetBrowserTabSelection(request, out _, out _)
        || BrowserShortcutForAction(request) != null;

    internal static bool TryGetBrowserTabSelection(
        string request,
        out string selection,
        out string description)
    {
        selection = "";
        description = "";
        var normalized = NormalizePhraseText(request);
        if (normalized.StartsWith("please ", StringComparison.Ordinal))
            normalized = normalized["please ".Length..];

        var numberedTab = Regex.Match(
            normalized,
            @"^(?:(?:switch|go|change|open)\s+)?(?:to\s+)?(?:chrome(?:'s)?\s+)?(?:the\s+)?(?<tab>1|2|3|4|5|6|7|8|9|first|second|third|fourth|fifth|sixth|seventh|eighth|ninth)(?:st|nd|rd|th)?\s+tab(?:\s+using\s+(?:ctrl|control)\s*[1-9])?$",
            RegexOptions.IgnoreCase);
        if (numberedTab.Success)
        {
            var index = numberedTab.Groups["tab"].Value.ToLowerInvariant() switch
            {
                "first" => 1,
                "second" => 2,
                "third" => 3,
                "fourth" => 4,
                "fifth" => 5,
                "sixth" => 6,
                "seventh" => 7,
                "eighth" => 8,
                "ninth" => 9,
                var number when int.TryParse(number, out var parsed) => parsed,
                _ => 0,
            };
            if (index > 0)
            {
                selection = $"index:{index}";
                description = $"tab {index}";
                return true;
            }
        }

        if (Regex.IsMatch(
            normalized,
            @"^(?:(?:switch|go|change|open)\s+(?:to\s+)?)?(?:the\s+)?second(?:\s+to)?\s+last\s+tab$",
            RegexOptions.IgnoreCase))
        {
            selection = "from_end:2";
            description = "the second-last tab";
            return true;
        }

        if (Regex.IsMatch(
            normalized,
            @"^(?:(?:switch|go|change|open)\s+(?:to\s+)?)?(?:the\s+)?last\s+tab$",
            RegexOptions.IgnoreCase))
        {
            selection = "from_end:1";
            description = "the last tab";
            return true;
        }

        if (normalized is "next tab" or "go to next tab" or "go to the next tab"
            or "switch to next tab" or "switch to the next tab" or "change tab" or "switch tab")
        {
            selection = "next";
            description = "the next tab";
            return true;
        }

        if (normalized is "previous tab" or "go to previous tab" or "go to the previous tab"
            or "switch to previous tab" or "switch to the previous tab")
        {
            selection = "previous";
            description = "the previous tab";
            return true;
        }

        return false;
    }

    private Dictionary<string, object> BrowserAction(string action)
    {
        var keys = BrowserShortcutForAction(action);
        if (keys == null)
            return Result(false, "That browser action is not supported yet.", supported: false);

        var activeWindow = GetForegroundWindow();
        var activeProcess = GetProcessName(activeWindow);
        var foregroundedBrowser = false;
        if (!IsBrowserProcess(activeProcess))
        {
            var browser = EnumerateWindows()
                .FirstOrDefault(window => IsBrowserProcess(window.ProcessName));
            if (browser == null)
            {
                return Result(
                    false,
                    "I could not find an open supported browser window.",
                    supported: false,
                    needsClarification: true,
                    extras: new() { ["active_process"] = activeProcess });
            }

            if (string.Equals(
                browser.ProcessName,
                "chrome",
                StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    BrowserTrustedInput.FocusChromeWindowForAutomation(browser.Title);
                }
                catch (InvalidOperationException ex)
                {
                    return Result(
                        false,
                        ex.Message,
                        supported: false,
                        needsClarification: true,
                        extras: new() { ["active_process"] = activeProcess });
                }
            }
            else
            {
                ShowWindow(browser.Handle, SwRestore);
                SetForegroundWindow(browser.Handle);
            }
            Thread.Sleep(120);
            activeWindow = GetForegroundWindow();
            activeProcess = GetProcessName(activeWindow);
            foregroundedBrowser = IsBrowserProcess(activeProcess);
            if (!foregroundedBrowser)
            {
                return Result(
                    false,
                    "Windows did not allow me to focus the open browser for that shortcut.",
                    supported: false,
                    needsClarification: true,
                    extras: new() { ["active_process"] = activeProcess });
            }
        }

        try
        {
            if (string.Equals(keys, "^9^+{TAB}", StringComparison.Ordinal))
            {
                SendKeysOnUiThread("^9");
                Thread.Sleep(80);
                SendKeysOnUiThread("^+{TAB}");
            }
            else
            {
                SendKeysOnUiThread(keys);
            }
        }
        catch (Exception ex)
        {
            return Result(
                false,
                $"The browser shortcut could not be sent: {ex.Message}",
                supported: false);
        }
        return Logged(
            "browser_action",
            action,
            true,
            $"Sent the browser shortcut for {action}.",
            extras: new()
            {
                ["shortcut_sent"] = true,
                ["foregrounded_browser"] = foregroundedBrowser,
                ["verified_browser_state"] = false,
            });
    }

    public static string BrowserShortcutForAction(string action)
    {
        var normalized = NormalizePhraseText(action);
        if (normalized.StartsWith("please ", StringComparison.Ordinal))
            normalized = normalized["please ".Length..];
        if (TryGetBrowserTabSelection(normalized, out _, out _))
            return null;

        return normalized switch
        {
            "new tab" => "^t",
            "close tab" or "close this tab" or "close website" or "close this website" => "^w",
            "reopen tab" or "reopen closed tab" => "^+t",
            "refresh" or "reload" => "^r",
            "back" => "%{LEFT}",
            "forward" => "%{RIGHT}",
            "focus address" or "address bar" => "^l",
            "find" or "find on page" => "^f",
            "downloads" => "^j",
            "history" => "^h",
            _ => null,
        };
    }

    private Dictionary<string, object> WebSearch(string query, string engine, string browser)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Result(false, "What should I search for?", needsClarification: true);
        return OpenUrl(BrowserLauncherService.BuildSearchUrl(query, engine), browser);
    }

    private Dictionary<string, object> GetDesktopContext()
    {
        var handle = GetForegroundWindow();
        var title = GetWindowTitle(handle);
        var processName = GetProcessName(handle);
        var clipboard = Limit(ClipboardHelper.GetText(), 2500);
        return Result(true, "Desktop context captured.", extras: new()
        {
            ["active_window_title"] = title,
            ["active_process"] = processName,
            ["clipboard_text"] = clipboard,
            ["clipboard_truncated"] = clipboard.Length >= 2500,
        });
    }

    private Dictionary<string, object> GetClipboardText()
    {
        var text = Limit(ClipboardHelper.GetText(), 6000);
        return Result(true, string.IsNullOrWhiteSpace(text) ? "Clipboard is empty." : "Clipboard text captured.",
            extras: new() { ["text"] = text });
    }

    private async Task<Dictionary<string, object>> GetSelectedTextAsync(CancellationToken token)
    {
        var context = CaptureSelectionContext();
        foreach (var delayMs in SelectionCaptureRetryDelaysMs)
        {
            token.ThrowIfCancellationRequested();
            var previous = ClipboardHelper.Capture();
            var marker = $"keyboard-wtf-{Guid.NewGuid():N}";
            string selected;
            try
            {
                ClipboardHelper.SetText(marker);
                SendKeysOnUiThread("^c");
                await Task.Delay(delayMs, token);
                selected = ClipboardHelper.GetText();
            }
            finally
            {
                ClipboardHelper.Restore(previous);
            }
            var currentContext = CaptureSelectionContext();
            if (selected != marker
                && !string.IsNullOrWhiteSpace(selected)
                && SelectionContextsAreCompatible(context, currentContext))
            {
                var snapshot = new SelectionSnapshot(
                    selected,
                    DateTimeOffset.UtcNow,
                    currentContext.Handle,
                    currentContext.ProcessName,
                    currentContext.WindowTitle);
                lock (_selectionLock)
                    _lastSelection = snapshot;
                return SelectionResult(snapshot.Text, fresh: true);
            }
        }

        SelectionSnapshot cached;
        lock (_selectionLock)
            cached = _lastSelection;
        if (CanUseCachedSelection(cached, context, DateTimeOffset.UtcNow))
            return SelectionResult(cached.Text, fresh: false);

        return Result(
            false,
            "I couldn't read the current selection. Reselect it and try once more.",
            supported: false);
    }

    private static Dictionary<string, object> SelectionResult(string text, bool fresh) =>
        Result(true, fresh ? "Selected text captured." : "Used the recent selection from this same window.", extras: new()
        {
            ["text"] = Limit(text, 6000),
            ["truncated"] = text.Length > 6000,
            ["fresh"] = fresh,
        });

    private static SelectionContext CaptureSelectionContext()
    {
        var handle = GetForegroundWindow();
        return new SelectionContext(handle, GetProcessName(handle), GetWindowTitle(handle));
    }

    internal static readonly int[] SelectionCaptureRetryDelaysMs = [160, 260];
    internal static readonly TimeSpan SelectionCacheMaximumAge = TimeSpan.FromSeconds(90);

    internal static bool CanUseCachedSelection(
        SelectionSnapshot snapshot,
        SelectionContext current,
        DateTimeOffset now) =>
        snapshot is not null
        && !string.IsNullOrWhiteSpace(snapshot.Text)
        && now - snapshot.CapturedAt <= SelectionCacheMaximumAge
        && SelectionContextsAreCompatible(
            new SelectionContext(snapshot.Handle, snapshot.ProcessName, snapshot.WindowTitle),
            current);

    internal static bool SelectionContextsAreCompatible(SelectionContext expected, SelectionContext current) =>
        expected is not null
        && current is not null
        && expected.Handle != IntPtr.Zero
        && expected.Handle == current.Handle
        && string.Equals(expected.ProcessName, current.ProcessName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.WindowTitle, current.WindowTitle, StringComparison.Ordinal);

    private async Task<Dictionary<string, object>> ReplaceSelectedTextAsync(string text, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result(false, "What text should replace the selection?", needsClarification: true);
        var previous = ClipboardHelper.Capture();
        try
        {
            ClipboardHelper.SetText(text);
            SendKeysOnUiThread("^v");
            await Task.Delay(220, token);
        }
        finally
        {
            ClipboardHelper.Restore(previous);
        }
        return Logged("replace_selected_text", Limit(text, 120), true, "Replaced the selected text.");
    }

    private async Task<Dictionary<string, object>> TypeTextAsync(string text, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result(false, "What should I type?", needsClarification: true);
        var previous = ClipboardHelper.Capture();
        try
        {
            ClipboardHelper.SetText(text);
            SendKeysOnUiThread("^v");
            await Task.Delay(220, token);
        }
        finally
        {
            ClipboardHelper.Restore(previous);
        }
        return Logged("type_text", Limit(text, 120), true, "Typed the text into the active field.");
    }

    private Dictionary<string, object> PressKey(string key)
    {
        var sequence = NormalizeWords(key) switch
        {
            "tab" => "{TAB}",
            "escape" or "esc" => "{ESC}",
            "up" => "{UP}",
            "down" => "{DOWN}",
            "left" => "{LEFT}",
            "right" => "{RIGHT}",
            "page up" => "{PGUP}",
            "page down" => "{PGDN}",
            "home" => "{HOME}",
            "end" => "{END}",
            _ => null,
        };
        if (sequence == null)
            return Result(false, "That key is not supported.", supported: false);
        SendKeysOnUiThread(sequence);
        return Logged("press_key", key, true, $"Pressed {key}.");
    }

    private async Task<Dictionary<string, object>> SearchFilesAsync(
        string query,
        string location,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Result(false, "What file or folder should I search for?", needsClarification: true);

        location = ResolveRecentFileLocation(location);
        var search = await _files.SearchAsync(
            query,
            location,
            includeFiles: true,
            includeFolders: true,
            token,
            maxResults: 15);
        var success = search.Status is FileResolutionStatus.Found or FileResolutionStatus.Ambiguous;
        _history.Add("search_files", $"{query} in {location}", success);
        return Result(
            success,
            search.Candidates.Count == 0
                ? $"No matching files or folders were found in {search.SearchSummary}."
                : $"Found {search.Candidates.Count} ranked matches.",
            extras: new()
            {
                ["matches"] = search.Candidates.Select(candidate => new
                {
                    candidate.Name,
                    candidate.Path,
                    containing_folder = candidate.IsDirectory
                        ? candidate.Path
                        : Path.GetDirectoryName(candidate.Path),
                    type = candidate.IsDirectory ? "folder" : "file",
                    modified_at = candidate.ModifiedAt,
                    score = Math.Round(candidate.Score, 3),
                    risky = SmartFileSearchService.IsRiskyFile(candidate.Path),
                }).ToArray(),
                ["searched"] = search.SearchSummary,
                ["scanned_entries"] = search.ScannedEntries,
                ["timed_out"] = search.TimedOut,
            });
    }

    private string ResolveRecentFileLocation(string explicitLocation)
    {
        string recentTranscript;
        DateTimeOffset observedAt;
        lock (_pendingLock)
        {
            recentTranscript = _latestUserTranscript;
            observedAt = _latestUserTranscriptAt;
        }
        if (DateTimeOffset.UtcNow - observedAt > TimeSpan.FromSeconds(30))
            recentTranscript = "";
        return SmartFileSearchService.ResolveRequestedLocation(
            recentTranscript,
            explicitLocation);
    }

    private Dictionary<string, object> SaveNote(string text, string title)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result(false, "What should I save in the note?", needsClarification: true);
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "keyboard.wtf");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Jarvis Notes.md");
        var heading = string.IsNullOrWhiteSpace(title) ? "Quick note" : title.Trim();
        File.AppendAllText(path, $"{Environment.NewLine}## {heading} - {DateTime.Now:g}{Environment.NewLine}{text.Trim()}{Environment.NewLine}");
        return Logged("save_note", path, true, "Saved the note.", extras: new() { ["path"] = path });
    }

    private Dictionary<string, object> AddTodo(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result(false, "What should I add to the to-do list?", needsClarification: true);
        var todos = LoadTodos();
        todos.Add(new JarvisTodo { Text = Limit(text, 240) });
        SaveTodos(todos);
        return Logged("add_todo", text, true, "Added to the to-do list.");
    }

    private Dictionary<string, object> ListTodos()
    {
        var todos = LoadTodos();
        return Result(true, todos.Count == 0 ? "The to-do list is empty." : "To-do list loaded.", extras: new()
        {
            ["todos"] = todos.Select(t => new { t.Id, t.Text, t.Completed, t.CreatedAt }).ToArray(),
        });
    }

    private Dictionary<string, object> CompleteTodo(string query)
    {
        var todos = LoadTodos();
        var todo = todos.FirstOrDefault(t =>
            !t.Completed && t.Text.Contains(query ?? "", StringComparison.OrdinalIgnoreCase));
        if (todo == null)
            return Result(false, "I could not find a matching unfinished to-do.", supported: false);
        todo.Completed = true;
        SaveTodos(todos);
        return Logged("complete_todo", todo.Text, true, "Marked the to-do complete.");
    }

    private Dictionary<string, object> SetTimer(int minutes, int seconds, string label)
    {
        var duration = seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 1440));
        var timer = _timerService.Start(duration, label);
        return Result(
            true,
            $"Timer set for {FormatTimerDuration(duration)}.",
            extras: new()
            {
                ["timer_id"] = timer.Id,
                ["label"] = timer.Label,
                ["due_at"] = timer.DueAt.ToString("O"),
            });
    }

    private Dictionary<string, object> CancelTimer(string query)
    {
        var count = _timerService.Cancel(query);
        return Result(
            count > 0,
            count switch
            {
                0 => "I could not find a matching active timer.",
                1 => "Timer cancelled.",
                _ => $"{count} timers cancelled.",
            },
            supported: count > 0,
            needsClarification: count == 0);
    }

    private Dictionary<string, object> ListTimers()
    {
        var timers = _timerService.ActiveTimers;
        return Result(
            true,
            timers.Count == 0
                ? "There are no active timers."
                : timers.Count == 1
                    ? "There is 1 active timer."
                    : $"There are {timers.Count} active timers.",
            extras: new()
            {
                ["timers"] = timers.Select(timer => new
                {
                    timer.Id,
                    timer.Label,
                    due_at = timer.DueAt,
                    remaining_seconds = Math.Max(0, (int)Math.Ceiling(timer.Remaining.TotalSeconds)),
                }).ToArray(),
            });
    }

    private static string FormatTimerDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1 && duration.TotalSeconds % 60 == 0)
        {
            var minutes = (int)duration.TotalMinutes;
            return $"{minutes} minute{(minutes == 1 ? "" : "s")}";
        }
        var seconds = Math.Max(1, (int)Math.Round(duration.TotalSeconds));
        return $"{seconds} second{(seconds == 1 ? "" : "s")}";
    }

    private Dictionary<string, object> SystemControl(string action, int value)
    {
        var normalized = NormalizeWords(action);
        switch (normalized)
        {
            case "volume up":
                PressMediaKey(VkVolumeUp, Math.Clamp(value <= 0 ? 2 : value, 1, 20));
                return Logged("system_control", action, true, "Volume increased.");
            case "volume down":
                PressMediaKey(VkVolumeDown, Math.Clamp(value <= 0 ? 2 : value, 1, 20));
                return Logged("system_control", action, true, "Volume decreased.");
            case "mute":
            case "toggle mute":
                PressMediaKey(VkVolumeMute, 1);
                return Logged("system_control", action, true, "Mute toggled.");
            case "bluetooth settings":
                return OpenUrl("ms-settings:bluetooth");
            case "wifi settings":
                return OpenUrl("ms-settings:network-wifi");
            case "display settings":
                return OpenUrl("ms-settings:display");
            case "sound settings":
                return OpenUrl("ms-settings:sound");
            default:
                return Result(false, "Supported direct controls are volume, mute, and opening Wi-Fi, Bluetooth, display, or sound settings.", supported: false);
        }
    }

    private Dictionary<string, object> SystemStatus()
    {
        var power = SystemInformation.PowerStatus;
        var batteryPercent = power.BatteryLifePercent < 0
            ? (int?)null
            : (int)Math.Round(power.BatteryLifePercent * 100);
        return Result(true, "System status captured.", extras: new()
        {
            ["battery_percent"] = batteryPercent,
            ["plugged_in"] = power.PowerLineStatus == PowerLineStatus.Online,
            ["uptime_minutes"] = (long)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalMinutes,
            ["machine_name"] = Environment.MachineName,
            ["user_name"] = Environment.UserName,
            ["os"] = Environment.OSVersion.VersionString,
        });
    }

    private Dictionary<string, object> PlayMedia(string query, string service)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Result(false, "What should I play?", needsClarification: true);

        var cleanQuery = query.Trim();
        var normalizedService = NormalizeWords(service);
        if (string.IsNullOrWhiteSpace(normalizedService))
            normalizedService = "spotify";

        if (normalizedService is "spotify" or "spotify app")
        {
            var encoded = Uri.EscapeDataString(cleanQuery);
            if (TryStart($"spotify:search:{encoded}"))
            {
                return Logged(
                    "play_media",
                    $"Spotify: {cleanQuery}",
                    true,
                    $"Opened Spotify search for {cleanQuery}. Select the result and press Play.",
                    extras: new()
                    {
                        ["service"] = "spotify",
                        ["direct_playback"] = false,
                        ["requires_spotify_oauth"] = true,
                    });
            }

            var result = OpenUrl($"https://open.spotify.com/search/{encoded}");
            result["direct_playback"] = false;
            result["requires_spotify_oauth"] = true;
            result["message"] = $"Opened Spotify search for {cleanQuery}. Select the result and press Play.";
            return result;
        }

        if (normalizedService is "youtube" or "youtube music")
            return OpenUrl($"https://www.youtube.com/results?search_query={Uri.EscapeDataString(cleanQuery)}");

        return Result(false, "Supported media services are Spotify and YouTube.", supported: false);
    }

    private Dictionary<string, object> OpenServicePage(string service, string page, string query)
    {
        var normalizedService = NormalizeWords(service);
        var normalizedPage = NormalizeWords(page);
        var encoded = Uri.EscapeDataString(query?.Trim() ?? "");

        if (normalizedService is "amazon" or "amazon india" or "amazon.in")
        {
            if (normalizedPage is not ("search" or "products"))
                return Result(
                    false,
                    "Amazon cart changes are not supported safely yet. I can open a product search for manual review.",
                    supported: false);
            if (string.IsNullOrWhiteSpace(query))
                return Result(false, "What should I search for on Amazon?", needsClarification: true);
            return OpenUrl($"https://www.amazon.in/s?k={encoded}");
        }

        if (normalizedService is "youtube")
        {
            if (normalizedPage is "liked videos" or "liked playlist" or "likes")
                return OpenUrl("https://www.youtube.com/playlist?list=LL");
            if (normalizedPage is "subscriptions")
                return OpenUrl("https://www.youtube.com/feed/subscriptions");
            if (normalizedPage is "history")
                return OpenUrl("https://www.youtube.com/feed/history");
            return Result(
                false,
                "Bulk unlike and account-changing YouTube actions are not supported safely. I can open Liked Videos, Subscriptions, or History.",
                supported: false);
        }

        if (normalizedService is "spotify")
        {
            if (normalizedPage is "liked songs" or "liked music" or "library")
            {
                if (TryStart("spotify:collection:tracks"))
                    return Logged("open_service_page", "Spotify liked songs", true, "Opened Spotify Liked Songs.");
                return OpenUrl("https://open.spotify.com/collection/tracks");
            }
            return Result(false, "Supported Spotify pages are Liked Songs and media search.", supported: false);
        }

        if (normalizedService is "discord")
        {
            if (normalizedPage is "home" or "app" or "friends")
            {
                if (TryStart("discord://"))
                    return Logged("open_service_page", "Discord", true, "Opened Discord.");
                return OpenUrl("https://discord.com/app");
            }
            return Result(
                false,
                "Discord messaging is not configured in this build. It requires a registered Discord app, OAuth permission, and a stable recipient ID; personal-account UI automation is intentionally blocked.",
                supported: false);
        }

        return Result(false, "That service page is not supported yet.", supported: false);
    }

    private Dictionary<string, object> OpenCamera()
    {
        var resolution = _apps.Resolve("Camera", refresh: true);
        var opened = resolution.Status == AppResolutionStatus.Found
            && _apps.TryLaunch(resolution.Best, out _);
        if (!opened)
            opened = TryStart("microsoft.windows.camera:");
        if (!opened)
        {
            return Logged(
                "open_camera",
                "",
                false,
                "Windows Camera could not be opened. Check that the Camera app is installed and camera access is enabled under Windows Settings > Privacy & security > Camera.",
                supported: false,
                extras: new() { ["settings_uri"] = "ms-settings:privacy-webcam" });
        }

        return Logged(
            "open_camera",
            "Windows Camera",
            true,
            "Opened Windows Camera.",
            extras: new() { ["automatic_capture"] = true });
    }

    public Dictionary<string, object> OpenCameraFromSettings() => OpenCamera();

    private Dictionary<string, object> TakeScreenshot()
    {
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return Logged("take_screenshot", "", false, "The screen could not be captured.", supported: false);

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "keyboard.wtf",
            "Screenshots");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png");

        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        bitmap.Save(path, ImageFormat.Png);
        return Logged(
            "take_screenshot",
            path,
            true,
            "Screenshot saved.",
            extras: new() { ["path"] = path });
    }

    private async Task<Dictionary<string, object>> InspectScreenAsync(string request, CancellationToken token)
    {
        try
        {
            var guidance = await _screenGuidance.InspectAsync(request, token);
            return Logged(
                "inspect_screen",
                Limit(request, 180),
                true,
                guidance,
                extras: new()
                {
                    ["guidance"] = guidance,
                    ["capture_persisted"] = false,
                    ["capture_scope"] = "one approved screen image",
                    ["automation_performed"] = false,
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Screen guidance failed");
            return Logged(
                "inspect_screen",
                Limit(request, 180),
                false,
                $"I could not analyze the approved screen capture: {ex.Message}",
                supported: false,
                extras: new() { ["capture_persisted"] = false });
        }
    }

    public Task<Dictionary<string, object>> InspectScreenFromSettingsAsync(
        string request,
        CancellationToken token) =>
        InspectScreenAsync(request, token);

    private async Task<Dictionary<string, object>> TakePhotoAsync(CancellationToken token)
    {
        var result = await _webcam.CapturePhotoAsync(token);
        if (!result.Success)
        {
            Models.KeyboardWtfState.SetUi(Models.VoiceUiPhase.Error, "Camera unavailable", result.Message);
            return Logged(
                "take_photo",
                "",
                false,
                result.Message,
                supported: false,
                extras: new()
                {
                    ["automatic_capture"] = true,
                    ["photo_saved"] = false,
                    ["settings_uri"] = "ms-settings:privacy-webcam",
                });
        }

        return Logged(
            "take_photo",
            result.Path,
            true,
            result.Message,
            extras: new()
            {
                ["automatic_capture"] = true,
                ["photo_saved"] = true,
                ["path"] = result.Path,
                ["camera_index"] = result.CameraIndex,
                ["backend"] = result.Backend,
            });
    }

    public Task<Dictionary<string, object>> CapturePhotoFromSettingsAsync(CancellationToken token) =>
        TakePhotoAsync(token);

    private Dictionary<string, object> VirtualDesktopAction(string action)
    {
        var normalized = NormalizeWords(action);
        var key = VirtualDesktopKeyForAction(normalized);
        if (key == 0)
            return Result(false, "Supported virtual desktop actions are create, next, previous, and close.", supported: false);

        SendChord([0x5B, 0x11, key]);
        var message = key switch
        {
            0x44 => "Requested a new virtual desktop.",
            0x27 => "Requested the next virtual desktop.",
            0x25 => "Requested the previous virtual desktop.",
            0x73 => "Requested closing the current virtual desktop.",
            _ => "Virtual desktop shortcut sent.",
        };
        return Logged(
            "virtual_desktop_action",
            normalized,
            true,
            message,
            extras: new() { ["shortcut_sent"] = true, ["verified_state"] = false });
    }

    public static byte VirtualDesktopKeyForAction(string action)
    {
        return NormalizeWords(action) switch
        {
            "create" or "new" or "new desktop" or "create desktop" or "create new desktop" or "create a new desktop" => (byte)0x44,
            "next" or "next desktop" or "switch next" or "switch to next desktop" or "move to next desktop" => (byte)0x27,
            "previous" or "previous desktop" or "switch previous" or "switch to previous desktop" => (byte)0x25,
            "close" or "close desktop" or "close current desktop" => (byte)0x73,
            _ => (byte)0,
        };
    }

    private Dictionary<string, object> WindowsRecordingAction(string action)
    {
        var normalized = NormalizeWords(action);
        var shortcut = WindowsRecordingShortcutForAction(normalized);
        if (shortcut == null)
        {
            return Result(
                false,
                "Supported recording actions are start, stop, toggle, open Game Bar, and select a recording region.",
                supported: false);
        }

        SendChord(shortcut);
        var regionCapture = shortcut.SequenceEqual(new byte[] { 0x5B, 0x10, 0x52 });
        var message = regionCapture
            ? "Opened the Windows screen-region recording picker. Windows controls the selected area and save result."
            : normalized is "open" or "open game bar" or "game bar"
                ? "Opened Xbox Game Bar."
                : "Recording shortcut sent. Xbox Game Bar controls whether recording actually starts or stops.";
        return Logged(
            "windows_recording_action",
            normalized,
            true,
            message,
            extras: new()
            {
                ["shortcut_sent"] = true,
                ["verified_recording_state"] = false,
                ["shortcut"] = regionCapture ? "Win+Shift+R" : shortcut.Length == 2 ? "Win+G" : "Win+Alt+R",
            });
    }

    public static byte[] WindowsRecordingShortcutForAction(string action)
    {
        return NormalizeWords(action) switch
        {
            "start" or "stop" or "toggle" or "record" or "start recording" or "stop recording"
                or "toggle recording" or "start screen recording" or "record my screen"
                or "start windows recording" or "use game bar recording" => [0x5B, 0x12, 0x52],
            "open" or "open game bar" or "game bar" => [0x5B, 0x47],
            "region" or "record region" or "select region" or "record screen region"
                or "snipping tool recording" => [0x5B, 0x10, 0x52],
            _ => null,
        };
    }

    private Dictionary<string, object> RememberAppAlias(string alias, string appName)
    {
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(appName))
            return Result(false, "Provide both the phrase to remember and the installed app name.", needsClarification: true);
        var resolution = _apps.Resolve(appName, refresh: true);
        if (resolution.Status != AppResolutionStatus.Found)
        {
            return Result(
                false,
                resolution.Status == AppResolutionStatus.Ambiguous
                    ? $"Several installed apps match {appName}. Ask the user to choose the exact app name first."
                    : $"I could not find an installed app matching {appName}.",
                supported: false,
                needsClarification: resolution.Status == AppResolutionStatus.Ambiguous,
                extras: new()
                {
                    ["candidates"] = resolution.Candidates.Select(candidate => candidate.Name).ToArray(),
                });
        }

        var entry = _mappings.RememberExclusive(
            LearnedMappingKind.App,
            alias,
            resolution.Best.LaunchTarget,
            resolution.Best.Name,
            DurablePreferenceKinds);
        var persisted = _mappings.FindExact(LearnedMappingKind.App, alias);
        if (!MappingRecordsMatch(entry, persisted))
            return Result(false, "The app preference could not be read back, so I did not mark it as remembered.");
        return Logged(
            "remember_app_alias",
            $"{entry.Alias} -> {entry.DisplayName}",
            true,
            $"I will use {entry.DisplayName} when you say {entry.Alias}.",
            extras: new()
            {
                ["mapping"] = PublicMapping(persisted),
                ["persistence_verified"] = true,
            });
    }

    private Dictionary<string, object> RememberPathAlias(string alias, string path, string kind)
    {
        var expanded = ExpandPath(path);
        var isFolder = Directory.Exists(expanded);
        var isFile = File.Exists(expanded);
        if (!isFolder && !isFile)
            return Result(false, "That local file or folder does not exist.", supported: false);
        if (isFile && SmartFileSearchService.IsRiskyFile(expanded))
            return Result(false, "Executable, script, installer, shortcut, and registry files cannot be saved as openable aliases.", supported: false);
        var mappingKind = NormalizeWords(kind) == "folder" || isFolder
            ? LearnedMappingKind.Folder
            : LearnedMappingKind.File;
        var entry = _mappings.RememberExclusive(
            mappingKind,
            alias,
            expanded,
            Path.GetFileName(expanded.TrimEnd(Path.DirectorySeparatorChar)),
            DurablePreferenceKinds);
        var persisted = _mappings.FindExact(mappingKind, alias);
        if (!MappingRecordsMatch(entry, persisted))
            return Result(false, "The path preference could not be read back, so I did not mark it as remembered.");
        return Logged(
            "remember_path_alias",
            $"{entry.Alias} -> {entry.Target}",
            true,
            $"Remembered {entry.Alias}.",
            extras: new()
            {
                ["mapping"] = PublicMapping(persisted),
                ["persistence_verified"] = true,
            });
    }

    private Dictionary<string, object> RememberLinkAlias(string alias, string url)
    {
        var entry = _mappings.RememberExclusive(
            LearnedMappingKind.Link,
            alias,
            url,
            url,
            DurablePreferenceKinds);
        var persisted = _mappings.FindExact(LearnedMappingKind.Link, alias);
        if (!MappingRecordsMatch(entry, persisted))
            return Result(false, "The web preference could not be read back, so I did not mark it as remembered.");
        return Logged(
            "remember_link_alias",
            $"{entry.Alias} -> {entry.Target}",
            true,
            $"Remembered the link {entry.Alias}.",
            extras: new()
            {
                ["mapping"] = PublicMapping(persisted),
                ["persistence_verified"] = true,
            });
    }

    internal bool TryApplyDurablePreferenceFromTranscript(
        string text,
        out string toolName,
        out Dictionary<string, object> result)
    {
        toolName = "";
        result = null;
        var request = Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        if (!IsExplicitDurablePreferenceRequest(request))
            return false;

        var forget = Regex.Match(
            request,
            @"^(?:please\s+)?(?:forget|delete|remove|clear|stop\s+remembering)(?:\s+my|\s+the)?(?:\s+saved)?\s+(?:preference|correction|mapping)\s+(?:for|about)\s+(?<alias>.+?)[.!?]*$",
            RegexOptions.IgnoreCase);
        if (forget.Success)
        {
            var alias = CleanPreferenceAlias(forget.Groups["alias"].Value);
            var removed = _mappings.ForgetAcrossKinds(alias, DurablePreferenceKinds);
            result = Logged(
                "forget_learned_mapping",
                alias,
                removed > 0 && !DurablePreferenceKinds.Any(kind =>
                    _mappings.FindExact(kind, alias) != null),
                removed > 0
                    ? $"Forgot the saved preference for {alias}."
                    : $"No saved preference for {alias} was found.",
                extras: new() { ["removed_count"] = removed });
            toolName = "forget_learned_mapping";
            return true;
        }

        var explicitLink = Regex.Match(
            request,
            @"^(?:(?:please\s+)?(?:always|from\s+now\s+on|going\s+forward|remember(?:\s+that)?))\s+(?:open|use)\s+(?<url>https?://\S+|[\p{L}\p{N}.-]+\.[\p{L}]{2,}\S*)\s+(?:when\s+i\s+say|for)\s+(?<alias>.+?)[.!?]*$",
            RegexOptions.IgnoreCase);
        if (explicitLink.Success)
        {
            var alias = CleanPreferenceAlias(explicitLink.Groups["alias"].Value);
            var url = NormalizeUrl(explicitLink.Groups["url"].Value.TrimEnd('.', '!', '?'));
            result = RememberLinkAlias(alias, url);
            toolName = "remember_link_alias";
            return true;
        }

        if (TryParseWebPreference(request, out var webAlias))
        {
            if (!TryResolvePreferenceUrl(webAlias, out var webUrl))
            {
                result = Result(
                    false,
                    $"I need the exact website URL to remember a web preference for {webAlias}.",
                    needsClarification: true);
                toolName = "remember_link_alias";
                return true;
            }
            result = RememberLinkAlias(webAlias, webUrl);
            toolName = "remember_link_alias";
            return true;
        }

        if (TryParseNativePreference(request, out var nativeAlias))
        {
            result = RememberAppAlias(nativeAlias, nativeAlias);
            toolName = "remember_app_alias";
            return true;
        }

        var browser = Regex.Match(
            request,
            @"^(?:(?:please\s+)?(?:always|from\s+now\s+on|going\s+forward|remember(?:\s+that)?\s+i))\s+(?:use|open\s+links\s+in)\s+(?<browser>chrome|edge|brave|firefox|opera|vivaldi|arc)(?:\s+browser)?[.!?]*$",
            RegexOptions.IgnoreCase);
        if (browser.Success)
        {
            result = SetBrowserPreference(browser.Groups["browser"].Value);
            toolName = "set_browser_preference";
            return true;
        }

        return false;
    }

    internal bool TryResolveDurableOpenPreference(
        string text,
        out string toolName,
        out JsonElement arguments)
    {
        toolName = "";
        arguments = default;
        var request = Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        var match = Regex.Match(
            request,
            @"^(?:please\s+)?(?<verb>open|use)\s+(?<alias>.+?)[.!?]*$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        var alias = CleanPreferenceAlias(match.Groups["alias"].Value);
        if (string.IsNullOrWhiteSpace(alias)
            || Regex.IsMatch(
                alias,
                @"\b(?:app|application|website|web|browser|this\s+time|just\s+once|for\s+now)\b",
                RegexOptions.IgnoreCase))
        {
            // An explicit current target choice outranks a durable preference.
            return false;
        }

        // Learned mappings may intentionally use either the named object
        // ("portal") or the complete spoken trigger ("open portal"). Resolve
        // both exact normalized forms, in that order, so creation and lookup
        // share one durable command identity without introducing fuzzy action
        // execution or a second memory system.
        var spokenAlias = CleanPreferenceAlias(
            $"{match.Groups["verb"].Value} {match.Groups["alias"].Value}");
        var candidates = new[] { alias, spokenAlias }
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var link = candidates
            .Select(candidate => _mappings.FindExact(LearnedMappingKind.Link, candidate))
            .FirstOrDefault(entry => entry != null);
        if (link != null)
        {
            toolName = "open_app";
            arguments = JsonSerializer.SerializeToElement(new
            {
                app_name = link.Alias,
                target_kind = "auto",
            });
            return true;
        }

        var app = candidates
            .Select(candidate => _mappings.FindExact(LearnedMappingKind.App, candidate))
            .FirstOrDefault(entry => entry != null);
        if (app != null)
        {
            toolName = "open_app";
            arguments = JsonSerializer.SerializeToElement(new
            {
                // Resolve the verified learned candidate by its durable alias;
                // its stored launch target is not a human app-search query.
                app_name = app.Alias,
                target_kind = "native",
            });
            return true;
        }

        var file = candidates
            .Select(candidate => _mappings.FindExact(LearnedMappingKind.File, candidate))
            .FirstOrDefault(entry => entry != null);
        if (file != null)
        {
            toolName = "open_path";
            arguments = JsonSerializer.SerializeToElement(new { path = file.Target });
            return true;
        }

        var folder = candidates
            .Select(candidate => _mappings.FindExact(LearnedMappingKind.Folder, candidate))
            .FirstOrDefault(entry => entry != null);
        if (folder != null)
        {
            toolName = "open_folder";
            arguments = JsonSerializer.SerializeToElement(new { folder = folder.Target });
            return true;
        }

        return false;
    }

    internal static bool IsExplicitDurablePreferenceRequest(string text)
    {
        var request = (text ?? "").Trim();
        if (request.Length == 0
            || Regex.IsMatch(
                request,
                @"\b(?:this\s+time|just\s+(?:this\s+)?once|for\s+now|temporarily|only\s+today)\b",
                RegexOptions.IgnoreCase))
            return false;
        return Regex.IsMatch(
            request,
            @"\b(?:always|from\s+now\s+on|going\s+forward|remember\s+(?:that|to)|(?:forget|delete|remove|clear)\s+(?:my\s+|the\s+)?(?:saved\s+)?(?:preference|correction|mapping)|stop\s+remembering)\b",
            RegexOptions.IgnoreCase);
    }

    private bool TryParseWebPreference(string request, out string alias)
    {
        alias = "";
        var patterns = new[]
        {
            @"^(?:(?:please\s+)?(?:always|from\s+now\s+on|going\s+forward))\s+(?:open|use|launch)\s+(?<alias>.+?)\s+(?:on|in|using)\s+(?:the\s+)?(?:web|browser|web\s+version)(?:\s+instead)?(?:\s+from\s+now\s+on)?[.!?]*$",
            @"^remember\s+that\s+i\s+(?:use|open)\s+(?<alias>.+?)\s+(?:on|in|using)\s+(?:the\s+)?(?:web|browser|web\s+version)[.!?]*$",
            @"^(?:(?:please\s+)?(?:always|from\s+now\s+on|going\s+forward))\s+(?:use|open)\s+(?:the\s+)?(?:web|browser|web\s+version)(?:\s+instead)?\s+(?:for|of)\s+(?<alias>.+?)(?:\s+from\s+now\s+on)?[.!?]*$",
            @"^(?:change|update)\s+(?:my\s+|the\s+)?(?:saved\s+)?preference\s+for\s+(?<alias>.+?)\s+to\s+(?:the\s+)?(?:web|browser|web\s+version)[.!?]*$",
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(request, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;
            alias = CleanPreferenceAlias(match.Groups["alias"].Value);
            return !string.IsNullOrWhiteSpace(alias);
        }

        if (!Regex.IsMatch(
                request,
                @"^(?:(?:please\s+)?(?:always|from\s+now\s+on|going\s+forward))\s+(?:use|open)\s+(?:it\s+)?(?:(?:on|in)\s+)?(?:the\s+)?(?:web|browser|web\s+version)(?:\s+instead)?(?:\s+from\s+now\s+on)?[.!?]*$",
                RegexOptions.IgnoreCase))
            return false;
        alias = RecentOpenTarget();
        return !string.IsNullOrWhiteSpace(alias);
    }

    private bool TryParseNativePreference(string request, out string alias)
    {
        alias = "";
        var patterns = new[]
        {
            @"^(?:(?:please\s+)?(?:always|from\s+now\s+on|going\s+forward))\s+(?:open|use|launch)\s+(?<alias>.+?)\s+as\s+(?:an?\s+)?(?:native\s+)?(?:app|application)(?:\s+from\s+now\s+on)?[.!?]*$",
            @"^(?:(?:please\s+)?(?:always|from\s+now\s+on|going\s+forward))\s+(?:use|open)\s+(?:the\s+)?(?:native\s+)?(?:app|application)\s+(?:for|of)\s+(?<alias>.+?)(?:\s+from\s+now\s+on)?[.!?]*$",
            @"^(?:change|update)\s+(?:my\s+|the\s+)?(?:saved\s+)?preference\s+for\s+(?<alias>.+?)\s+to\s+(?:the\s+)?(?:native\s+)?(?:app|application)[.!?]*$",
        };
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(request, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;
            alias = CleanPreferenceAlias(match.Groups["alias"].Value);
            return !string.IsNullOrWhiteSpace(alias);
        }
        return false;
    }

    private static string CleanPreferenceAlias(string value) =>
        Regex.Replace(
            (value ?? "").Trim().Trim(' ', '\'', '"', '.', '!', '?'),
            @"^(?:the\s+)",
            "",
            RegexOptions.IgnoreCase).Trim();

    private static bool TryResolvePreferenceUrl(string alias, out string url)
    {
        url = "";
        var clean = CleanPreferenceAlias(alias);
        if (Uri.TryCreate(clean, UriKind.Absolute, out var absolute)
            && (string.Equals(absolute.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            url = absolute.AbsoluteUri;
            return true;
        }
        var normalized = NormalizeWords(clean);
        if (KnownWebApps.TryGetValue(normalized, out url))
            return true;
        // Discord is intentionally native-first for an ambiguous "open"
        // request, but its official web client is a valid explicit durable
        // preference target.
        if (normalized == "discord")
        {
            url = "https://discord.com/app";
            return true;
        }
        if (Regex.IsMatch(clean, @"^[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)+(?:/.*)?$"))
        {
            url = NormalizeUrl(clean);
            return true;
        }
        return false;
    }

    private Dictionary<string, object> RememberWorkflowAlias(string alias, string workflowName)
    {
        var workflow = FindWorkflow(workflowName);
        if (workflow == null)
            return Result(false, $"Workflow {workflowName} was not found.", supported: false);
        var entry = _mappings.Remember(
            LearnedMappingKind.Workflow,
            alias,
            workflow.Name,
            workflow.Name);
        return Logged(
            "remember_workflow_alias",
            $"{entry.Alias} -> {entry.Target}",
            true,
            $"I will use workflow {entry.Target} when you say {entry.Alias}.",
            extras: new() { ["mapping"] = PublicMapping(entry) });
    }

    private Dictionary<string, object> ListLearnedMappings()
    {
        var entries = _mappings.Snapshot();
        return Result(
            true,
            entries.Count == 0 ? "No learned mappings are saved." : $"Loaded {entries.Count} learned mappings.",
            extras: new() { ["mappings"] = entries.Select(PublicMapping).ToArray() });
    }

    private Dictionary<string, object> ForgetLearnedMapping(string idOrAlias, string kind)
    {
        LearnedMappingKind? parsedKind = Enum.TryParse<LearnedMappingKind>(kind, true, out var value)
            ? value
            : null;
        var removed = _mappings.Forget(idOrAlias, parsedKind);
        return Logged(
            "forget_learned_mapping",
            idOrAlias,
            removed,
            removed ? "Learned mapping forgotten." : "No matching learned mapping was found.");
    }

    private Dictionary<string, object> SetBrowserPreference(string browser)
    {
        var normalized = BrowserLauncherService.NormalizeBrowser(browser);
        if (!string.IsNullOrWhiteSpace(normalized) && _browsers.FindBrowserExecutable(normalized) == null)
            return Result(false, $"{browser} is not installed, so the browser preference was not changed.", supported: false);
        _settings.SavePreferredBrowser(normalized);
        var persisted = _settings.ReadPersistedPreferredBrowser();
        if (!string.Equals(normalized, persisted, StringComparison.OrdinalIgnoreCase))
            return Result(false, "The browser preference could not be read back, so I did not confirm it.");
        return Logged(
            "set_browser_preference",
            normalized,
            true,
            string.IsNullOrWhiteSpace(normalized)
                ? "Future links will use the Windows default browser."
                : $"Future links will use {browser} unless you name another browser.",
            extras: new() { ["persistence_verified"] = true });
    }

    private Dictionary<string, object> OpenGmailDraft(string recipient, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(recipient))
            return Result(false, "Who should I address the email to?", needsClarification: true);
        if (string.IsNullOrWhiteSpace(body))
            return Result(false, "What should the email say?", needsClarification: true);

        var query = new List<string> { "view=cm", "fs=1" };
        query.Add($"to={Uri.EscapeDataString(recipient.Trim())}");
        if (!string.IsNullOrWhiteSpace(subject))
            query.Add($"su={Uri.EscapeDataString(subject.Trim())}");
        query.Add($"body={Uri.EscapeDataString(body.Trim())}");
        ClipboardHelper.SetText(body.Trim());

        var result = OpenUrl("https://mail.google.com/mail/?" + string.Join("&", query));
        result["message"] = "Gmail draft opened. Review it before sending.";
        result["manual_send_required"] = true;
        return result;
    }

    private Dictionary<string, object> PrepareWhatsAppMessage(string message, string contactName, string phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Result(false, "What should the WhatsApp message say?", needsClarification: true);

        ClipboardHelper.SetText(message.Trim());
        if (!string.IsNullOrWhiteSpace(phoneNumber))
        {
            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
            if (digits.Length < 7)
                return Result(false, "That phone number does not look usable.", needsClarification: true);

            var encoded = Uri.EscapeDataString(message.Trim());
            if (!TryStart($"whatsapp://send?phone={digits}&text={encoded}"))
                OpenUrl($"https://wa.me/{digits}?text={encoded}");
            return Logged(
                "prepare_whatsapp_message",
                digits,
                true,
                "WhatsApp message prepared. Review it before sending.",
                extras: new() { ["manual_send_required"] = true });
        }

        if (!TryStart("whatsapp://"))
            OpenUrl("https://web.whatsapp.com/");
        var contact = string.IsNullOrWhiteSpace(contactName) ? "that contact" : contactName.Trim();
        return Logged(
            "prepare_whatsapp_message",
            contact,
            false,
            $"I cannot reliably select {contact} by name yet. I copied the message and opened WhatsApp.",
            supported: false,
            extras: new() { ["contact_lookup_supported"] = false });
    }

    private Dictionary<string, object> CopyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result(false, "What should I copy?", needsClarification: true);
        ClipboardHelper.SetText(text);
        return Logged("copy_text", Limit(text, 120), true, "Copied to clipboard.");
    }

    private Dictionary<string, object> CreateWorkflow(JsonElement args)
    {
        var workflow = _settings.SaveWorkflow(
            GetString(args, "name"),
            [GetString(args, "name")],
            ParseWorkflowActions(args));
        return Logged("create_workflow", workflow.Name, true, $"Saved workflow {workflow.Name}.");
    }

    private Dictionary<string, object> RememberRoutine(JsonElement args)
    {
        var trigger = CleanRoutineTrigger(GetString(args, "trigger") ?? GetString(args, "name"));
        if (string.IsNullOrWhiteSpace(trigger))
            return Result(false, "What phrase should trigger the routine?", needsClarification: true);

        return SaveVerifiedRoutine(trigger, ParseWorkflowActions(args));
    }

    private Dictionary<string, object> EditWorkflow(JsonElement args) =>
        ApplyWorkflowEdit(
            GetString(args, "name"),
            GetString(args, "operation"),
            GetString(args, "target"),
            GetString(args, "replacement") ?? GetString(args, "new_value"));

    internal bool TryEditWorkflowFromTranscript(
        string text,
        out Dictionary<string, object> result)
    {
        result = null;
        var request = Regex.Replace((text ?? "").Trim(), @"\s+", " ")
            .TrimEnd('.', '!', '?');
        if (request.Length == 0)
            return false;

        var deleteWorkflow = Regex.Match(
            request,
            @"^(?:delete|forget)\s+(?:my\s+|the\s+)?(?:(?:workflow|routine)\s+(?<prefix>.+)|(?<suffix>.+?)\s+(?:workflow|routine))$",
            RegexOptions.IgnoreCase);
        if (deleteWorkflow.Success)
        {
            var reference = deleteWorkflow.Groups["prefix"].Success
                ? deleteWorkflow.Groups["prefix"].Value
                : deleteWorkflow.Groups["suffix"].Value;
            result = DeleteWorkflow(ResolveWorkflowReference(reference));
            return true;
        }

        var aliasEdit = Regex.Match(
            request,
            @"^(?<operation>add|remove|delete)\s+(?:the\s+)?alias\s+(?<target>.+?)\s+(?:to|from)\s+(?<workflow>.+)$",
            RegexOptions.IgnoreCase);
        if (aliasEdit.Success)
        {
            result = ApplyWorkflowEdit(
                ResolveWorkflowReference(aliasEdit.Groups["workflow"].Value),
                aliasEdit.Groups["operation"].Value.Equals("add", StringComparison.OrdinalIgnoreCase)
                    ? "add_alias"
                    : "remove_alias",
                aliasEdit.Groups["target"].Value,
                "");
            return true;
        }

        var rename = Regex.Match(
            request,
            @"^rename\s+(?<workflow>.+?)\s+to\s+(?<target>.+)$",
            RegexOptions.IgnoreCase);
        if (rename.Success)
        {
            result = ApplyWorkflowEdit(
                ResolveWorkflowReference(rename.Groups["workflow"].Value),
                "rename",
                rename.Groups["target"].Value,
                "");
            return true;
        }

        var replace = Regex.Match(
            request,
            @"^(?:replace|change)\s+(?<target>.+?)\s+(?:with|to)\s+(?<replacement>.+?)(?:\s+(?:in|from)\s+(?<workflow>.+))?$",
            RegexOptions.IgnoreCase);
        if (replace.Success)
        {
            var rawWorkflow = replace.Groups["workflow"].Value;
            var workflow = ResolveWorkflowReference(rawWorkflow);
            if (!string.IsNullOrWhiteSpace(workflow))
            {
                if (FindSavedWorkflow(workflow) == null
                    && !IsExplicitWorkflowReferenceLanguage(rawWorkflow))
                    return false;
                result = ApplyWorkflowEdit(
                    workflow,
                    "replace_action",
                    replace.Groups["target"].Value,
                    replace.Groups["replacement"].Value);
                return true;
            }
        }

        var actionEdit = Regex.Match(
            request,
            @"^(?<operation>add|remove|delete)\s+(?<target>.+?)(?:\s+(?:to|from)\s+(?<workflow>.+))?$",
            RegexOptions.IgnoreCase);
        if (!actionEdit.Success)
            return false;
        var rawWorkflowReference = actionEdit.Groups["workflow"].Value;
        var workflowReference = ResolveWorkflowReference(rawWorkflowReference);
        if (string.IsNullOrWhiteSpace(workflowReference))
            return false;
        if (FindSavedWorkflow(workflowReference) == null
            && !IsExplicitWorkflowReferenceLanguage(rawWorkflowReference))
            return false;
        result = ApplyWorkflowEdit(
            workflowReference,
            actionEdit.Groups["operation"].Value.Equals("add", StringComparison.OrdinalIgnoreCase)
                ? "add_action"
                : "remove_action",
            actionEdit.Groups["target"].Value,
            "");
        return true;
    }

    private static bool IsExplicitWorkflowReferenceLanguage(string value) =>
        Regex.IsMatch(
            (value ?? "").Trim(),
            @"\b(?:workflow|routine|setup)\b",
            RegexOptions.IgnoreCase);

    private Dictionary<string, object> ApplyWorkflowEdit(
        string workflowReference,
        string operation,
        string target,
        string replacement)
    {
        var workflow = FindSavedWorkflow(workflowReference);
        if (workflow == null)
            return Result(false, $"Workflow {workflowReference} was not found.", supported: false);

        var previousIdentities = (workflow.Aliases ?? [])
            .Prepend(workflow.Name)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var newName = workflow.Name;
        var aliases = (workflow.Aliases ?? [])
            .Prepend(workflow.Name)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var actions = OrderedWorkflowActions(workflow)
            .Select(action => new JarvisWorkflowActionSettings
            {
                Kind = action.Kind,
                Target = action.Target,
            })
            .ToList();
        var normalizedOperation = NormalizeWords(
            (operation ?? "").Replace('_', ' ').Replace('-', ' '));
        var cleanTarget = (target ?? "").Trim().Trim(' ', '\'', '"', '.', '!', '?');

        switch (normalizedOperation)
        {
            case "add action":
            case "add":
            {
                var action = ResolveExplicitOpenAction(cleanTarget);
                if (string.IsNullOrWhiteSpace(action.Target))
                    return Result(false, "What should I add to the workflow?", needsClarification: true);
                if (!actions.Any(existing => SameWorkflowAction(existing, action)))
                    actions.Add(action);
                break;
            }
            case "remove action":
            case "remove":
            case "delete action":
            {
                if (!TryFindWorkflowAction(actions, cleanTarget, out var index, out var reason))
                    return Result(false, reason, needsClarification: true);
                actions.RemoveAt(index);
                if (actions.Count == 0)
                    return Result(false, "A workflow must keep at least one action. Delete the workflow instead.", needsClarification: true);
                break;
            }
            case "replace action":
            case "replace":
            case "change":
            {
                if (!TryFindWorkflowAction(actions, cleanTarget, out var index, out var reason))
                    return Result(false, reason, needsClarification: true);
                var action = ResolveExplicitOpenAction(replacement);
                if (string.IsNullOrWhiteSpace(action.Target))
                    return Result(false, "What should replace that workflow action?", needsClarification: true);
                actions[index] = action;
                break;
            }
            case "rename":
                newName = CleanRoutineTrigger(cleanTarget);
                if (string.IsNullOrWhiteSpace(newName))
                    return Result(false, "What should the workflow be called?", needsClarification: true);
                aliases.RemoveAll(alias => string.Equals(
                    NormalizePhraseText(alias),
                    NormalizePhraseText(workflow.Name),
                    StringComparison.Ordinal));
                aliases.Insert(0, newName);
                break;
            case "add alias":
            {
                var alias = CleanRoutineTrigger(cleanTarget);
                if (string.IsNullOrWhiteSpace(alias))
                    return Result(false, "What alias should I add?", needsClarification: true);
                if (!aliases.Any(existing => string.Equals(
                    NormalizePhraseText(existing),
                    NormalizePhraseText(alias),
                    StringComparison.Ordinal)))
                    aliases.Add(alias);
                break;
            }
            case "remove alias":
            case "delete alias":
            {
                var normalized = NormalizePhraseText(cleanTarget);
                if (string.Equals(normalized, NormalizePhraseText(workflow.Name), StringComparison.Ordinal))
                    return Result(false, "Rename the workflow instead of removing its canonical trigger.", needsClarification: true);
                var removed = aliases.RemoveAll(alias => string.Equals(
                    NormalizePhraseText(alias),
                    normalized,
                    StringComparison.Ordinal));
                if (removed == 0)
                    return Result(false, $"Alias {cleanTarget} was not found.", needsClarification: true);
                break;
            }
            default:
                return Result(false, "Supported workflow edits are add, remove, replace, rename, and alias changes.", supported: false);
        }

        JarvisWorkflowSettings updated;
        try
        {
            updated = _settings.UpdateWorkflow(
                workflow.Name,
                newName,
                aliases,
                actions);
        }
        catch (InvalidOperationException ex)
        {
            return Result(false, ex.Message, needsClarification: true);
        }

        var persisted = _settings.ReadPersistedWorkflow(updated.Name);
        if (!WorkflowRecordsMatch(updated, persisted))
        {
            return Logged(
                "edit_workflow",
                workflow.Name,
                false,
                "The updated workflow could not be read back, so I did not confirm the change.");
        }

        try
        {
            foreach (var identity in previousIdentities)
                _mappings.ForgetWorkflowReferences(identity);
            foreach (var alias in persisted.Aliases.Prepend(persisted.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _mappings.Remember(
                    LearnedMappingKind.Workflow,
                    alias,
                    persisted.Name,
                    persisted.Name);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Workflow alias index refresh failed after verified authoritative edit");
        }

        RememberWorkflowContext(persisted.Name);
        return Logged(
            "edit_workflow",
            persisted.Name,
            true,
            $"Updated workflow {persisted.Name}.",
            extras: new()
            {
                ["workflow"] = PublicWorkflow(persisted),
                ["persistence_verified"] = true,
            });
    }

    private string ResolveWorkflowReference(string value)
    {
        var reference = Regex.Replace((value ?? "").Trim(), @"\s+", " ")
            .Trim(' ', '\'', '"', '.', '!', '?');
        reference = Regex.Replace(reference, @"^(?:my|the)\s+", "", RegexOptions.IgnoreCase);
        reference = Regex.Replace(reference, @"\s+(?:workflow|routine)$", "", RegexOptions.IgnoreCase);
        if (string.IsNullOrWhiteSpace(reference)
            || NormalizeWords(reference) is "that" or "this" or "it" or "workflow" or "routine" or "that workflow" or "this workflow")
            return RecentWorkflowName();
        return FindSavedWorkflow(reference)?.Name ?? reference;
    }

    private static bool TryFindWorkflowAction(
        IReadOnlyList<JarvisWorkflowActionSettings> actions,
        string query,
        out int index,
        out string reason)
    {
        index = -1;
        reason = "";
        var ranked = actions
            .Select((action, actionIndex) => new
            {
                Index = actionIndex,
                Score = WorkflowActionMatchScore(action, query),
            })
            .Where(item => item.Score >= 0.62)
            .OrderByDescending(item => item.Score)
            .ToArray();
        if (ranked.Length == 0)
        {
            reason = $"I could not find {query} in that workflow.";
            return false;
        }
        if (ranked.Length > 1
            && ranked[0].Score < 0.92
            && ranked[0].Score - ranked[1].Score < 0.08)
        {
            reason = $"More than one workflow action resembles {query}. Please name it more precisely.";
            return false;
        }
        index = ranked[0].Index;
        return true;
    }

    private static double WorkflowActionMatchScore(
        JarvisWorkflowActionSettings action,
        string query)
    {
        var terms = new List<string> { action?.Target ?? "" };
        if (Uri.TryCreate(action?.Target, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? uri.Host[4..]
                : uri.Host;
            terms.Add(host);
            terms.Add(host.Split('.')[0]);
            terms.AddRange(KnownWebApps
                .Where(pair => Uri.TryCreate(pair.Value, UriKind.Absolute, out var known)
                    && string.Equals(known.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key));
        }
        return terms.Max(term => FuzzyMatcher.Score(query, term));
    }

    private static bool SameWorkflowAction(
        JarvisWorkflowActionSettings left,
        JarvisWorkflowActionSettings right) =>
        string.Equals(left?.Kind, right?.Kind, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left?.Target, right?.Target, StringComparison.OrdinalIgnoreCase);

    internal bool TryRememberWorkflowFromTranscript(
        string text,
        bool completeTurn,
        out Dictionary<string, object> result)
    {
        result = null;
        if (!TryParseExplicitWorkflow(text, completeTurn, out var trigger, out var actions))
            return false;
        result = SaveVerifiedRoutine(trigger, actions);
        return true;
    }

    private Dictionary<string, object> SaveVerifiedRoutine(
        string trigger,
        IReadOnlyList<JarvisWorkflowActionSettings> actions)
    {
        trigger = CleanRoutineTrigger(trigger);
        if (string.IsNullOrWhiteSpace(trigger))
            return Result(false, "What phrase should trigger the routine?", needsClarification: true);

        JarvisWorkflowSettings workflow;
        try
        {
            workflow = _settings.SaveWorkflow(
                trigger,
                [trigger],
                actions);
        }
        catch (InvalidOperationException ex)
        {
            return Result(false, ex.Message, needsClarification: true);
        }

        // The complete settings record is authoritative. The learned mapping
        // remains a backward-compatible lookup index, not a second copy of the
        // routine. A failure to refresh that index cannot discard a verified
        // durable workflow object.
        LearnedMappingEntry mapping = null;
        try
        {
            mapping = _mappings.Remember(
                LearnedMappingKind.Workflow,
                trigger,
                workflow.Name,
                workflow.Name);
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Workflow alias index refresh failed; authoritative workflow remains saved");
        }

        var persisted = _settings.ReadPersistedWorkflow(trigger);
        if (!WorkflowRecordsMatch(workflow, persisted))
        {
            return Logged(
                "remember_routine",
                trigger,
                false,
                "The complete workflow could not be read back from local storage, so I did not mark it as remembered.");
        }
        RememberWorkflowContext(persisted.Name);
        return Logged(
            "remember_routine",
            trigger,
            true,
            $"Saved the routine. Say {trigger} to run it.",
            extras: new()
            {
                ["workflow"] = PublicWorkflow(persisted),
                ["mapping"] = mapping == null ? null : PublicMapping(mapping),
                ["persistence_verified"] = true,
            });
    }

    private bool TryParseExplicitWorkflow(
        string text,
        bool completeTurn,
        out string trigger,
        out IReadOnlyList<JarvisWorkflowActionSettings> actions)
    {
        trigger = "";
        actions = Array.Empty<JarvisWorkflowActionSettings>();
        var request = Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        if (request.Length == 0)
            return false;

        // Live input transcription arrives incrementally. Do not persist an
        // incomplete list during a speaking pause; a terminal remember/save
        // phrase proves the spoken mapping is complete. Local text submissions
        // are already complete turns and can be parsed immediately.
        if (!completeTurn && !Regex.IsMatch(
                request,
                @"\b(?:remember|save)\s+(?:this|that)(?:\s+(?:workflow|routine))?\s*[.!?]*$",
                RegexOptions.IgnoreCase))
            return false;

        var match = Regex.Match(
            request,
            @"(?:^|\b)(?:whenever|when)\s+i\s+say\s+(?<trigger>.+?)[,;:]\s*(?<actions>.+)$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(
                request,
                @"(?:^|\b)make\s+(?<trigger>.+?)\s+mean\s+(?<actions>.+)$",
                RegexOptions.IgnoreCase);
        }
        if (!match.Success)
            return false;

        trigger = CleanRoutineTrigger(match.Groups["trigger"].Value.Trim(' ', '\'', '"'));
        var actionText = Regex.Replace(
            match.Groups["actions"].Value,
            @"(?:[.!?]\s*)?(?:remember|save)\s+(?:this|that)(?:\s+(?:workflow|routine))?\s*[.!?]*$",
            "",
            RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(trigger)
            || !Regex.IsMatch(actionText, @"^(?:please\s+)?(?:open|launch|start|visit|go\s+to)\b", RegexOptions.IgnoreCase))
            return false;

        var targets = SplitExplicitOpenTargets(actionText);
        if (targets.Count == 0)
            return false;
        actions = targets.Select(ResolveExplicitOpenAction).ToArray();
        return actions.All(action => !string.IsNullOrWhiteSpace(action.Target));
    }

    private static IReadOnlyList<string> SplitExplicitOpenTargets(string actionText)
    {
        var list = Regex.Replace(
            actionText,
            @"^(?:please\s+)?(?:open|launch|start|visit|go\s+to)\s+",
            "",
            RegexOptions.IgnoreCase).Trim();
        list = Regex.Replace(list, @"\s+(?:and\s+then|then)\s+", ",", RegexOptions.IgnoreCase);
        var parts = list.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
            parts = Regex.Split(list, @"\s+and\s+", RegexOptions.IgnoreCase);
        return parts
            .Select(part => Regex.Replace(part, @"^and\s+", "", RegexOptions.IgnoreCase))
            .Select(part => Regex.Replace(
                part,
                @"^(?:open|launch|start|visit|go\s+to)\s+",
                "",
                RegexOptions.IgnoreCase))
            .Select(part => part.Trim().TrimEnd('.', '!', '?'))
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Take(24)
            .ToArray();
    }

    private JarvisWorkflowActionSettings ResolveExplicitOpenAction(string rawTarget)
    {
        var explicitNative = Regex.IsMatch(rawTarget, @"\s+(?:native\s+)?(?:app|application)$", RegexOptions.IgnoreCase);
        var explicitWeb = Regex.IsMatch(rawTarget, @"\s+(?:website|site|web\s+page)$", RegexOptions.IgnoreCase);
        var target = Regex.Replace(
            rawTarget,
            @"\s+(?:(?:native\s+)?(?:app|application)|website|site|web\s+page)$",
            "",
            RegexOptions.IgnoreCase).Trim();
        if (explicitNative)
            return new JarvisWorkflowActionSettings { Kind = "app", Target = target };
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return new JarvisWorkflowActionSettings { Kind = "url", Target = uri.AbsoluteUri };
        }

        var normalized = NormalizeWords(target);
        if (KnownWebApps.TryGetValue(normalized, out var knownUrl))
            return new JarvisWorkflowActionSettings { Kind = "url", Target = knownUrl };
        if (explicitWeb || Regex.IsMatch(target, @"^[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)+(?:/.*)?$"))
            return new JarvisWorkflowActionSettings { Kind = "url", Target = NormalizeUrl(target) };
        if (AppResolverService.IsKnownNativeAppName(target))
            return new JarvisWorkflowActionSettings { Kind = "app", Target = target };

        var app = _apps.Resolve(target);
        return new JarvisWorkflowActionSettings
        {
            Kind = app.Status == AppResolutionStatus.Found ? "app" : "auto",
            Target = target,
        };
    }

    private Dictionary<string, object> ListWorkflows() =>
        Result(true, "Workflows loaded.", extras: new()
        {
            ["workflows"] = (_settings.Current.JarvisWorkflows ?? new()).Select(w => new
            {
                w.Name,
                w.Version,
                w.Aliases,
                w.Actions,
                w.Apps,
                w.Urls,
                w.Folder,
            }).ToArray(),
            ["built_in_examples"] = new[] { "coding mode", "study mode", "hackathon mode" },
        });

    private async Task<Dictionary<string, object>> RunWorkflowAsync(string name, CancellationToken token)
    {
        var workflow = FindWorkflow(name);
        if (workflow == null)
            return Result(false, $"Workflow {name} was not found. Create it first or use a built-in example.", supported: false);
        RememberWorkflowContext(workflow.Name);

        var failures = new List<string>();
        var receipts = new List<object>();
        foreach (var action in OrderedWorkflowActions(workflow))
        {
            token.ThrowIfCancellationRequested();
            Dictionary<string, object> result;
            switch (action.Kind)
            {
                case "app":
                    result = OpenApp(action.Target, "native");
                    break;
                case "auto":
                    result = OpenApp(action.Target);
                    break;
                case "url":
                    result = OpenUrl(NormalizeUrl(action.Target));
                    break;
                case "folder":
                    result = await OpenFolderAsync(action.Target, "", token);
                    break;
                default:
                    result = Result(false, $"Unsupported saved action type {action.Kind}.", supported: false);
                    break;
            }
            var ok = Succeeded(result);
            var message = result.TryGetValue("message", out var detail)
                ? detail?.ToString() ?? ""
                : "";
            receipts.Add(new { action.Kind, action.Target, ok, message });
            if (!Succeeded(result))
                failures.Add(string.IsNullOrWhiteSpace(message) ? $"Could not run {action.Target}." : message);
            await Task.Delay(180, token);
        }

        return Logged(
            "run_workflow",
            workflow.Name,
            failures.Count == 0,
            failures.Count == 0
                ? $"Started {workflow.Name}."
                : $"Started {workflow.Name} with {failures.Count} action failure(s).",
            extras: new()
            {
                ["failures"] = failures.ToArray(),
                ["action_results"] = receipts.ToArray(),
                ["workflow_version"] = workflow.Version,
            });
    }

    private Dictionary<string, object> DeleteWorkflow(string name)
    {
        var learned = _mappings.FindExact(LearnedMappingKind.Workflow, name);
        var saved = FindSavedWorkflow(learned?.Target ?? name);
        var canonicalName = saved?.Name ?? learned?.Target ?? name;
        var removed = _settings.DeleteWorkflow(canonicalName);
        if (!string.Equals(
                FuzzyMatcher.Normalize(canonicalName),
                FuzzyMatcher.Normalize(name),
                StringComparison.Ordinal))
        {
            _settings.DeleteWorkflow(name);
        }
        _mappings.ForgetWorkflowReferences(canonicalName);
        _mappings.ForgetWorkflowReferences(name);
        foreach (var alias in saved?.Aliases ?? [])
            _mappings.ForgetWorkflowReferences(alias);
        lock (_conversationStateLock)
        {
            if (string.Equals(
                    NormalizePhraseText(_latestWorkflowName),
                    NormalizePhraseText(canonicalName),
                    StringComparison.Ordinal))
            {
                _latestWorkflowName = "";
                _latestWorkflowAt = default;
            }
        }
        return Logged("delete_workflow", name, removed, removed ? "Workflow deleted." : "Workflow not found.");
    }

    internal bool TryResolveSavedWorkflowTrigger(string utterance, out string canonicalName)
    {
        canonicalName = "";
        var normalized = NormalizePhraseText(utterance);
        if (string.IsNullOrWhiteSpace(normalized) || _settings.IsWorkflowDeleted(normalized))
            return false;
        var workflow = (_settings.Current.JarvisWorkflows ?? []).FirstOrDefault(candidate =>
            (candidate.Aliases ?? []).Prepend(candidate.Name).Any(alias =>
                string.Equals(NormalizePhraseText(alias), normalized, StringComparison.Ordinal)));
        if (workflow == null || OrderedWorkflowActions(workflow).Count == 0)
            return false;
        canonicalName = workflow.Name;
        RememberWorkflowContext(workflow.Name);
        return true;
    }

    internal bool IsDeletedWorkflowTrigger(string utterance) =>
        _settings.IsWorkflowDeleted(NormalizePhraseText(utterance));

    internal static bool IsExplicitWorkflowPersistenceRequest(string text)
    {
        var normalized = NormalizePhraseText(text);
        return Regex.IsMatch(normalized, @"\b(?:whenever|when) i say\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(normalized, @"\b(?:remember|save) (?:this|that)(?: as)?(?: workflow| routine)?\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(normalized, @"\bsave (?:this )?(?:workflow|routine)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(normalized, @"\bmake .+ mean .+", RegexOptions.IgnoreCase);
    }

    internal static string BuildSavedWorkflowPromptDigest(IEnumerable<JarvisWorkflowSettings> workflows)
    {
        var triggers = (workflows ?? [])
            .Where(workflow => OrderedWorkflowActions(workflow).Count > 0)
            .SelectMany(workflow => (workflow.Aliases ?? []).Prepend(workflow.Name))
            .Select(NormalizePhraseText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .ToArray();
        return triggers.Length == 0
            ? "None."
            : string.Join(" | ", triggers.Select(trigger => $"[{Limit(trigger, 48)}]"));
    }

    private Dictionary<string, object> RequestSensitiveAction(string action, string target)
    {
        var normalized = NormalizeWords(action);
        var allowed = new[]
        {
            "close active app", "close app", "shutdown", "restart", "sleep", "lock screen", "disable wifi",
        };
        if (!allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            return Result(false, "That sensitive action is not supported.", supported: false);
        if (normalized == "close app" && string.IsNullOrWhiteSpace(target))
            return Result(false, "Which app should I close?", needsClarification: true);

        if (_settings.Current.JarvisPermissionMode == JarvisPermissionMode.AutoExecute
            && normalized is "close active app" or "close app")
        {
            var result = WindowAction("close", normalized == "close app" ? target : "");
            result["auto_executed"] = true;
            return result;
        }

        var description = normalized switch
        {
            "close active app" => "Close the currently active app",
            "close app" => $"Close {target}",
            "shutdown" => "Shut down this computer",
            "restart" => "Restart this computer",
            "sleep" => "Put this computer to sleep",
            "lock screen" => "Lock this computer",
            "disable wifi" => "Disable Wi-Fi and disconnect Jarvis",
            _ => normalized,
        };

        lock (_pendingLock)
        {
            _pending = new PendingJarvisAction
            {
                IsSensitive = true,
                Action = normalized,
                Target = target ?? "",
                Description = description,
            };
        }
        Models.KeyboardWtfState.SetUi(Models.VoiceUiPhase.Thinking, "Confirmation required", description);
        _history.Add("confirmation_requested", description, true, true);
        return Result(true, $"{description}. Ask the user to say confirm or cancel.", confirmationRequired: true,
            extras: new() { ["description"] = description });
    }

    private async Task<Dictionary<string, object>> ConfirmPendingActionAsync(CancellationToken token)
    {
        PendingJarvisAction pending;
        lock (_pendingLock)
            pending = _pending;
        if (pending == null)
            return Result(false, "There is no pending action.", supported: false);
        if (pending.ConfirmedAt == null
            || pending.ConfirmedAt < pending.RequestedAt
            || DateTimeOffset.UtcNow - pending.ConfirmedAt > TimeSpan.FromSeconds(30))
        {
            return Result(false, "The user has not confirmed this action yet. Ask them to say confirm or cancel.",
                confirmationRequired: true);
        }

        lock (_pendingLock)
            _pending = null;
        if (pending.IsSensitive)
            return ExecuteSensitiveAction(pending);

        _executingApprovedAction = true;
        try
        {
            return await ExecuteCoreAsync(pending.ToolName, pending.Args, token);
        }
        finally
        {
            _executingApprovedAction = false;
        }
    }

    private Dictionary<string, object> CancelPendingAction()
    {
        lock (_pendingLock)
            _pending = null;
        return Result(true, "Pending action cancelled.");
    }

    private Dictionary<string, object> ExecuteSensitiveAction(PendingJarvisAction pending)
    {
        switch (pending.Action)
        {
            case "close active app":
            {
                var handle = GetForegroundWindow();
                if (handle == IntPtr.Zero)
                    return Logged("close_app", "active app", false, "No active app window was found.");
                PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
                return Logged("close_app", GetWindowTitle(handle), true, "Closed the active app.");
            }
            case "close app":
            {
                var handle = FindWindowByName(pending.Target);
                if (handle == IntPtr.Zero)
                    return Logged("close_app", pending.Target, false, "That app window was not found.");
                PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
                return Logged("close_app", pending.Target, true, $"Closed {pending.Target}.");
            }
            case "lock screen":
                LockWorkStation();
                return Logged("lock_screen", "", true, "Computer locked.");
            case "shutdown":
                Process.Start(new ProcessStartInfo { FileName = "shutdown.exe", Arguments = "/s /t 0", UseShellExecute = false });
                return Logged("shutdown", "", true, "Shutting down.");
            case "restart":
                Process.Start(new ProcessStartInfo { FileName = "shutdown.exe", Arguments = "/r /t 0", UseShellExecute = false });
                return Logged("restart", "", true, "Restarting.");
            case "sleep":
                Process.Start(new ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = "powrprof.dll,SetSuspendState 0,1,0",
                    UseShellExecute = false,
                });
                return Logged("sleep", "", true, "Going to sleep.");
            case "disable wifi":
                Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh.exe",
                    Arguments = "interface set interface name=\"Wi-Fi\" admin=disabled",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return Logged("disable_wifi", "", true, "Wi-Fi disable requested.");
            default:
                return Result(false, "Sensitive action is no longer supported.", supported: false);
        }
    }

    private Dictionary<string, object> OpenUrl(string url, string browser = null)
    {
        if (url?.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!string.IsNullOrWhiteSpace(browser))
                return Logged("open_url", url, false, "Windows settings pages cannot be targeted to a web browser.", supported: false);
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return Logged("open_settings_page", url, true, "Opened Windows settings.");
        }
        var learned = _mappings.FindExact(LearnedMappingKind.Link, url);
        if (learned != null)
            url = learned.Target;
        if (!_browsers.TryOpen(url, browser, out var message, out var browserUsed))
        {
            return Logged(
                "open_url",
                $"{url} browser={browser}",
                false,
                message,
                supported: false,
                extras: new()
                {
                    ["requested_browser"] = BrowserLauncherService.NormalizeBrowser(browser),
                    ["fallback_used"] = false,
                });
        }
        return Logged(
            "open_url",
            $"{url} browser={browserUsed}",
            true,
            message,
            extras: new() { ["browser"] = browserUsed });
    }

    private JarvisWorkflowSettings FindWorkflow(string name)
    {
        var saved = FindSavedWorkflow(name);
        if (saved != null)
            return saved;
        if (_settings.IsWorkflowDeleted(name))
            return null;

        var learned = _mappings.FindExact(LearnedMappingKind.Workflow, name);
        learned ??= _mappings.Search(name, LearnedMappingKind.Workflow, 2)
            .Select(entry => new
            {
                Entry = entry,
                Score = Math.Max(
                    FuzzyMatcher.Score(name, entry.Alias),
                    FuzzyMatcher.Score(name, entry.DisplayName)),
            })
            .Where(item => item.Score >= 0.78)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Entry)
            .FirstOrDefault();
        if (learned != null)
        {
            if (_settings.IsWorkflowDeleted(learned.Alias)
                || _settings.IsWorkflowDeleted(learned.Target))
                return null;
            name = learned.Target;
            saved = FindSavedWorkflow(name);
            if (saved != null)
                return saved;
        }
        var normalized = NormalizeWords(name);
        if (_settings.IsWorkflowDeleted(normalized))
            return null;
        return BuiltInWorkflow(normalized);
    }

    private JarvisWorkflowSettings FindSavedWorkflow(string name)
    {
        var normalized = NormalizeWords(name);
        var saved = _settings.Current.JarvisWorkflows?.FirstOrDefault(w =>
            (w.Aliases ?? []).Prepend(w.Name).Any(alias =>
                string.Equals(NormalizeWords(alias), normalized, StringComparison.OrdinalIgnoreCase)));
        if (saved != null)
            return saved;
        var ranked = _settings.Current.JarvisWorkflows?
            .Select(workflow => (Workflow: workflow, Score: (workflow.Aliases ?? [])
                .Prepend(workflow.Name)
                .Max(alias => FuzzyMatcher.Score(normalized, alias))))
            .Where(item => item.Score >= 0.72)
            .OrderByDescending(item => item.Score)
            .Take(2)
            .ToArray() ?? Array.Empty<(JarvisWorkflowSettings Workflow, double Score)>();
        if (ranked.Length > 0)
        {
            var top = ranked[0];
            var secondScore = ranked.Length > 1 ? ranked[1].Score : 0;
            if (top.Score >= 0.9 || top.Score - secondScore >= 0.08)
                return top.Workflow;
        }
        return null;
    }

    private static JarvisWorkflowSettings BuiltInWorkflow(string normalized) =>
        normalized switch
        {
            "coding mode" => new JarvisWorkflowSettings
            {
                Name = "Coding mode",
                Apps = "Visual Studio Code, Windows Terminal",
                Urls = "https://github.com/, https://chatgpt.com/",
            },
            "study mode" => new JarvisWorkflowSettings
            {
                Name = "Study mode",
                Apps = "Notepad",
                Urls = "https://www.youtube.com/results?search_query=focus+music",
            },
            "hackathon mode" => new JarvisWorkflowSettings
            {
                Name = "Hackathon mode",
                Apps = "Visual Studio Code, Windows Terminal",
                Urls = "https://devpost.com/, https://github.com/, https://vercel.com/, https://chatgpt.com/",
            },
            _ => null,
        };

    private static IReadOnlyList<JarvisWorkflowActionSettings> OrderedWorkflowActions(
        JarvisWorkflowSettings workflow)
    {
        if (workflow?.Actions is { Count: > 0 })
            return workflow.Actions;
        if (workflow == null)
            return Array.Empty<JarvisWorkflowActionSettings>();
        return SplitList(workflow.Apps)
            .Select(target => new JarvisWorkflowActionSettings { Kind = "auto", Target = target })
            .Concat(SplitList(workflow.Urls)
                .Select(target => new JarvisWorkflowActionSettings { Kind = "url", Target = target }))
            .Concat(string.IsNullOrWhiteSpace(workflow.Folder)
                ? Array.Empty<JarvisWorkflowActionSettings>()
                : [new JarvisWorkflowActionSettings { Kind = "folder", Target = workflow.Folder }])
            .ToArray();
    }

    private static List<JarvisWorkflowActionSettings> ParseWorkflowActions(JsonElement args)
    {
        var actions = new List<JarvisWorkflowActionSettings>();
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("actions", out var actionArray)
            && actionArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in actionArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                actions.Add(new JarvisWorkflowActionSettings
                {
                    Kind = GetString(item, "kind") ?? GetString(item, "type"),
                    Target = GetString(item, "target") ?? GetString(item, "value"),
                });
            }
        }
        if (actions.Count > 0)
            return actions;
        actions.AddRange(SplitList(GetString(args, "apps"))
            .Select(target => new JarvisWorkflowActionSettings { Kind = "app", Target = target }));
        actions.AddRange(SplitList(GetString(args, "urls"))
            .Select(target => new JarvisWorkflowActionSettings { Kind = "url", Target = target }));
        var folder = GetString(args, "folder");
        if (!string.IsNullOrWhiteSpace(folder))
            actions.Add(new JarvisWorkflowActionSettings { Kind = "folder", Target = folder });
        return actions;
    }

    private static bool WorkflowRecordsMatch(
        JarvisWorkflowSettings expected,
        JarvisWorkflowSettings persisted)
    {
        if (expected == null || persisted == null
            || persisted.Version != JarvisWorkflowSettings.CurrentVersion
            || !string.Equals(
                NormalizePhraseText(expected.Name),
                NormalizePhraseText(persisted.Name),
                StringComparison.Ordinal))
            return false;
        var expectedAliases = (expected.Aliases ?? [])
            .Prepend(expected.Name)
            .Select(NormalizePhraseText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        var persistedAliases = (persisted.Aliases ?? [])
            .Prepend(persisted.Name)
            .Select(NormalizePhraseText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedAliases.SetEquals(persistedAliases))
            return false;
        var expectedActions = OrderedWorkflowActions(expected);
        var persistedActions = OrderedWorkflowActions(persisted);
        return expectedActions.Count > 0
            && expectedActions.Count == persistedActions.Count
            && expectedActions.Zip(persistedActions).All(pair =>
                string.Equals(pair.First.Kind, pair.Second.Kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pair.First.Target, pair.Second.Target, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MappingRecordsMatch(
        LearnedMappingEntry expected,
        LearnedMappingEntry persisted) =>
        expected != null
        && persisted != null
        && expected.Kind == persisted.Kind
        && string.Equals(
            FuzzyMatcher.Normalize(expected.Alias),
            FuzzyMatcher.Normalize(persisted.Alias),
            StringComparison.Ordinal)
        && string.Equals(expected.Target, persisted.Target, StringComparison.OrdinalIgnoreCase);

    private static object PublicWorkflow(JarvisWorkflowSettings workflow) => new
    {
        workflow.Name,
        workflow.Version,
        aliases = workflow.Aliases?.ToArray() ?? Array.Empty<string>(),
        actions = OrderedWorkflowActions(workflow)
            .Select(action => new { action.Kind, action.Target })
            .ToArray(),
    };

    private Dictionary<string, object> Logged(
        string action,
        string detail,
        bool success,
        string message,
        bool supported = true,
        Dictionary<string, object> extras = null)
    {
        _history.Add(action, detail, success);
        return Result(success, message, supported: supported, extras: extras);
    }

    private static Dictionary<string, object> Result(
        bool ok,
        string message,
        bool supported = true,
        bool needsClarification = false,
        bool confirmationRequired = false,
        Dictionary<string, object> extras = null)
    {
        var result = extras ?? new Dictionary<string, object>();
        result["ok"] = ok;
        result["supported"] = supported;
        result["needs_clarification"] = needsClarification;
        result["confirmation_required"] = confirmationRequired;
        result["message"] = message ?? "";
        return result;
    }

    private static string GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : value.ToString();
    }

    private static int GetInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        return int.TryParse(value.ToString(), out number) ? number : 0;
    }

    private static string NormalizeWords(string text) =>
        string.Join(" ", (text ?? "").Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string CleanRoutineTrigger(string value)
    {
        var trigger = NormalizePhraseText(value);
        foreach (var prefix in new[]
        {
            "whenever i say ", "when i say ", "remember that whenever i say ",
            "remember whenever i say ", "remember this as ",
        })
        {
            if (trigger.StartsWith(prefix, StringComparison.Ordinal))
            {
                trigger = trigger[prefix.Length..].Trim();
                break;
            }
        }
        return Limit(trigger, 48);
    }

    private static string NormalizePhraseText(string text)
    {
        var wordsOnly = new string((text ?? "").ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character == '\''
                ? character
                : ' ')
            .ToArray());
        return NormalizeWords(wordsOnly);
    }

    private static bool ContainsPhrase(string value, params string[] phrases)
    {
        var padded = $" {value} ";
        return phrases.Any(phrase =>
            padded.Contains($" {NormalizePhraseText(phrase)} ", StringComparison.OrdinalIgnoreCase));
    }

    private static bool Succeeded(Dictionary<string, object> result) =>
        result.TryGetValue("ok", out var ok) && ok is bool success && success;

    private static object PublicMapping(LearnedMappingEntry entry) => new
    {
        entry.Id,
        kind = entry.Kind.ToString(),
        entry.Alias,
        entry.DisplayName,
        target = entry.Kind == LearnedMappingKind.App ? entry.DisplayName : entry.Target,
        entry.UpdatedAt,
    };

    private static bool TryStart(string fileName)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = fileName, UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    private static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
    }

    private static List<WindowInfo> EnumerateWindows()
    {
        var windows = new List<WindowInfo>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;
            var title = GetWindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title))
                return true;
            windows.Add(new WindowInfo(handle, title, GetProcessName(handle)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static IntPtr FindWindowByName(string name)
    {
        var normalized = NormalizeWords(name);
        return EnumerateWindows()
            .FirstOrDefault(w => w.Title.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || w.ProcessName.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            ?.Handle ?? IntPtr.Zero;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        if (length <= 0)
            return "";
        var builder = new StringBuilder(length + 1);
        GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetProcessName(IntPtr handle)
    {
        GetWindowThreadProcessId(handle, out var processId);
        try { return Process.GetProcessById((int)processId).ProcessName; }
        catch { return ""; }
    }

    private static bool IsBrowserProcess(string processName) =>
        processName is not null
        && new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc" }
            .Contains(processName, StringComparer.OrdinalIgnoreCase);

    private static void SendKeysOnUiThread(string keys)
    {
        var form = Application.OpenForms.Cast<Form>().FirstOrDefault();
        if (form?.InvokeRequired == true)
            form.Invoke(() => SendKeys.SendWait(keys));
        else
            SendKeys.SendWait(keys);
    }

    private static void PressMediaKey(byte key, int count)
    {
        for (var i = 0; i < count; i++)
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KeyeventfKeyup, UIntPtr.Zero);
        }
    }

    private static void SendChord(byte[] keys)
    {
        foreach (var key in keys)
            keybd_event(key, 0, 0, UIntPtr.Zero);
        for (var index = keys.Length - 1; index >= 0; index--)
            keybd_event(keys[index], 0, KeyeventfKeyup, UIntPtr.Zero);
    }

    private List<JarvisTodo> LoadTodos()
    {
        try
        {
            if (!File.Exists(_todosPath))
                return new List<JarvisTodo>();
            return JsonSerializer.Deserialize<List<JarvisTodo>>(File.ReadAllText(_todosPath)) ?? new();
        }
        catch { return new List<JarvisTodo>(); }
    }

    private void SaveTodos(List<JarvisTodo> todos)
    {
        Directory.CreateDirectory(SettingsService.AppDataDir);
        File.WriteAllText(_todosPath, JsonSerializer.Serialize(todos, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<string> SplitList(string value) =>
        (value ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeUrl(string value)
    {
        var url = value?.Trim() ?? "";
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;
        return url;
    }

    private static string Limit(string value, int maxLength)
    {
        var text = value ?? "";
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsTimerService)
            _timerService.Dispose();
        _screenGuidance.Dispose();
    }

    private sealed class PendingJarvisAction
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string ToolName { get; init; } = "";
        public JsonElement Args { get; init; }
        public bool IsSensitive { get; init; }
        public string Action { get; init; } = "";
        public string Target { get; init; } = "";
        public string Description { get; init; } = "";
        public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ConfirmedAt { get; set; }
    }

    private sealed class JarvisTodo
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Text { get; set; } = "";
        public bool Completed { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    }

    private sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}

internal sealed record SelectionContext(IntPtr Handle, string ProcessName, string WindowTitle);
internal sealed record SelectionSnapshot(
    string Text,
    DateTimeOffset CapturedAt,
    IntPtr Handle,
    string ProcessName,
    string WindowTitle);
