namespace KeyboardWtf.Services;

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyboardWtf.Destinations;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;
using System.Windows.Forms;
using KeyboardWtf.Services.Ai;
using KeyboardWtf.Services.BrowserAutomationV2;

public sealed class CommandRegistry
{
    internal static readonly TimeSpan SmartWritingTimeout = TimeSpan.FromSeconds(40);
    internal const string SmartWritingSystemInstruction =
        "You are a speech-to-writing engine. Listen carefully to the attached speech and return only " +
        "the text the user intends to type. Preserve names, terminology, meaning, wording, and tone as " +
        "accurately as possible. Remove obvious filler words and abandoned self-corrections only where " +
        "appropriate. Fix punctuation, capitalization, and basic grammar. Do not answer the user. Do not " +
        "explain anything. Do not perform actions. Do not add commentary. Output only the final text to insert.";
    private readonly KeyboardWtfApp _app;
    private readonly VoiceCaptureService _capture;
    private readonly DestinationRouter _router;
    private readonly SettingsService _settings;
    private readonly NotificationService _notifications;
    private readonly GeminiLiveConversationService _liveConversation;
    private readonly JarvisAutomationService _automation;
    private readonly JarvisActionHistoryService _history;
    private readonly BrowserAutomationCoordinator _browserAutomation;
    private readonly SmartActionPlanner _actionPlanner = new();
    private readonly object _browserPreRouteLock = new();
    private CancellationTokenSource _actionCts;
    private BrowserPreRouteReceipt _browserPreRouteReceipt;
    private string _awaitingLocalTextEcho = "";
    private DateTime _awaitingLocalTextEchoUtc;
    private bool _browserPreRouteOwnsCurrentTurn;
    private string _latestJarvisUserTurn = "";
    private DateTime _latestJarvisUserTurnUtc;

    public CommandRegistry(
        KeyboardWtfApp app,
        VoiceCaptureService capture,
        DestinationRouter router,
        SettingsService settings,
        NotificationService notifications,
        GeminiLiveConversationService liveConversation = null,
        JarvisAutomationService automation = null,
        JarvisActionHistoryService history = null,
        BrowserAutomationCoordinator browserAutomation = null)
    {
        _app = app;
        _capture = capture;
        _router = router;
        _settings = settings;
        _notifications = notifications;
        _liveConversation = liveConversation;
        _automation = automation;
        _history = history;
        _browserAutomation = browserAutomation;
    }

    public void TogglePushToTalk() => _ = TogglePushToTalkAsync();
    public void ToggleSmartMode()
    {
        if (KeyboardWtfState.IsProcessingAi)
        {
            CancelCurrent();
            return;
        }
        if (StopLiveBeforeAnotherVoiceMode("Smart Writing"))
            return;
        // Wake the authenticated REST path while the user is speaking. This
        // endpoint never reserves credits or invokes Vertex, and a failed
        // warmup must never prevent local recording from starting.
        _ = _liveConversation?.WarmPaidBackendAsync();
        _ = _capture.ToggleSmartModeAsync(HandleSmartWritingAsync);
    }
    public void ToggleDictation()
    {
        if (KeyboardWtfState.IsProcessingAi)
        {
            CancelCurrent();
            return;
        }
        if (StopLiveBeforeAnotherVoiceMode("Dictation"))
            return;
        _ = _capture.ToggleDictationAsync(DeliverRawDictationAsync);
    }
    public void ToggleCommandMode()
    {
        if (KeyboardWtfState.IsProcessingAi)
        {
            CancelCurrent();
            return;
        }
        if (StopLiveBeforeAnotherVoiceMode("Command"))
            return;
        _ = _capture.ToggleCommandModeAsync(HandleSmartExecutionAsync);
    }
    public void ToggleConversation() => ToggleJarvisMode();
    public void ToggleJarvisMode()
    {
        if (_liveConversation == null)
            return;
        if (KeyboardWtfState.IsProcessingAi)
        {
            CancelCurrent();
            return;
        }
        if (_liveConversation.IsActive || _liveConversation.IsStarting)
        {
            _ = _liveConversation.StopAsync();
            return;
        }
        // The dedicated Jarvis shortcut is explicit user intent. Start the
        // authenticated Live transport immediately; no local transcription or
        // speech-activity preflight owns this path.
        _ = _liveConversation.StartAsync();
    }
    public void CancelCurrent() => _ = CancelCurrentAsync();
    public Task CancelCurrentAndWaitAsync() => CancelCurrentAsync();
    public void QuickSendDefault() => _ = _capture.QuickSendAsync(_settings.Current.DefaultDestination ?? "Clipboard");
    public void OpenSettings() => _app.OpenSettings();
    public object PendingSensitiveAction => _automation?.PendingSnapshot();
    internal static bool UsesJarvisTranscriptPreflight => false;

    private bool StopLiveBeforeAnotherVoiceMode(string requestedMode)
    {
        if (_liveConversation?.IsActive != true && _liveConversation?.IsStarting != true)
            return false;
        _ = _liveConversation.StopAsync();
        _notifications.Info(
            "Jarvis stopping",
            $"Press the {requestedMode} shortcut again after Jarvis closes.");
        return true;
    }
    public void ObserveJarvisTranscript(string text) =>
        TryPreRouteJarvisTranscript(text);

    public bool TryPreRouteJarvisTranscript(string text, bool fromLocalTextSubmission = false)
    {
        _automation?.ObserveUserTranscript(text);
        var userTurn = (text ?? "").Trim();
        if (userTurn.Length == 0)
            return false;

        // Text turns are first pre-routed locally, then echoed by Gemini Live
        // as input transcription. Reuse that one receipt for the echo so a
        // click/tab switch/browser start is never performed twice.
        lock (_browserPreRouteLock)
        {
            var now = DateTime.UtcNow;
            if (!fromLocalTextSubmission
                && _browserPreRouteReceipt?.IsEchoOf(userTurn, now) == true
                && !string.IsNullOrWhiteSpace(_awaitingLocalTextEcho)
                && now - _awaitingLocalTextEchoUtc <= TimeSpan.FromSeconds(12))
            {
                _awaitingLocalTextEcho = "";
                _browserPreRouteOwnsCurrentTurn = true;
                _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                _latestJarvisUserTurnUtc = now;
                return true;
            }

            _browserPreRouteOwnsCurrentTurn = false;
            _browserPreRouteReceipt = null;
            _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
            _latestJarvisUserTurnUtc = now;
            if (fromLocalTextSubmission)
            {
                _awaitingLocalTextEcho = userTurn;
                _awaitingLocalTextEchoUtc = now;
            }
            else
            {
                _awaitingLocalTextEcho = "";
            }
        }

        if (_browserAutomation != null
            && JarvisAutomationService.TryGetBrowserTabSelection(
                text,
                out var tabSelection,
                out var tabDescription))
        {
            var ok = false;
            var message = "";
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var receipt = _browserAutomation
                    .ActivateExistingTabAsync(tabSelection, timeout.Token)
                    .GetAwaiter()
                    .GetResult();
                ok = receipt.Ok;
                message = receipt.Ok
                    ? $"Switched to {tabDescription} in the existing Chrome window."
                    : string.IsNullOrWhiteSpace(receipt.Error)
                        ? "Chrome did not confirm the requested tab."
                        : receipt.Error;
            }
            catch (Exception ex)
            {
                message = ex is OperationCanceledException
                    ? "Chrome did not confirm the tab switch in time."
                    : ex.Message;
            }

            lock (_browserPreRouteLock)
            {
                _browserPreRouteReceipt = new BrowserPreRouteReceipt
                {
                    UserTurn = userTurn,
                    ToolName = "browser_action",
                    Ok = ok,
                    Message = message,
                };
                _browserPreRouteOwnsCurrentTurn = true;
            }
            if (ok)
                _notifications.InfoTransient("Browser", message);
            else
                _notifications.WarningTransient("Browser", message);
            AppLog.Info(
                $"Chrome Helper tab selection: ok={ok} selection={tabSelection} request={BrowserAutomationText.Truncate(text, 120)}");
            return true;
        }

        if (_automation != null
            && JarvisAutomationService.IsDirectBrowserShortcutRequest(text))
        {
            var shortcut = _automation.ExecuteBrowserShortcut(text);
            var ok = shortcut.TryGetValue("ok", out var value)
                && value is bool success
                && success;
            var message = shortcut.TryGetValue("message", out var detail)
                ? detail?.ToString() ?? ""
                : "";
            lock (_browserPreRouteLock)
            {
                _browserPreRouteReceipt = new BrowserPreRouteReceipt
                {
                    UserTurn = userTurn,
                    ToolName = "browser_action",
                    Ok = ok,
                    Message = message,
                };
                _browserPreRouteOwnsCurrentTurn = true;
            }
            if (ok)
                _notifications.InfoTransient("Browser", message);
            else
                _notifications.WarningTransient("Browser", message);
            AppLog.Info(
                $"Deterministic browser shortcut pre-route: ok={ok} request={BrowserAutomationText.Truncate(text, 120)}");
            return true;
        }

        if (_browserAutomation == null || string.IsNullOrWhiteSpace(text))
            return false;

        // A running browser task does not own the whole Jarvis conversation.
        // In particular, normal Chrome/desktop commands must remain normal
        // commands rather than being injected into the controlled webpage.
        var active = _browserAutomation.Snapshot();
        if (active.CanCancel)
        {
            AppLog.Info(
                $"Browser pre-route skipped while task={active.TaskId}: the live conversation decides normal commands; only explicit browser-task guidance is accepted by the coordinator.");
            return false;
        }

        var classification = _browserAutomation.Classify(text);
        if (classification.Decision != BrowserAutomationDecision.BrowserAutomation)
            return false;

        var result = _browserAutomation.Start(userTurn);
        if (!result.Started || string.IsNullOrWhiteSpace(result.TaskId))
            return false;

        lock (_browserPreRouteLock)
        {
            _browserPreRouteReceipt = new BrowserPreRouteReceipt
            {
                UserTurn = userTurn,
                ToolName = "browser_automation",
                Ok = true,
                Started = true,
                TaskId = result.TaskId,
                Message = result.Message,
            };
            _browserPreRouteOwnsCurrentTurn = true;
        }
        AppLog.Info(
            $"Browser pre-route: task={result.TaskId} started={result.Started} current={classification.RefersToCurrentPage}");
        return true;
    }

    private async Task TogglePushToTalkAsync()
    {
        if (!KeyboardWtfState.IsRecording)
        {
            _capture.Start(RecordingMode.PushToTalk);
            return;
        }

        if (KeyboardWtfState.CurrentRecordingMode == RecordingMode.PushToTalk)
        {
            var transcript = await _capture.StopAndTranscribeAsync();
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                var destination = DestinationRegistry.Current?.Name ?? _settings.Current.DefaultDestination ?? "Clipboard";
                var ok = await _router.SendAsync(
                    destination,
                    transcript,
                    _capture.CurrentDeliveryTarget,
                    _capture.CurrentOperationToken);
                _notifications.Info(ok ? "Sent" : "Send failed", KeyboardWtfState.LastSendResult ?? destination);
            }
        }
    }

    public async Task<Dictionary<string, object>> ExecuteJarvisToolAsync(string name, JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var toolName = (name ?? "").Trim();
        KeyboardWtfState.SetUi(VoiceUiPhase.Executing, KeyboardWtfState.AssistantName, HumanizeTool(toolName));

        try
        {
            if (TryGetPreRoutedToolResult(toolName, out var preRoutedResult))
                return preRoutedResult;

            if (ShouldBlockCompetingToolForPreRoutedBrowserTurn(toolName))
            {
                return ToolResult(
                    false,
                    "This user turn is already owned by Browser Automation. No generic active-window or desktop action was sent.");
            }

            if (toolName == "browser_automation")
                return StartBrowserAutomation(args);
            if (toolName == "browser_automation_status")
                return BrowserAutomationStatus();
            if (toolName == "browser_automation_control")
                return await ControlBrowserAutomationAsync(args);
            if (toolName == "remember_link_alias")
                return await RememberLinkAliasAsync(args, token);
            if (toolName == "open_keyboard_wtf_settings")
                return OpenKeyboardWtfSettings(GetString(args, "section"));
            if (toolName == "browser_action"
                && _browserAutomation != null
                && JarvisAutomationService.TryGetBrowserTabSelection(
                    GetString(args, "action"),
                    out var tabSelection,
                    out var tabDescription))
            {
                var receipt = await _browserAutomation.ActivateExistingTabAsync(tabSelection, token);
                return ToolResult(
                    receipt.Ok,
                    receipt.Ok
                        ? $"Switched to {tabDescription} in the existing Chrome window."
                        : string.IsNullOrWhiteSpace(receipt.Error)
                            ? "Chrome did not confirm the requested tab."
                            : receipt.Error);
            }

            return _automation == null
                ? ToolResult(false, $"{toolName} is not a supported keyboard.wtf action yet.", supported: false)
                : await _automation.ExecuteAsync(toolName, args, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, $"Jarvis tool failed: {toolName}");
            KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Action failed", ex.Message);
            return ToolResult(false, ex.Message);
        }
    }

    private bool ShouldBlockCompetingToolForPreRoutedBrowserTurn(string toolName)
    {
        lock (_browserPreRouteLock)
        {
            if (!_browserPreRouteOwnsCurrentTurn)
                return false;

            return _browserPreRouteReceipt?.BlocksCompetingTool(toolName, DateTime.UtcNow)
                ?? !IsBrowserTaskLifecycleTool(toolName);
        }
    }

    internal static bool IsBrowserTaskLifecycleTool(string toolName) =>
        (toolName ?? "").Trim() is
            "browser_automation"
            or "browser_automation_status"
            or "browser_automation_control";

    public async Task HandleVoiceCommandAsync(string transcript)
    {
        var parsed = VoiceCommandParser.Parse(transcript);
        AppLog.Info($"Voice command parsed: {parsed.Kind} {parsed.Argument}");

        switch (parsed.Kind)
        {
            case ParsedCommandKind.Copy:
                await SendToAsync("Clipboard", TargetText(parsed));
                break;
            case ParsedCommandKind.TypeOut:
                await SendToAsync("Type Out", TargetText(parsed));
                break;
            case ParsedCommandKind.SendTo:
                await SendToAsync(parsed.Argument, TargetText(parsed));
                break;
            case ParsedCommandKind.MakeProfessional:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Rewrite this transcript to sound professional. Return only the rewritten text.");
                break;
            case ParsedCommandKind.MakeCasual:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Rewrite this transcript to sound casual, clear, and friendly. Return only the rewritten text.");
                break;
            case ParsedCommandKind.MakeShorter:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Make this transcript shorter while preserving the meaning. Return only the shortened text.");
                break;
            case ParsedCommandKind.Summarize:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Summarize this transcript into the fewest useful words. Return only the summary.");
                break;
            case ParsedCommandKind.BulletList:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Turn this transcript into concise bullet points. Return only the bullet list.");
                break;
            case ParsedCommandKind.FixGrammar:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Fix grammar, punctuation, and obvious speech recognition errors. Preserve meaning. Return only the corrected text.");
                break;
            case ParsedCommandKind.Email:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Turn this transcript into a polished email. Return only the email text.");
                break;
            case ParsedCommandKind.TranslateGerman:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Translate this transcript to German. Return only the translated text.");
                break;
            case ParsedCommandKind.SaveNote:
                SaveCurrentNote();
                break;
            case ParsedCommandKind.ReadBack:
                ReadBack();
                break;
            case ParsedCommandKind.ReplayLast:
                ReplayLast();
                break;
            case ParsedCommandKind.Cancel:
                _capture.Cancel();
                break;
            case ParsedCommandKind.OpenSettings:
                _app.OpenSettings();
                break;
            case ParsedCommandKind.OpenNotepad:
                OpenNotepad();
                break;
            case ParsedCommandKind.OpenUrl:
                OpenUrl(parsed.Argument);
                break;
            case ParsedCommandKind.GmailDraft:
                OpenGmailDraft(parsed);
                break;
            case ParsedCommandKind.SwitchWhisper:
                _settings.SaveSpeechEngine(SpeechEngine.Whisper);
                _notifications.Info("Speech engine", "Switched to Whisper.");
                break;
            case ParsedCommandKind.SwitchVosk:
                _settings.SaveSpeechEngine(SpeechEngine.Vosk);
                _notifications.Info("Speech engine", "Switched to Vosk.");
                break;
            case ParsedCommandKind.EnableAi:
                _settings.SaveUseAi(true);
                _notifications.Info("AI formatting", "Enabled.");
                break;
            case ParsedCommandKind.DisableAi:
                _settings.SaveUseAi(false);
                _notifications.Info("AI formatting", "Disabled.");
                break;
            default:
                KeyboardWtfState.SetTranscript(transcript);
                await DeliverTextAsync(transcript);
                break;
        }
    }

    public async Task HandleSmartExecutionAsync(string transcript)
    {
        ResetActionCancellation();
        var token = _actionCts.Token;
        KeyboardWtfState.IsProcessingAi = true;
        KeyboardWtfState.SetUi(VoiceUiPhase.Thinking, "Understanding command", "Gemini is planning the safest action.");

        try
        {
            var plan = await _actionPlanner.PlanAsync(transcript, token);
            token.ThrowIfCancellationRequested();
            AppLog.Info($"Smart action planned: {plan.Action}");
            KeyboardWtfState.SetUi(VoiceUiPhase.Executing, "Executing", DescribePlan(plan));
            await ExecutePlanAsync(plan, token);
        }
        catch (OperationCanceledException)
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "The action was not executed.");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Smart command failed");
            KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Command failed", ex.Message);
            _notifications.Error("Command failed", ex.Message);
        }
        finally
        {
            KeyboardWtfState.IsProcessingAi = false;
        }
    }

    private async Task HandleSmartWritingAsync(SmartWritingAudioCapture capture)
    {
        if (AiProviderRegistry.Current is not PaidCloudAiProvider paidProvider)
        {
            var localTranscript = await _capture.TranscribeCapturedAudioAsync(
                capture.WavData,
                preferFast: true,
                _capture.CurrentOperationToken);
            if (!string.IsNullOrWhiteSpace(localTranscript))
                await HandleSmartWritingTranscriptAsync(localTranscript);
            return;
        }

        ResetActionCancellation();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_actionCts.Token);
        var timeoutLimit = SmartWritingTimeoutFor(capture.RecordingDurationMs);
        timeout.CancelAfter(timeoutLimit);
        var token = timeout.Token;
        var operation = Guid.NewGuid().ToString("N")[..12];
        var operationStarted = Stopwatch.GetTimestamp();
        long polishMs = 0;
        long insertionMs = 0;
        try
        {
            KeyboardWtfState.IsProcessingAi = true;
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Thinking,
                "Polishing",
                "Gemini is turning your speech into writing.");
            AppLog.Info(
                $"Smart Writing stage: operation={operation} stage=audio_ready recordingMs={capture.RecordingDurationMs} bytes={capture.WavData.Length}");
            var polishStarted = Stopwatch.GetTimestamp();
            var result = await paidProvider.ReformatAudioAsync(
                capture.WavData,
                SmartWritingSystemInstruction,
                KeyboardWtfState.SelectedLanguage,
                token);
            polishMs = (long)Stopwatch.GetElapsedTime(polishStarted).TotalMilliseconds;
            AppLog.Info(
                $"Smart Writing stage: operation={operation} stage=audio_polish_completed elapsedMs={polishMs} characters={result?.Length ?? 0}");
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("Gemini returned no text to insert.");
            KeyboardWtfState.SetTranscript(result);
            var insertionStarted = Stopwatch.GetTimestamp();
            await DeliverTextAsync(result, token);
            insertionMs = (long)Stopwatch.GetElapsedTime(insertionStarted).TotalMilliseconds;
            AppLog.Info(
                $"Smart Writing audio timing: operation={operation} recordingMs={capture.RecordingDurationMs} " +
                $"polishMs={polishMs} insertionMs={insertionMs} totalAfterRecordingMs=" +
                $"{(long)Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds}");
        }
        catch (OperationCanceledException) when (_actionCts.IsCancellationRequested)
        {
            AppLog.Info($"Smart Writing stage: operation={operation} stage=cancelled");
            KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "Smart writing was stopped.");
        }
        catch (OperationCanceledException)
        {
            AppLog.Warning(
                $"Smart Writing stage: operation={operation} stage=timeout limitMs={(long)timeoutLimit.TotalMilliseconds}");
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Error,
                "Smart Writing timed out",
                "The request took too long and was stopped. Please try again.");
            _notifications.Warning(
                "Smart Writing timed out",
                "The request took too long and was stopped. Please try again.");
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Paid audio Smart Writing failed; no text was inserted");
            KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Smart Writing unavailable", ex.Message);
            _notifications.Warning("Smart Writing unavailable", ex.Message);
        }
        finally
        {
            KeyboardWtfState.IsProcessingAi = false;
            if (NeedsSmartWritingTerminalCleanup(KeyboardWtfState.UiPhase, KeyboardWtfState.UiTitle))
            {
                AppLog.Warning($"Smart Writing stage: operation={operation} stage=terminal_cleanup_recovery");
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Error,
                    "Smart Writing did not finish",
                    "The operation ended without a delivery result. Please try again.");
            }
        }
    }

    internal static TimeSpan SmartWritingTimeoutFor(long recordingDurationMs) =>
        TimeSpan.FromSeconds(Math.Clamp(40d + recordingDurationMs / 20_000d, 40d, 55d));

    private async Task HandleSmartWritingTranscriptAsync(string transcript)
    {
        ResetActionCancellation();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_actionCts.Token);
        timeout.CancelAfter(SmartWritingTimeout);
        var token = timeout.Token;
        var operation = Guid.NewGuid().ToString("N")[..12];
        var operationStarted = Stopwatch.GetTimestamp();
        long polishMs = 0;
        long insertionMs = 0;
        try
        {
            AppLog.Info($"Smart Writing stage: operation={operation} stage=transcript_ready");
            KeyboardWtfState.SetTranscript(transcript);
            var polishStarted = Stopwatch.GetTimestamp();
            AppLog.Info($"Smart Writing stage: operation={operation} stage=polish_started");
            var result = await _capture.ApplyPromptToCurrentTranscriptAsync(
                "Clean this spoken dictation into ready-to-send writing. Remove filler words and false starts, " +
                "honor self-corrections such as 'actually' or 'scratch that', add natural punctuation, and keep " +
                "the speaker's meaning and tone. Return only the final text.",
                token);
            polishMs = (long)Stopwatch.GetElapsedTime(polishStarted).TotalMilliseconds;
            AppLog.Info(
                $"Smart Writing stage: operation={operation} stage=polish_completed elapsedMs={polishMs}");
            token.ThrowIfCancellationRequested();
            var insertionStarted = Stopwatch.GetTimestamp();
            AppLog.Info($"Smart Writing stage: operation={operation} stage=insertion_started");
            await DeliverTextAsync(string.IsNullOrWhiteSpace(result) ? transcript : result, token);
            insertionMs = (long)Stopwatch.GetElapsedTime(insertionStarted).TotalMilliseconds;
            AppLog.Info(
                $"Smart Writing timing: operation={operation} recordingMs={_capture.LastRecordingDurationMs} " +
                $"transcriptionMs={_capture.LastTranscriptionDurationMs} polishMs={polishMs} " +
                $"insertionMs={insertionMs} totalAfterTranscriptMs=" +
                $"{(long)Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds}");
        }
        catch (OperationCanceledException) when (_actionCts.IsCancellationRequested)
        {
            AppLog.Info($"Smart Writing stage: operation={operation} stage=cancelled");
            KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "Smart writing was stopped.");
        }
        catch (OperationCanceledException)
        {
            AppLog.Warning(
                $"Smart Writing stage: operation={operation} stage=timeout limitMs={(long)SmartWritingTimeout.TotalMilliseconds}");
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Error,
                "Smart Writing timed out",
                "The request took too long and was stopped. Please try again.");
            _notifications.Warning(
                "Smart Writing timed out",
                "The request took too long and was stopped. Please try again.");
        }
        catch (Exception ex)
        {
            if (AiProviderRegistry.Current is PaidCloudAiProvider)
            {
                AppLog.Warning(ex, "Paid Smart Writing failed; no text was inserted");
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Error,
                    "Smart Writing unavailable",
                    ex.Message);
                _notifications.Warning("Smart Writing unavailable", ex.Message);
                return;
            }
            AppLog.Warning(ex, "Smart writing AI failed; delivering raw transcript");
            _notifications.Warning("AI unavailable", "Typed the raw transcript instead.");
            await DeliverTextAsync(transcript, token);
        }
        finally
        {
            // Every Smart Writing operation owns one terminal UI transition.
            // This guards against a completed provider/delivery path leaving
            // the stopwatch overlay in Polishing indefinitely.
            if (NeedsSmartWritingTerminalCleanup(
                    KeyboardWtfState.UiPhase,
                    KeyboardWtfState.UiTitle))
            {
                AppLog.Warning(
                    $"Smart Writing stage: operation={operation} stage=terminal_cleanup_recovery");
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Error,
                    "Smart Writing did not finish",
                    "The operation ended without a delivery result. Please try again.");
            }
        }
    }

#if KEYBOARD_WTF_PAID_PRODUCTION_CANDIDATE
    internal async Task<object> RunSmartWritingAudioDiagnosticAsync(byte[] wavData)
    {
        if (wavData is null || wavData.Length is < 44 or > 10 * 1024 * 1024)
            throw new ArgumentException("The diagnostic WAV is empty or too large.", nameof(wavData));
        var durationMs = Math.Max(1, (long)((wavData.Length - 44L) * 1000d / 32_000d));
        var started = Stopwatch.GetTimestamp();
        await HandleSmartWritingAsync(new SmartWritingAudioCapture(wavData, durationMs));
        return new
        {
            phase = KeyboardWtfState.UiPhase.ToString(),
            title = KeyboardWtfState.UiTitle,
            terminal = !NeedsSmartWritingTerminalCleanup(
                KeyboardWtfState.UiPhase,
                KeyboardWtfState.UiTitle),
            elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
    }

    internal async Task<object> RunSmartWritingDiagnosticAsync(string transcript)
    {
        transcript = (transcript ?? "").Trim();
        if (transcript.Length is < 1 or > 1000)
            throw new ArgumentException("The diagnostic transcript must contain 1 to 1000 characters.", nameof(transcript));

        var started = Stopwatch.GetTimestamp();
        await HandleSmartWritingTranscriptAsync(transcript);
        return new
        {
            phase = KeyboardWtfState.UiPhase.ToString(),
            title = KeyboardWtfState.UiTitle,
            terminal = !NeedsSmartWritingTerminalCleanup(
                KeyboardWtfState.UiPhase,
                KeyboardWtfState.UiTitle),
            elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        };
    }
#endif

    private async Task<Dictionary<string, object>> RememberLinkAliasAsync(
        JsonElement args,
        CancellationToken token)
    {
        if (_automation == null)
            return ToolResult(false, "No action executor is available.", supported: false);
        var alias = GetString(args, "alias");
        var url = GetString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            var selection = await _automation.ExecuteAsync(
                "get_selected_text",
                JsonSerializer.SerializeToElement(new { }),
                token);
            url = TryGetSelectedHttpUrl(selection);
            if (string.IsNullOrWhiteSpace(url))
            {
                return ToolResult(
                    false,
                    "The selected text is not a valid http or https link. Select one link and try again.",
                    supported: false);
            }
            args = JsonSerializer.SerializeToElement(new { alias, url });
        }
        return await _automation.ExecuteAsync("remember_link_alias", args, token);
    }

    internal static string TryGetSelectedHttpUrl(Dictionary<string, object> result)
    {
        if (result == null
            || !result.TryGetValue("ok", out var ok)
            || ok is not true
            || !result.TryGetValue("text", out var textValue))
            return "";
        var text = textValue?.ToString()?.Trim() ?? "";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : "";
    }

    internal static bool NeedsSmartWritingTerminalCleanup(
        VoiceUiPhase phase,
        string title) =>
        phase == VoiceUiPhase.Thinking
        && string.Equals(title, "Polishing", StringComparison.Ordinal);

    private async Task DeliverRawDictationAsync(string transcript)
    {
        KeyboardWtfState.SetTranscript(transcript);
        await DeliverTextAsync(transcript, _capture.CurrentOperationToken);
    }

    private async Task ExecutePlanAsync(SmartActionPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (plan.Action)
        {
            case "open_notepad":
                OpenNotepad();
                break;
            case "open_settings":
                OpenWindowsSettings();
                break;
            case "open_url":
                OpenUrl(plan.Content);
                break;
            case "gmail_draft":
                OpenGmailDraft(plan.Recipient, plan.Subject, plan.Content);
                break;
            case "copy":
                await SendToAsync("Clipboard", plan.Content);
                break;
            case "ask":
                KeyboardWtfState.LastSendResult = "Needs clarification";
                break;
            default:
                KeyboardWtfState.SetTranscript(plan.Content);
                await DeliverTextAsync(plan.Content);
                break;
        }

        token.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(plan.SpokenResponse))
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Speaking, "keyboard.wtf", plan.SpokenResponse);
            TtsService.Instance.Speak(plan.SpokenResponse, "en");
            var wordCount = plan.SpokenResponse.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            var speakingMs = Math.Clamp(wordCount * 330, 1200, 5500);
            await Task.Delay(speakingMs, token);
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Done,
                KeyboardWtfState.LastSendResult ?? "Done",
                Shorten(plan.Content, 90));
        }
        else
        {
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Done,
                KeyboardWtfState.LastSendResult ?? "Done",
                Shorten(plan.Content, 90));
        }
    }

    private Dictionary<string, object> OpenKeyboardWtfSettings(string section)
    {
        var canonical = SettingsSectionRegistry.Resolve(section);
        if (!string.IsNullOrWhiteSpace(section) && string.IsNullOrWhiteSpace(canonical))
            return ToolResult(false, "That keyboard.wtf Settings section does not exist.", supported: false);
        _app.OpenSettings(canonical);
        KeyboardWtfState.LastSendResult = "Opened keyboard.wtf Settings";
        return ToolResult(
            true,
            string.IsNullOrWhiteSpace(canonical)
                ? "Opened keyboard.wtf Settings."
                : $"Opened keyboard.wtf Settings at {canonical.Replace('-', ' ')}.");
    }

    private static Dictionary<string, object> OpenWindowsSettings()
    {
        Process.Start(new ProcessStartInfo { FileName = "ms-settings:", UseShellExecute = true });
        KeyboardWtfState.LastSendResult = "Opened Windows Settings";
        return ToolResult(true, "Opened Windows Settings.");
    }

    private Dictionary<string, object> OpenAllowedApp(string appName)
    {
        var normalized = NormalizeAppName(appName);
        if (string.IsNullOrWhiteSpace(normalized))
            return ToolResult(false, "Which app should I open?", needsClarification: true);

        switch (normalized)
        {
            case "notepad":
                OpenNotepad();
                return ToolResult(true, "Opened Notepad.");
            case "calculator":
                return StartAllowedProcess("calc.exe", "Opened Calculator.");
            case "paint":
                return StartAllowedProcess("mspaint.exe", "Opened Paint.");
            case "file explorer":
                return StartAllowedProcess("explorer.exe", "Opened File Explorer.");
            case "settings":
                return OpenWindowsSettings();
            case "gmail":
                return OpenSafeUrl("https://mail.google.com/mail/u/0/#inbox");
            case "whatsapp":
                return OpenWhatsAppHome("Opened WhatsApp.");
            default:
                return ToolResult(
                    false,
                    $"I cannot open {appName} yet. Supported apps are Notepad, Calculator, Paint, File Explorer, Settings, Gmail, and WhatsApp.",
                    supported: false);
        }
    }

    private Dictionary<string, object> OpenSafeUrl(string url)
    {
        if (!IsSafeHttpUrl(url, out var uri))
        {
            KeyboardWtfState.LastSendResult = "Unsafe URL blocked";
            _notifications.Warning("URL blocked", "Only http and https links can be opened from voice.");
            return ToolResult(false, "Only http and https links can be opened from voice.", supported: false);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true,
        });
        KeyboardWtfState.LastSendResult = $"Opened {uri.Host}";
        _notifications.Info("Opened", uri.Host);
        return ToolResult(true, $"Opened {uri.Host}.", detail: uri.ToString());
    }

    private Dictionary<string, object> OpenGmailDraftTool(JsonElement args)
    {
        var recipient = GetString(args, "recipient") ?? GetString(args, "to") ?? "";
        var subject = GetString(args, "subject") ?? "";
        var body = GetString(args, "body") ?? GetString(args, "message") ?? "";

        if (string.IsNullOrWhiteSpace(recipient))
            return ToolResult(false, "Who should I address the email to?", needsClarification: true);
        if (string.IsNullOrWhiteSpace(body))
            return ToolResult(false, "What should the email say?", needsClarification: true);

        OpenGmailDraft(recipient, subject, body);
        return ToolResult(true, "Gmail draft opened. Review it before sending.", detail: recipient);
    }

    private Dictionary<string, object> PrepareWhatsAppMessage(JsonElement args)
    {
        var message = GetString(args, "message") ?? GetString(args, "text") ?? "";
        var contactName = GetString(args, "contact_name") ?? GetString(args, "contactName") ?? "";
        var phoneNumber = GetString(args, "phone_number") ?? GetString(args, "phoneNumber") ?? "";

        if (string.IsNullOrWhiteSpace(message))
            return ToolResult(false, "What should the WhatsApp message say?", needsClarification: true);

        ClipboardHelper.SetText(message);
        if (!string.IsNullOrWhiteSpace(phoneNumber))
        {
            var phone = NormalizePhoneNumber(phoneNumber);
            if (string.IsNullOrWhiteSpace(phone))
                return ToolResult(false, "That phone number does not look usable.", needsClarification: true);

            var encoded = Uri.EscapeDataString(message);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"whatsapp://send?phone={Uri.EscapeDataString(phone)}&text={encoded}",
                    UseShellExecute = true,
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"https://wa.me/{phone.TrimStart('+')}?text={encoded}",
                    UseShellExecute = true,
                });
            }

            KeyboardWtfState.LastSendResult = "WhatsApp draft opened; manual send required";
            _notifications.Info("WhatsApp", "Message prepared. Review and send manually.");
            return ToolResult(true, "WhatsApp message prepared. Review it before sending.", detail: phone);
        }

        var result = OpenWhatsAppHome("WhatsApp opened; message copied.");
        var contact = string.IsNullOrWhiteSpace(contactName) ? "that contact" : contactName.Trim();
        result["ok"] = false;
        result["supported"] = false;
        result["message"] = $"I cannot choose the WhatsApp contact {contact} automatically yet. I copied the message and opened WhatsApp so you can paste it.";
        result["contact_lookup_supported"] = false;
        return result;
    }

    private async Task<Dictionary<string, object>> CopyTextToolAsync(string text, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ToolResult(false, "What should I copy?", needsClarification: true);
        await SendToAsync("Clipboard", text);
        token.ThrowIfCancellationRequested();
        return ToolResult(true, "Copied to clipboard.");
    }

    private Dictionary<string, object> StartAllowedProcess(string fileName, string message)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = true,
        });
        KeyboardWtfState.LastSendResult = message.TrimEnd('.');
        _notifications.Info("Opened", message.TrimEnd('.'));
        KeyboardWtfState.SetUi(VoiceUiPhase.Done, message.TrimEnd('.'), "Ready.");
        return ToolResult(true, message);
    }

    private Dictionary<string, object> OpenWhatsAppHome(string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "whatsapp://", UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo { FileName = "https://web.whatsapp.com", UseShellExecute = true });
        }

        KeyboardWtfState.LastSendResult = message.TrimEnd('.');
        _notifications.Info("WhatsApp", message.TrimEnd('.'));
        return ToolResult(true, message);
    }

    private void OpenNotepad()
    {
        var systemNotepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "notepad.exe");
        var executable = File.Exists(systemNotepad) ? systemNotepad : "notepad.exe";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            KeyboardWtfState.LastSendResult = "Opened Notepad";
            KeyboardWtfState.SetUi(VoiceUiPhase.Done, "Opened Notepad", "Ready for dictation.");
            _notifications.Info("Opened", "Notepad is ready.");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Notepad launch failed");
            KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Could not open Notepad", ex.Message);
            _notifications.Error("Notepad failed", ex.Message);
        }
    }

    private void OpenUrl(string url)
    {
        if (!IsSafeHttpUrl(url, out var uri))
        {
            KeyboardWtfState.LastSendResult = "Unsafe URL blocked";
            _notifications.Warning("URL blocked", "Only http and https links can be opened from voice.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true,
        });
        KeyboardWtfState.LastSendResult = $"Opened {uri.Host}";
        _notifications.Info("Opened", uri.Host);
    }

    private void OpenGmailDraft(ParsedVoiceCommand parsed)
    {
        var recipient = parsed.Argument ?? "";
        var body = !string.IsNullOrWhiteSpace(parsed.Content)
            ? parsed.Content
            : KeyboardWtfState.CurrentTranscript;

        OpenGmailDraft(recipient, "", body);
    }

    private void OpenGmailDraft(string recipient, string subject, string body)
    {
        recipient ??= "";
        subject ??= "";
        body ??= "";

        if (!string.IsNullOrWhiteSpace(body))
            ClipboardHelper.SetText(body);

        var query = new List<string> { "view=cm", "fs=1" };
        if (!string.IsNullOrWhiteSpace(recipient))
            query.Add($"to={Uri.EscapeDataString(recipient)}");
        if (!string.IsNullOrWhiteSpace(subject))
            query.Add($"su={Uri.EscapeDataString(subject)}");
        if (!string.IsNullOrWhiteSpace(body))
            query.Add($"body={Uri.EscapeDataString(body)}");

        var url = "https://mail.google.com/mail/?" + string.Join("&", query);
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });

        KeyboardWtfState.LastSendResult = "Gmail draft opened; manual send required";
        KeyboardWtfState.SetUi(
            VoiceUiPhase.Done,
            "Gmail draft ready",
            "Review the draft, then click Send.");
        _notifications.Info("Gmail draft", "Opened Gmail compose. Review and send manually.");
    }

    private async Task SendToAsync(string destination, string text = null)
    {
        text ??= KeyboardWtfState.CurrentTranscript;
        if (!string.IsNullOrWhiteSpace(text))
            KeyboardWtfState.SetTranscript(text);
        var ok = await _router.SendAsync(destination, text);
        _notifications.Info(ok ? "Sent" : "Send failed", KeyboardWtfState.LastSendResult ?? destination);
    }

    private static string TargetText(ParsedVoiceCommand parsed) =>
        !string.IsNullOrWhiteSpace(parsed.Content)
            ? parsed.Content
            : KeyboardWtfState.CurrentTranscript;

    private static void ApplyInlineContent(ParsedVoiceCommand parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Content))
            KeyboardWtfState.SetTranscript(parsed.Content);
    }

    private async Task TransformAndCopyAsync(string prompt)
    {
        var text = await _capture.ApplyPromptToCurrentTranscriptAsync(prompt);
        if (!string.IsNullOrWhiteSpace(text))
        {
            ClipboardHelper.SetText(text);
            _notifications.Info("Copied", "Transformed text copied to clipboard.");
        }
    }

    private async Task DeliverTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var destination = _settings.Current.TextDeliveryMode switch
        {
            VoiceTextDeliveryMode.ClipboardOnly => "Clipboard",
            VoiceTextDeliveryMode.AskEveryTime => ChooseDeliveryDestination(),
            _ => "Type Out",
        };
        if (string.IsNullOrWhiteSpace(destination))
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Delivery cancelled", "The transcript was not typed or copied.");
            return;
        }

        var insertionTarget = _capture.CurrentDeliveryTarget;
        if (destination == "Type Out"
            && (insertionTarget is null || !insertionTarget.IsCurrent()))
        {
            await _router.SendAsync("Clipboard", text);
            _notifications.Warning(
                "Focus changed — copied instead",
                "The original field is no longer active, so keyboard.wtf did not type into another app.");
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Done,
                "Copied — focus changed",
                "Return to the intended field and paste when ready.");
            return;
        }

        var ok = await _router.SendAsync(
            destination,
            text,
            insertionTarget,
            cancellationToken);
        if (!ok && destination == "Type Out")
        {
            var copied = await _router.SendAsync("Clipboard", text);
            _notifications.Warning(
                copied ? "Copied instead" : "Delivery failed",
                copied
                    ? "Typing failed, so the transcript was copied to clipboard."
                    : "Typing and clipboard delivery both failed. The transcript remains in keyboard.wtf.");
            KeyboardWtfState.SetUi(
                copied ? VoiceUiPhase.Done : VoiceUiPhase.Error,
                copied ? "Copied instead" : "Delivery failed",
                copied
                    ? "Typing failed, so the final text is on the clipboard."
                    : "The final text remains available in keyboard.wtf.");
        }
        else
        {
            _notifications.Info(ok ? "Done" : "Failed", KeyboardWtfState.LastSendResult ?? destination);
            KeyboardWtfState.SetUi(
                ok ? VoiceUiPhase.Done : VoiceUiPhase.Error,
                ok ? "Done" : "Delivery failed",
                ok ? Shorten(text, 90) : "The transcript remains on the clipboard.");
        }
    }

    private static string ChooseDeliveryDestination()
    {
        DialogResult ShowChoice() => MessageBox.Show(
            "Type this transcript into the active app?\n\nChoose No to copy it to the clipboard instead.",
            "keyboard.wtf delivery",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        var owner = Application.OpenForms.Cast<Form>().FirstOrDefault();
        DialogResult choice;
        if (owner?.InvokeRequired == true)
            choice = (DialogResult)owner.Invoke(ShowChoice);
        else
            choice = ShowChoice();
        return choice switch
        {
            DialogResult.Yes => "Type Out",
            DialogResult.No => "Clipboard",
            _ => "",
        };
    }

    private async Task CancelCurrentAsync()
    {
        _actionCts?.Cancel();
        _capture.Cancel();
        if (_browserAutomation != null)
            await _browserAutomation.CancelAsync("Global Ctrl+Alt+X cancellation");
        _automation?.ObserveUserTranscript("cancel");
        if (_liveConversation?.IsActive == true)
            await _liveConversation.StopAsync();
    }

    private Dictionary<string, object> StartBrowserAutomation(JsonElement args)
    {
        if (_browserAutomation == null)
            return ToolResult(false, "Browser Automation is unavailable.", supported: false);

        var request = ResolveBrowserAutomationTask(
            GetString(args, "task"),
            RecentJarvisUserTurn());
        var result = _browserAutomation.Start(request);
        var lifecycleState = result.Started
            ? "starting"
            : NormalizeBrowserTaskState(_browserAutomation.Snapshot());
        return new Dictionary<string, object>
        {
            ["ok"] = result.Started,
            ["started"] = result.Started,
            ["state"] = lifecycleState,
            ["supported"] = true,
            ["needs_clarification"] = result.NeedsClarification,
            ["needs_consent"] = result.NeedsConsent,
            ["task_id"] = result.TaskId,
            ["message"] = result.Message,
            ["routing_reason"] = result.RoutingReason,
            ["lifecycle_note"] = "This response describes only this invocation. For any later browser request, call browser_automation again; do not assume this task is still active.",
        };
    }

    private string RecentJarvisUserTurn()
    {
        lock (_browserPreRouteLock)
        {
            return DateTime.UtcNow - _latestJarvisUserTurnUtc <= TimeSpan.FromSeconds(60)
                ? _latestJarvisUserTurn
                : "";
        }
    }

    internal static string ResolveBrowserAutomationTask(
        string modelTask,
        string latestUserTurn)
    {
        var model = BrowserAutomationText.Truncate((modelTask ?? "").Trim(), 4000);
        var user = BrowserAutomationText.Truncate((latestUserTurn ?? "").Trim(), 4000);
        if (user.Length == 0)
            return model;

        // The raw user turn is the authoritative task contract. Gemini may
        // summarize a tool argument, and generic summaries such as "Browser
        // automation" lose entities, dates, constraints, and terminal intent.
        // A short context-only acknowledgement is the exception: in that case
        // the model must resolve the referenced task from the conversation.
        if (Regex.IsMatch(
            user,
            @"^\s*(?:yes[, ]*)?(?:please\s+)?(?:do (?:it|that)|go ahead|proceed|continue|resume|same as before|try again)\s*[.!?]*\s*$",
            RegexOptions.IgnoreCase))
        {
            return model;
        }

        return user;
    }

    private bool TryGetPreRoutedToolResult(
        string toolName,
        out Dictionary<string, object> result)
    {
        lock (_browserPreRouteLock)
        {
            var receipt = _browserPreRouteReceipt;
            if (receipt?.AppliesTo(toolName, DateTime.UtcNow) != true)
            {
                result = null;
                return false;
            }

            if (string.Equals(toolName, "browser_automation", StringComparison.Ordinal))
            {
                result = new Dictionary<string, object>
                {
                    ["ok"] = receipt.Ok,
                    ["started"] = receipt.Started,
                    ["state"] = receipt.Started ? "starting" : "idle",
                    ["supported"] = true,
                    ["needs_clarification"] = false,
                    ["task_id"] = receipt.TaskId,
                    ["message"] = receipt.Message,
                    ["routing_reason"] = "The local transcript router already started this exact browser task.",
                    ["lifecycle_note"] = "A browser task is started, not completed. Use browser_automation_status for live progress.",
                };
                return true;
            }

            result = ToolResult(
                receipt.Ok,
                receipt.Message,
                supported: true,
                detail: "The verified local browser command was already executed once for this user turn.");
            return true;
        }
    }

    private Dictionary<string, object> BrowserAutomationStatus()
    {
        if (_browserAutomation == null)
            return ToolResult(false, "Browser Automation is unavailable.", supported: false);

        var status = _browserAutomation.Snapshot();
        var helper = _browserAutomation.HelperStatus;
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["task_id"] = status.TaskId,
            ["state"] = NormalizeBrowserTaskState(status),
            ["detail_state"] = status.State.ToString(),
            ["is_active"] = status.CanCancel,
            ["current_action"] = status.CurrentAction,
            ["current_domain"] = status.CurrentDomain,
            ["current_tab_title"] = status.CurrentTabTitle,
            ["elapsed_seconds"] = status.StartedUtc == default
                ? 0
                : Math.Max(0, (int)(DateTime.UtcNow - status.StartedUtc).TotalSeconds),
            ["can_pause"] = status.CanPause,
            ["can_resume"] = status.CanResume,
            ["can_cancel"] = status.CanCancel,
            ["vision_recovery"] = status.VisionRecovery,
            ["intervention_kind"] = status.InterventionKind,
            ["intervention_reason"] = status.InterventionReason,
            ["result"] = status.ResultSummary,
            ["last_error"] = status.LastError,
            ["helper_connected"] = helper.CommandChannelReady,
        };
    }

    internal static string NormalizeBrowserTaskState(BrowserAutomationSnapshot snapshot)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TaskId))
            return "idle";

        return snapshot.State switch
        {
            BrowserTaskState.Queued => "starting",
            BrowserTaskState.Paused or BrowserTaskState.NeedsUserAction => "paused",
            BrowserTaskState.Completed or BrowserTaskState.CompletedWithNote => "completed",
            BrowserTaskState.Cancelled => "cancelled",
            BrowserTaskState.Partial or BrowserTaskState.Blocked or BrowserTaskState.Failed => "failed",
            _ => "running",
        };
    }

    private async Task<Dictionary<string, object>> ControlBrowserAutomationAsync(JsonElement args)
    {
        if (_browserAutomation == null)
            return ToolResult(false, "Browser Automation is unavailable.", supported: false);

        var action = (GetString(args, "action") ?? "").Trim().ToLowerInvariant();
        var guidance = GetString(args, "guidance");
        var ok = action switch
        {
            "pause" => _browserAutomation.Pause(),
            "resume" => _browserAutomation.Resume(),
            "cancel" => await _browserAutomation.CancelAsync(announceOutcome: false),
            "guide" => AddGuidance(guidance),
            _ => false,
        };
        return new Dictionary<string, object>
        {
            ["ok"] = ok,
            ["message"] = ok
                ? $"Browser automation {action} accepted."
                : "No active browser task accepted that control command.",
            ["status"] = BrowserAutomationStatus(),
        };
    }

    private bool AddGuidance(string guidance)
    {
        if (string.IsNullOrWhiteSpace(guidance))
            return false;
        var accepted = _browserAutomation.TryAddTaskGuidance(guidance, out var reason);
        if (!accepted)
            AppLog.Info($"Browser task guidance rejected: {reason}");
        return accepted;
    }

    private void ResetActionCancellation()
    {
        _actionCts?.Cancel();
        _actionCts?.Dispose();
        _actionCts = new CancellationTokenSource();
    }

    private static Dictionary<string, object> ToolResult(
        bool ok,
        string message,
        bool supported = true,
        bool needsClarification = false,
        string detail = null) => new()
    {
        ["ok"] = ok,
        ["supported"] = supported,
        ["needs_clarification"] = needsClarification,
        ["message"] = message ?? "",
        ["detail"] = detail ?? "",
    };

    private static bool IsSafeHttpUrl(string url, out Uri uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string GetString(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null,
        };
    }

    private static string NormalizeAppName(string appName)
    {
        var text = (appName ?? "").Trim().ToLowerInvariant();
        return text switch
        {
            "calc" or "calculator" => "calculator",
            "mspaint" or "paint" => "paint",
            "explorer" or "file explorer" or "files" or "file manager" => "file explorer",
            "keyboard settings" or "keyboard.wtf settings" or "settings" => "settings",
            "gmail" or "google mail" => "gmail",
            "whatsapp" or "whatsapp web" => "whatsapp",
            "notepad" => "notepad",
            _ => text,
        };
    }

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        var raw = (phoneNumber ?? "").Trim();
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length < 7)
            return "";
        return raw.StartsWith("+", StringComparison.Ordinal) ? "+" + digits : digits;
    }

    private static string HumanizeTool(string toolName) => toolName switch
    {
        "open_app" => "Opening app",
        "open_url" => "Opening website",
        "open_folder" => "Opening folder",
        "open_path" => "Opening file or folder",
        "window_action" => "Controlling window",
        "browser_action" => "Controlling browser",
        "browser_automation" => "Starting browser automation",
        "browser_automation_status" => "Checking browser automation",
        "browser_automation_control" => "Controlling browser automation",
        "web_search" => "Searching the web",
        "get_desktop_context" => "Reading desktop context",
        "get_clipboard_text" => "Reading clipboard",
        "get_selected_text" => "Reading selected text",
        "open_keyboard_wtf_settings" => "Opening keyboard.wtf Settings",
        "replace_selected_text" => "Replacing selected text",
        "type_text" => "Typing text",
        "press_key" => "Pressing key",
        "search_files" => "Searching files",
        "save_note" => "Saving note",
        "add_todo" => "Adding to-do",
        "list_todos" => "Reading to-do list",
        "complete_todo" => "Completing to-do",
        "set_timer" => "Setting timer",
        "cancel_timer" => "Cancelling timer",
        "list_timers" => "Checking timers",
        "system_control" => "Changing system setting",
        "system_status" => "Checking system status",
        "play_media" => "Opening media",
        "open_service_page" => "Opening service page",
        "open_camera" => "Opening camera",
        "take_photo" => "Opening camera for photo",
        "take_screenshot" => "Taking screenshot",
        "inspect_screen" => "Inspecting screen",
        "virtual_desktop_action" => "Controlling virtual desktop",
        "create_workflow" => "Saving workflow",
        "list_workflows" => "Reading workflows",
        "run_workflow" => "Running workflow",
        "delete_workflow" => "Deleting workflow",
        "remember_app_alias" => "Remembering app mapping",
        "remember_path_alias" => "Remembering local path",
        "remember_link_alias" => "Remembering link",
        "remember_workflow_alias" => "Remembering workflow name",
        "list_learned_mappings" => "Reading learned mappings",
        "forget_learned_mapping" => "Forgetting learned mapping",
        "set_browser_preference" => "Saving browser preference",
        "request_sensitive_action" => "Requesting confirmation",
        "confirm_sensitive_action" => "Confirming action",
        "cancel_sensitive_action" => "Cancelling action",
        "open_gmail_draft" => "Preparing Gmail draft",
        "prepare_whatsapp_message" => "Preparing WhatsApp message",
        "copy_text" => "Copying text",
        _ => "Running safe action",
    };

    private void LogJarvisResult(string action, Dictionary<string, object> result)
    {
        if (_history == null)
            return;
        var success = result.TryGetValue("ok", out var ok) && ok is bool value && value;
        var message = result.TryGetValue("message", out var detail) ? detail?.ToString() : "";
        var confirmation = result.TryGetValue("confirmation_required", out var pending)
            && pending is bool required && required;
        _history.Add(action, message, success, confirmation);
    }

    private static string DescribePlan(SmartActionPlan plan) => plan.Action switch
    {
        "gmail_draft" => $"Preparing a Gmail draft for {plan.Recipient}",
        "open_notepad" => "Opening Notepad",
        "open_settings" => "Opening keyboard.wtf settings",
        "open_url" => "Opening a safe web address",
        "copy" => "Copying the prepared text",
        "ask" => "A detail is missing",
        _ => "Preparing text for the active app",
    };

    private static string Shorten(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Ready";
        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..(maxLength - 1)] + "...";
    }

    public void SaveCurrentNote()
    {
        var text = KeyboardWtfState.CurrentTranscript;
        if (string.IsNullOrWhiteSpace(text))
        {
            _notifications.Warning("No transcript", "Nothing to save.");
            return;
        }

        Directory.CreateDirectory(KeyboardWtfState.EffectiveVoiceNoteSavePath);
        var pattern = string.IsNullOrWhiteSpace(KeyboardWtfState.VoiceNoteFilenamePattern)
            ? KeyboardWtfState.DefaultVoiceNoteFilenamePattern
            : KeyboardWtfState.VoiceNoteFilenamePattern;
        var name = DateTime.Now.ToString(pattern);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');
        var path = Path.Combine(KeyboardWtfState.EffectiveVoiceNoteSavePath, name + ".txt");
        File.WriteAllText(path, text);
        _notifications.Info("Voice note saved", path);
    }

    public void ReadBack()
    {
        var text = KeyboardWtfState.CurrentTranscript;
        if (string.IsNullOrWhiteSpace(text))
        {
            _notifications.Warning("No transcript", "Nothing to read back.");
            return;
        }

        TtsService.Instance.Speak(text, KeyboardWtfState.SelectedLanguage);
        _notifications.Info("Reading back", "Speaking the current transcript.");
    }

    public void ReplayLast()
    {
        var last = KeyboardWtfState.TranscriptHistory.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(last))
        {
            _notifications.Warning("No history", "No previous transcript found.");
            return;
        }

        KeyboardWtfState.CurrentTranscript = last;
        ClipboardHelper.SetText(last);
        _notifications.Info("Replayed", "Last transcript copied to clipboard.");
    }

    public void OpenVoiceNotes()
    {
        Directory.CreateDirectory(KeyboardWtfState.EffectiveVoiceNoteSavePath);
        Process.Start(new ProcessStartInfo
        {
            FileName = KeyboardWtfState.EffectiveVoiceNoteSavePath,
            UseShellExecute = true,
        });
    }
}
