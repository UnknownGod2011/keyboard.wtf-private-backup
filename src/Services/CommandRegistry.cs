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
using NAudio.Wave;

public sealed class CommandRegistry
{
    internal static readonly TimeSpan SmartWritingTimeout = TimeSpan.FromSeconds(40);
    internal const int SmartWritingProviderSegmentSeconds = 240;
    internal const string SmartWritingSystemInstruction =
        "You are a high-quality speech-to-writing editor. Listen carefully to the attached speech and return " +
        "only the polished text the user intends to type. Perform a real editorial cleanup rather than a " +
        "near-verbatim transcript. Mandatory cleanup: remove non-semantic fillers, hesitation sounds, and empty " +
        "discourse particles such as um, uh, er, hmm, like, you know, or an opening so/well when they carry no " +
        "meaning; collapse accidental repeated words or phrases; and remove or tighten empty spoken lead-ins. Compress " +
        "wordy speech scaffolding without changing intent—for example, 'I was thinking that we should' becomes 'I " +
        "think we should.' When the speaker " +
        "abandons or corrects wording, discard the abandoned clause and correction markers themselves and retain only " +
        "the final intended version—for example, 'Schedule it—no, scratch that—send it tomorrow' becomes 'Send it " +
        "tomorrow.' Rewrite awkward speech-only sentence structure into natural, grammatically correct written " +
        "language with appropriate punctuation and capitalization. Preserve the complete meaning, tone, substantive " +
        "details, names, technical terminology, URLs, numbers, and intentional emphasis or repetition. Do not " +
        "summarize or omit substantive content. Do not make the writing unnecessarily formal. Do not answer the " +
        "user, explain anything, perform actions, or add commentary. After drafting, perform a separate silent final " +
        "editorial pass over every sentence. Remove any remaining standalone um, uh, erm, hmm, meaningless filler, " +
        "accidental duplicate, abandoned clause, or correction marker unless it is intentional quoted content. Output only the " +
        "final text to insert.";
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
    // One conversation-scoped capture, replaced by every newer explicit
    // selection/clipboard read and cleared when Jarvis ends. It is never saved
    // as memory and is attached to a browser task only when the user asks to
    // use that context (or explicitly asked to retain it for a later action).
    private string _capturedBrowserContextText = "";
    private string _capturedBrowserContextSource = "";
    private string _capturedBrowserContextUserTurn = "";
    private DateTime _capturedBrowserContextUtc;
    private bool _capturedBrowserContextAuthorizedForLaterAction;
    private int _smartWritingOperationId;

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

    public async Task HandleJarvisSessionEndingAsync()
    {
        lock (_browserPreRouteLock)
        {
            _browserPreRouteReceipt = null;
            _browserPreRouteOwnsCurrentTurn = false;
            _latestJarvisUserTurn = "";
            _latestJarvisUserTurnUtc = default;
            _awaitingLocalTextEcho = "";
            _awaitingLocalTextEchoUtc = default;
            ClearCapturedBrowserContextLocked();
        }
        _automation?.ResetConversationTransientContext();
        if (_browserAutomation != null)
        {
            // Conversation shutdown owns the user-visible terminal outcome.
            // Browser cleanup must therefore be silent: otherwise a Partial
            // task can publish a late local "Cancelled" response after the
            // Live socket has already closed (including after a farewell).
            await _browserAutomation.CancelAsync(
                "Jarvis conversation ended",
                announceOutcome: false);
            _browserAutomation.ClearRecentTerminalOutcomes();
        }
    }

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
    public void ObserveJarvisTranscript(string text, CancellationToken token) =>
        RecordJarvisTranscript(text, token, fromLocalTextSubmission: false);

    public void ObserveSubmittedJarvisText(string text, CancellationToken token)
    {
        RecordJarvisTranscript(text, token, fromLocalTextSubmission: true);
        ObserveImmediateJarvisControl(text, token)?.Invoke();
    }

    public void CompleteJarvisUserTurn()
    {
        // End action-routing authority, not deferred selection or diagnostic
        // context. A later tool with no new transcript cannot reuse this turn.
        lock (_browserPreRouteLock)
        {
            _browserPreRouteReceipt = null;
            _browserPreRouteOwnsCurrentTurn = false;
            _latestJarvisUserTurn = "";
            _latestJarvisUserTurnUtc = default;
            _awaitingLocalTextEcho = "";
            _awaitingLocalTextEchoUtc = default;
        }
    }

    // Called on the receive path: only explicit task control is allowed here.
    // The existing coordinator owns cancellation/pause; this closure carries
    // its immutable result into the normal ordered turn/result stream later.
    public Action ObserveImmediateJarvisControl(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var userTurn = (text ?? "").Trim();
        if (!TryControlActiveBrowserTask(userTurn, token, out var result))
            return null;
        return () =>
        {
            token.ThrowIfCancellationRequested();
            _automation?.ObserveUserTranscript(userTurn);
            lock (_browserPreRouteLock)
            {
                _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                _latestJarvisUserTurnUtc = DateTime.UtcNow;
                _awaitingLocalTextEcho = "";
                RecordAuthoritativePreRoute(userTurn, "browser_automation_control", result);
            }
        };
    }

    private bool TryControlActiveBrowserTask(
        string userTurn, CancellationToken token, out Dictionary<string, object> result)
    {
        result = null;
        if (_browserAutomation == null
            || !TryParseExplicitBrowserTaskStopOrPause(userTurn, out var action, out var explicitTarget)
            || !explicitTarget)
            return false;
        var active = _browserAutomation.Snapshot();
        if (!active.CanCancel)
            return false;
        token.ThrowIfCancellationRequested();
        var accepted = action == "pause"
            ? _browserAutomation.Pause()
            : _browserAutomation.RequestCancel(announceOutcome: false);
        result = ToolResult(accepted, accepted
            ? action == "pause" ? "Browser automation paused." : "Browser automation cancelled."
            : "No active browser task accepted that control command.");
        AppLog.Info($"Browser task control accepted by local pre-route: task={active.TaskId} action={action} ok={accepted}");
        return true;
    }

    private void RecordJarvisTranscript(string text, CancellationToken token, bool fromLocalTextSubmission)
    {
        token.ThrowIfCancellationRequested();
        _automation?.ObserveUserTranscript(text);
        var userTurn = (text ?? "").Trim();
        if (userTurn.Length == 0)
            return;

        // Observation updates context, never executes an ordinary action.
        // Keep a completed tool/control receipt across its transcription echo.
        lock (_browserPreRouteLock)
        {
            var now = DateTime.UtcNow;
            if (!fromLocalTextSubmission
                && _browserPreRouteReceipt?.Authoritative == true
                && _browserPreRouteReceipt.ToolName == "browser_automation_control"
                && _browserPreRouteReceipt.IsEchoOf(userTurn, now))
            {
                // A final transcript echo must retain the already-applied
                // control receipt, not turn Cancelled into "no active task".
                _browserPreRouteOwnsCurrentTurn = true;
                _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                _latestJarvisUserTurnUtc = now;
                return;
            }
            if (!fromLocalTextSubmission
                && IsDurableMutationTool(_browserPreRouteReceipt?.ToolName)
                && _browserPreRouteReceipt?.IsIncrementalContinuationOf(userTurn, now) == true)
            {
                _browserPreRouteOwnsCurrentTurn = true;
                _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                _latestJarvisUserTurnUtc = now;
                return;
            }
            if (!fromLocalTextSubmission
                && string.Equals(
                    _browserPreRouteReceipt?.ToolName,
                    "run_workflow",
                    StringComparison.Ordinal)
                && _browserPreRouteReceipt?.IsEchoOf(userTurn, now) == true)
            {
                _browserPreRouteOwnsCurrentTurn = true;
                _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                _latestJarvisUserTurnUtc = now;
                return;
            }
            if (!fromLocalTextSubmission
                && _browserPreRouteReceipt?.IsEchoOf(userTurn, now) == true
                && !string.IsNullOrWhiteSpace(_awaitingLocalTextEcho)
                && now - _awaitingLocalTextEchoUtc <= TimeSpan.FromSeconds(12))
            {
                _awaitingLocalTextEcho = "";
                _browserPreRouteOwnsCurrentTurn = true;
                _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                _latestJarvisUserTurnUtc = now;
                return;
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
    }

    private bool TryResolveAuthoritativeToolRoute(string selectedTool, CancellationToken token)
    {
        // The Live tool dispatcher is the sole ordinary execution owner. A
        // transcript alone must not perform an action whose result the model
        // never receives. Local cancel/farewell remain on the immediate path.
        lock (_browserPreRouteLock)
        {
            if (_browserPreRouteReceipt != null)
                return false;
        }
        var userTurn = RecentJarvisUserTurn();
        var text = userTurn;
        if (userTurn.Length == 0)
            return false;
        token.ThrowIfCancellationRequested();

        // Explicit durable corrections use the existing learned mapping or
        // browser preference store. Check them before workflow syntax because
        // "always open URL when I say X" is a durable target preference, not
        // a trigger-only workflow. Temporary "this time" instructions do not
        // enter this path.
        if (_automation != null
            && _automation.TryApplyDurablePreferenceFromTranscript(
                userTurn,
                out var preferenceTool,
                out var preferenceResult))
        {
            RecordAuthoritativePreRoute(
                userTurn,
                preferenceTool,
                preferenceResult);
            AppLog.Info($"Durable correction pre-route: tool={preferenceTool} ok={ToolSucceeded(preferenceResult)}");
            return true;
        }

        // A selected Link mapping has enough local structure to be owned
        // deterministically: an explicit persistence request, one selected web
        // destination, and one exact future trigger. Keep the existing Link
        // tool as the sole capture/validation/persistence owner instead of
        // relying on optional model tool selection or treating it as a Workflow.
        if (_automation != null
            && JarvisAutomationService.TryParseSelectedLinkPreference(userTurn, out var selectedLinkAlias))
        {
            var selectedLinkResult = RememberLinkAliasAsync(
                    JsonSerializer.SerializeToElement(new { alias = selectedLinkAlias }),
                    token)
                .GetAwaiter()
                .GetResult();
            RecordAuthoritativePreRoute(userTurn, "remember_link_alias", selectedLinkResult);
            AppLog.Info($"Selected Link pre-route: ok={ToolSucceeded(selectedLinkResult)}");
            return true;
        }

        // Explicit persistence requests must reach the workflow tool as one
        // complete user turn. In particular, an early "open Gmail" fragment
        // must not start Browser Automation before "remember this" arrives.
        if (_automation != null
            && JarvisAutomationService.IsExplicitWorkflowPersistenceRequest(userTurn))
        {
            if (_automation.TryRememberWorkflowFromTranscript(
                    userTurn,
                    completeTurn: true,
                    out var saveResult))
            {
                var ok = saveResult.TryGetValue("ok", out var okValue)
                    && okValue is bool succeeded
                    && succeeded;
                var message = saveResult.TryGetValue("message", out var messageValue)
                    ? messageValue?.ToString() ?? ""
                    : "";
                lock (_browserPreRouteLock)
                {
                    _browserPreRouteReceipt = new BrowserPreRouteReceipt
                    {
                        UserTurn = userTurn,
                        ToolName = "remember_routine",
                        Ok = ok,
                        Message = message,
                        Result = new Dictionary<string, object>(saveResult),
                    };
                    _browserPreRouteOwnsCurrentTurn = true;
                }
                AppLog.Info($"Explicit workflow persistence pre-route: ok={ok}");
                return true;
            }
            AppLog.Info("Browser pre-route skipped for explicit workflow persistence request.");
            return false;
        }

        // An ambiguous one-time "open X" request must honor the current
        // durable correction locally. Do not let a model-supplied default
        // native hint override the user's verified saved web/app/path choice.
        // Explicit current app/web wording remains outside this path and keeps
        // its higher precedence.
        if (_automation != null
            && _automation.TryResolveDurableOpenPreference(
                userTurn,
                out var preferenceOpenTool,
                out var preferenceOpenArguments))
        {
            Dictionary<string, object> preferenceOpenResult;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                preferenceOpenResult = _automation.ExecuteAsync(
                        preferenceOpenTool,
                        preferenceOpenArguments,
                        timeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Durable preference open pre-route failed");
                preferenceOpenResult = ToolResult(
                    false,
                    ex is OperationCanceledException
                        ? "The saved preference did not finish in time."
                        : ex.Message);
            }
            token.ThrowIfCancellationRequested();
            RecordAuthoritativePreRoute(
                userTurn,
                preferenceOpenTool,
                preferenceOpenResult);
            AppLog.Info(
                $"Durable preference open pre-route: tool={preferenceOpenTool} " +
                $"ok={ToolSucceeded(preferenceOpenResult)}");
            return true;
        }

        // Mutate the one authoritative saved workflow object in place. This
        // runs before browser classification so adding a website is a durable
        // direct URL action, never a reason to start Browser Automation.
        if (_automation != null
            && _automation.TryEditWorkflowFromTranscript(userTurn, out var editResult))
        {
            token.ThrowIfCancellationRequested();
            RecordAuthoritativePreRoute(
                userTurn,
                "edit_workflow",
                editResult);
            AppLog.Info($"Workflow edit pre-route: ok={ToolSucceeded(editResult)}");
            return true;
        }

        // A complete durable workflow trigger is locally authoritative. Run
        // its stored ordered actions once and retain the verified receipt for
        // the matching Live tool call instead of asking the planner to
        // reinterpret the phrase from conversation memory.
        if (_automation != null
            && _automation.TryResolveSavedWorkflowTrigger(userTurn, out var workflowName))
        {
            Dictionary<string, object> workflowResult;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                workflowResult = _automation.ExecuteAsync(
                        "run_workflow",
                        JsonSerializer.SerializeToElement(new { name = workflowName }),
                        timeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Deterministic saved workflow pre-route failed");
                workflowResult = ToolResult(false, ex is OperationCanceledException
                    ? "The saved workflow did not finish in time."
                    : ex.Message);
            }
            token.ThrowIfCancellationRequested();
            var ok = workflowResult.TryGetValue("ok", out var okValue)
                && okValue is bool succeeded
                && succeeded;
            var message = workflowResult.TryGetValue("message", out var messageValue)
                ? messageValue?.ToString() ?? ""
                : "";
            lock (_browserPreRouteLock)
            {
                _browserPreRouteReceipt = new BrowserPreRouteReceipt
                {
                    UserTurn = userTurn,
                    ToolName = "run_workflow",
                    Ok = ok,
                    Message = message,
                    Result = new Dictionary<string, object>(workflowResult),
                };
                _browserPreRouteOwnsCurrentTurn = true;
            }
            AppLog.Info($"Saved workflow pre-route: workflow={workflowName} ok={ok}");
            return true;
        }

        // An exact deleted trigger is a durable tombstone, not a new web
        // search or planner request. Keep deletion authoritative across new
        // conversations and legacy learned-index fallbacks.
        if (_automation?.IsDeletedWorkflowTrigger(userTurn) == true)
        {
            var deletedResult = ToolResult(
                false,
                "That saved workflow was deleted. Save it again before using this trigger.",
                supported: true);
            lock (_browserPreRouteLock)
            {
                _browserPreRouteReceipt = new BrowserPreRouteReceipt
                {
                    UserTurn = userTurn,
                    ToolName = "run_workflow",
                    Ok = false,
                    Message = deletedResult["message"]?.ToString() ?? "",
                    Result = deletedResult,
                };
                _browserPreRouteOwnsCurrentTurn = true;
            }
            AppLog.Info("Deleted workflow trigger blocked from alternate routing.");
            return true;
        }

        // A known keyboard.wtf Settings route is a complete local action. Run
        // it before browser classification and retain its authoritative receipt
        // so Gemini cannot launch a second browser task for the same turn.
        if (SettingsSectionRegistry.TryResolveKeyboardWtfNavigationRequest(
                userTurn,
                out var settingsSection))
        {
            token.ThrowIfCancellationRequested();
            var settingsResult = OpenKeyboardWtfSettings(settingsSection);
            RecordAuthoritativePreRoute(
                userTurn,
                "open_keyboard_wtf_settings",
                settingsResult);
            AppLog.Info(
                $"Local Settings pre-route: section={settingsSection} ok={ToolSucceeded(settingsResult)}");
            return true;
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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
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
            token.ThrowIfCancellationRequested();

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
            var shortcut = _automation.ExecuteBrowserShortcut(text, token);
            token.ThrowIfCancellationRequested();
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
        if (_browserAutomation.RecentTerminalOutcomes().Count > 0
            && BrowserAutomationTaskClassifier.IsOutcomeDiagnosticQuestion(userTurn))
        {
            var statusResult = BrowserAutomationStatus();
            RecordAuthoritativePreRoute(
                userTurn,
                "browser_automation_status",
                statusResult);
            AppLog.Info(
                "Browser outcome diagnostic pre-route retained read-only status and did not start or revive a task.");
            return true;
        }
        if (active.CanCancel)
        {
            if (TryControlActiveBrowserTask(userTurn, token, out var controlResult))
            {
                RecordAuthoritativePreRoute(
                    userTurn,
                    "browser_automation_control",
                    controlResult);
                return true;
            }

            // Semantic tool selection owns which task receives the turn.
            // A direct or read-only tool must not be reinterpreted as Browser
            // guidance merely because its text overlaps the current page.
            // Explicit cancellation above remains locally authoritative.
            if (selectedTool is not ("browser_automation" or "browser_automation_control"))
                return false;

            // The existing task owner validates the actual user's guidance;
            // a model paraphrase cannot supply extra action authority.
            var guidanceReason = "";
            if (!IsExplicitBrowserTaskStopOrPause(userTurn)
                && _browserAutomation.TryAddTaskGuidance(userTurn, out guidanceReason))
            {
                lock (_browserPreRouteLock)
                {
                    _browserPreRouteReceipt = new BrowserPreRouteReceipt
                    {
                        UserTurn = userTurn,
                        ToolName = "browser_automation_control",
                        Ok = true,
                        TaskId = active.TaskId,
                        Message = "Guidance accepted; the same browser task is continuing.",
                    };
                    _browserPreRouteOwnsCurrentTurn = true;
                }
                AppLog.Info($"Browser task guidance accepted by pre-route: task={active.TaskId}");
                return true;
            }

            if (active.State == BrowserTaskState.NeedsUserAction
                && !IsExplicitBrowserTaskStopOrPause(userTurn)
                && BrowserAutomationCoordinator.IsSkipInterventionGuidance(userTurn))
            {
                var requiredResult = ToolResult(
                    false,
                    string.IsNullOrWhiteSpace(guidanceReason)
                        ? "That browser requirement is not known to be safely optional, so the same task remains paused."
                        : guidanceReason,
                    supported: true);
                RecordAuthoritativePreRoute(
                    userTurn,
                    "browser_automation_control",
                    requiredResult);
                AppLog.Info(
                    $"Browser task required-step skip rejected authoritatively: task={active.TaskId}");
                return true;
            }

            AppLog.Info(
                $"Browser pre-route skipped while task={active.TaskId}: the live conversation decides normal commands; guidanceRejected={BrowserAutomationText.Truncate(guidanceReason, 120)}");
            return false;
        }

        var classification = _browserAutomation.Classify(text);
        if (classification.Decision != BrowserAutomationDecision.BrowserAutomation)
            return false;

        // Context-dependent starts must reach the Browser tool with the
        // captured context; do not start from the bare request before that
        // tool has supplied its authoritative selected/deferred content.
        if (ShouldDeferIncrementalBrowserPreRoute(userTurn, fromLocalTextSubmission: true)
            || ShouldDeferBrowserPreRouteForCapturedContext(
                HasFreshCapturedBrowserContext(),
                userTurn))
        {
            AppLog.Info(
                "Browser pre-route deferred until the complete Live turn so the model can authoritatively decide whether fresh user-provided context belongs to this task.");
            return false;
        }

        token.ThrowIfCancellationRequested();
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

    internal static bool ShouldDeferIncrementalBrowserPreRoute(
        string text,
        bool fromLocalTextSubmission) =>
        BrowserAutomationTaskClassifier.RequiresFreshUserContextCapture(text)
        || (!fromLocalTextSubmission
            && BrowserAutomationTaskClassifier.ShouldDeferIncrementalContentRouting(text));

    internal static bool ShouldDeferBrowserPreRouteForCapturedContext(
        bool contextAvailable,
        string request) =>
        contextAvailable
        && BrowserAutomationTaskClassifier.RequiresPageMutation(request);

    internal static bool IsExplicitBrowserTaskStopOrPause(string text) =>
        TryParseExplicitBrowserTaskStopOrPause(text, out _);

    internal static bool TryParseExplicitBrowserTaskStopOrPause(
        string text,
        out string action) =>
        TryParseExplicitBrowserTaskStopOrPause(
            text,
            out action,
            out _);

    internal static bool TryParseExplicitBrowserTaskStopOrPause(
        string text,
        out string action,
        out bool explicitlyTargetsBrowserTask)
    {
        action = "";
        explicitlyTargetsBrowserTask = false;
        var remaining = (text ?? "").Trim();
        // A bare preliminary control must not consume the explicitly targeted
        // control that follows it. Reuse the same grammar for control clauses;
        // do not search arbitrary quoted/discussed text for a stop keyword.
        for (var clause = 0; clause < 8 && remaining.Length > 0; clause++)
        {
            var match = Regex.Match(
                remaining,
                @"^\s*(?:(?:okay|ok|alright|yes|no)[,;:.!\s]+)?"
                + @"(?:(?:please\s+)|(?:(?:can|could|would|will)\s+you\s+)|(?:i\s+(?:want|need|would\s+like)\s+you\s+to\s+))*"
                + @"(?<action>pause|cancel|stop)"
                + @"(?<target>\s+(?:the\s+)?(?:browser\s+automation|browser\s+task|automation\s+task|task))?"
                + @"(?:\s+(?:(?:right\s+)?now|immediately))?"
                + @"(?:\s*(?:(?:[,;:.!?]|\band\b|\bthen\b)\s*)+(?<remaining>.*))?$",
                RegexOptions.IgnoreCase);
            if (!match.Success)
                break;
            action = match.Groups["action"].Value.Equals(
                "pause",
                StringComparison.OrdinalIgnoreCase)
                    ? "pause"
                    : "cancel";
            explicitlyTargetsBrowserTask = match.Groups["target"].Success;
            if (explicitlyTargetsBrowserTask)
                return true;
            remaining = match.Groups["remaining"].Value.Trim();
        }
        return action.Length > 0;
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
                _notifications.Outcome(
                    ok,
                    "Sent",
                    "Send failed",
                    KeyboardWtfState.LastSendResult ?? destination);
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
            // Exact tab handles are already semantically resolved. Do not
            // reinterpret that set as a foreground close-tab shortcut.
            var hasTabTargets = toolName == "browser_action" && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("target_ids", out _);
            if (!hasTabTargets)
                TryResolveAuthoritativeToolRoute(toolName, token);
            if (TryGetPreRoutedToolResult(toolName, out var preRoutedResult))
                return preRoutedResult;

            if (TryConsumePreRoutedAuthoritativeOutcome(out var authoritativeOutcome))
                return authoritativeOutcome;

            if (ShouldBlockCompetingToolForPreRoutedBrowserTurn(toolName))
            {
                return ToolResult(
                    false,
                    "This user turn is already owned by Browser Automation. No generic active-window or desktop action was sent.");
            }

            if (toolName == "browser_automation")
                return await StartBrowserAutomationAsync(args, token);
            if (toolName == "browser_automation_status")
                return BrowserAutomationStatus();
            if (toolName == "browser_automation_control")
                return await ControlBrowserAutomationAsync(args);
            if (toolName == "remember_link_alias")
                return await RememberLinkAliasAsync(args, token);
            if (toolName == "open_keyboard_wtf_settings")
                return OpenKeyboardWtfSettings(GetString(args, "section"));
            if (toolName == "browser_action"
                && !hasTabTargets
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

            if (_automation == null)
                return ToolResult(false, $"{toolName} is not a supported keyboard.wtf action yet.", supported: false);

            var result = await _automation.ExecuteAsync(toolName, args, token);
            if (toolName is "get_selected_text" or "get_clipboard_text")
                RecordCapturedBrowserContext(toolName, result);
            return result;
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

            return _browserPreRouteReceipt?.BlocksCompetingTool(toolName, DateTime.UtcNow, ownsCurrentTurn: true)
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
        KeyboardWtfState.SetUi(VoiceUiPhase.Thinking, "Understanding command", "Planning the requested action...");

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
        var actionToken = _actionCts.Token;
        var ownedOperationId = Interlocked.Increment(ref _smartWritingOperationId);
        var operation = Guid.NewGuid().ToString("N")[..12];
        var operationStarted = Stopwatch.GetTimestamp();
        long polishMs = 0;
        long insertionMs = 0;
        var usedLocalRecovery = false;
        var partialRecovery = false;
        try
        {
            KeyboardWtfState.IsProcessingAi = true;
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Thinking,
                "Polishing",
                "Polishing your text...");
            AppLog.Info(
                $"Smart Writing stage: operation={operation} stage=audio_ready recordingMs={capture.RecordingDurationMs} bytes={capture.WavData.Length}");
            var polishStarted = Stopwatch.GetTimestamp();
            var segments = SplitSmartWritingAudio(capture.WavData);
            var polishedSegments = new List<string>(segments.Count);
            Exception providerFailure = null;
            for (var index = 0; index < segments.Count; index++)
            {
                actionToken.ThrowIfCancellationRequested();
                ThrowIfSmartWritingOperationWasReplaced(ownedOperationId, actionToken);
                var segment = segments[index];
                var timeoutLimit = SmartWritingTimeoutFor(segment.RecordingDurationMs);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(actionToken);
                timeout.CancelAfter(timeoutLimit);
                try
                {
                    var segmentResult = await paidProvider.ReformatAudioAsync(
                        segment.WavData,
                        SmartWritingSystemInstruction,
                        KeyboardWtfState.SelectedLanguage,
                        timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    ThrowIfSmartWritingOperationWasReplaced(ownedOperationId, actionToken);
                    segmentResult = FinalizeSmartWritingText(segmentResult);
                    if (string.IsNullOrWhiteSpace(segmentResult))
                        throw new InvalidOperationException("No text was returned for an audio segment.");
                    polishedSegments.Add(segmentResult);
                    AppLog.Info(
                        $"Smart Writing stage: operation={operation} stage=segment_completed " +
                        $"segment={index + 1}/{segments.Count} recordingMs={segment.RecordingDurationMs} " +
                        $"characters={segmentResult.Length}");
                }
                catch (OperationCanceledException) when (
                    actionToken.IsCancellationRequested || !IsCurrentSmartWritingOperation(ownedOperationId))
                {
                    throw;
                }
                catch (Exception ex)
                {
                    providerFailure = ex;
                    AppLog.Warning(
                        ex,
                        $"Smart Writing provider segment failed: operation={operation} " +
                        $"segment={index + 1}/{segments.Count} limitMs={(long)timeoutLimit.TotalMilliseconds}");
                    break;
                }
            }

            string result;
            if (providerFailure is null)
            {
                result = JoinSmartWritingSegments(polishedSegments);
            }
            else
            {
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Transcribing,
                    "Preserving your speech",
                    "The cloud polish stopped; recovering the complete recording locally.");
                string localTranscript = "";
                try
                {
                    localTranscript = await _capture.TranscribeCapturedAudioAsync(
                        capture.WavData,
                        preferFast: false,
                        actionToken,
                        allowAiTranslation: false);
                }
                catch (OperationCanceledException) when (actionToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, $"Smart Writing local recovery failed: operation={operation}");
                }

                result = ResolveSmartWritingRecoveryText(
                    localTranscript,
                    polishedSegments,
                    out partialRecovery);
                usedLocalRecovery = !string.IsNullOrWhiteSpace(localTranscript);
                if (string.IsNullOrWhiteSpace(result))
                    throw new InvalidOperationException(
                        "Smart Writing could not recover usable text from the captured audio.",
                        providerFailure);
            }

            polishMs = (long)Stopwatch.GetElapsedTime(polishStarted).TotalMilliseconds;
            AppLog.Info(
                $"Smart Writing stage: operation={operation} stage=audio_polish_completed " +
                $"elapsedMs={polishMs} characters={result?.Length ?? 0} segments={segments.Count} " +
                $"localRecovery={usedLocalRecovery} partialRecovery={partialRecovery}");
            actionToken.ThrowIfCancellationRequested();
            ThrowIfSmartWritingOperationWasReplaced(ownedOperationId, actionToken);
            result = FinalizeSmartWritingText(result);
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("No text was returned to insert.");
            KeyboardWtfState.SetTranscript(result);
            var insertionStarted = Stopwatch.GetTimestamp();
            await DeliverTextAsync(result, actionToken, retainFinalTextOnClipboard: true);
            insertionMs = (long)Stopwatch.GetElapsedTime(insertionStarted).TotalMilliseconds;
            if (partialRecovery)
            {
                _notifications.Warning(
                    "Smart Writing partially recovered",
                    "Inserted the usable completed portion. The unprocessed tail remains unavailable.");
            }
            else if (usedLocalRecovery)
            {
                _notifications.Warning(
                    "Smart Writing recovered",
                    "Cloud polishing stopped, so the complete captured speech was transcribed locally and inserted.");
            }
            AppLog.Info(
                $"Smart Writing audio timing: operation={operation} recordingMs={capture.RecordingDurationMs} " +
                $"polishMs={polishMs} insertionMs={insertionMs} totalAfterRecordingMs=" +
                $"{(long)Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds}");
        }
        catch (OperationCanceledException) when (
            actionToken.IsCancellationRequested || !IsCurrentSmartWritingOperation(ownedOperationId))
        {
            AppLog.Info($"Smart Writing stage: operation={operation} stage=cancelled");
            if (IsCurrentSmartWritingOperation(ownedOperationId))
                KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "Smart writing was stopped.");
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Paid audio Smart Writing failed; no text was inserted");
            if (IsCurrentSmartWritingOperation(ownedOperationId))
            {
                KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Smart Writing unavailable", ex.Message);
                _notifications.Warning("Smart Writing unavailable", ex.Message);
            }
        }
        finally
        {
            if (IsCurrentSmartWritingOperation(ownedOperationId))
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
    }

    internal static TimeSpan SmartWritingTimeoutFor(long recordingDurationMs) =>
        TimeSpan.FromSeconds(Math.Clamp(40d + recordingDurationMs / 20_000d, 40d, 55d));

    internal static IReadOnlyList<SmartWritingAudioCapture> SplitSmartWritingAudio(byte[] wavData)
    {
        if (wavData is null || wavData.Length < 44)
            throw new ArgumentException("Smart Writing audio is empty.", nameof(wavData));

        using var input = new MemoryStream(wavData, writable: false);
        using var reader = new WaveFileReader(input);
        var format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm
            || format.SampleRate != 16_000
            || format.Channels != 1
            || format.BitsPerSample != 16)
        {
            throw new InvalidDataException("Smart Writing audio must be 16 kHz mono 16-bit PCM WAV.");
        }

        var maximumPcmBytes = checked(format.AverageBytesPerSecond * SmartWritingProviderSegmentSeconds);
        maximumPcmBytes -= maximumPcmBytes % format.BlockAlign;
        var copyBuffer = new byte[64 * 1024];
        var segments = new List<SmartWritingAudioCapture>();
        while (true)
        {
            var written = 0;
            byte[] segmentBytes;
            using (var output = new MemoryStream())
            {
                using (var writer = new WaveFileWriter(output, format))
                {
                    while (written < maximumPcmBytes)
                    {
                        var requested = Math.Min(copyBuffer.Length, maximumPcmBytes - written);
                        requested -= requested % format.BlockAlign;
                        var read = reader.Read(copyBuffer, 0, requested);
                        if (read <= 0)
                            break;
                        writer.Write(copyBuffer, 0, read);
                        written += read;
                    }
                }
                segmentBytes = output.ToArray();
            }

            if (written == 0)
                break;
            var durationMs = Math.Max(
                1,
                (long)Math.Round(written * 1000d / format.AverageBytesPerSecond));
            segments.Add(new SmartWritingAudioCapture(segmentBytes, durationMs));
        }

        if (segments.Count == 0)
            throw new InvalidDataException("Smart Writing audio contains no PCM samples.");
        return segments;
    }

    internal static string JoinSmartWritingSegments(IEnumerable<string> segments) =>
        string.Join(
            Environment.NewLine + Environment.NewLine,
            (segments ?? Array.Empty<string>())
                .Select(FinalizeSmartWritingText)
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    internal static string ResolveSmartWritingRecoveryText(
        string localTranscript,
        IReadOnlyList<string> completedSegments,
        out bool partial)
    {
        var local = FinalizeSmartWritingText(localTranscript);
        if (!string.IsNullOrWhiteSpace(local))
        {
            partial = false;
            return local;
        }

        var completed = JoinSmartWritingSegments(completedSegments);
        partial = !string.IsNullOrWhiteSpace(completed);
        return completed;
    }

    internal static string FinalizeSmartWritingText(string value) =>
        SmartWritingFinalEditor.Clean(value);

    private async Task HandleSmartWritingTranscriptAsync(string transcript)
    {
        ResetActionCancellation();
        var actionToken = _actionCts.Token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(actionToken);
        timeout.CancelAfter(SmartWritingTimeout);
        var token = timeout.Token;
        var ownedOperationId = Interlocked.Increment(ref _smartWritingOperationId);
        var operation = Guid.NewGuid().ToString("N")[..12];
        var operationStarted = Stopwatch.GetTimestamp();
        long polishMs = 0;
        long insertionMs = 0;
        var deliveryStarted = false;
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
            ThrowIfSmartWritingOperationWasReplaced(ownedOperationId, token);
            var deliveryText = FinalizeSmartWritingText(
                string.IsNullOrWhiteSpace(result) ? transcript : result);
            var insertionStarted = Stopwatch.GetTimestamp();
            AppLog.Info($"Smart Writing stage: operation={operation} stage=insertion_started");
            deliveryStarted = true;
            await DeliverTextAsync(
                deliveryText,
                actionToken,
                retainFinalTextOnClipboard: true);
            insertionMs = (long)Stopwatch.GetElapsedTime(insertionStarted).TotalMilliseconds;
            AppLog.Info(
                $"Smart Writing timing: operation={operation} recordingMs={_capture.LastRecordingDurationMs} " +
                $"transcriptionMs={_capture.LastTranscriptionDurationMs} polishMs={polishMs} " +
                $"insertionMs={insertionMs} totalAfterTranscriptMs=" +
                $"{(long)Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds}");
        }
        catch (OperationCanceledException) when (
            actionToken.IsCancellationRequested || !IsCurrentSmartWritingOperation(ownedOperationId))
        {
            AppLog.Info($"Smart Writing stage: operation={operation} stage=cancelled");
            if (IsCurrentSmartWritingOperation(ownedOperationId))
                KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "Smart writing was stopped.");
        }
        catch (OperationCanceledException)
        {
            AppLog.Warning(
                $"Smart Writing stage: operation={operation} stage=timeout limitMs={(long)SmartWritingTimeout.TotalMilliseconds}");
            if (!deliveryStarted)
                await DeliverSmartWritingTranscriptRecoveryAsync(
                    transcript,
                    ownedOperationId,
                    actionToken,
                    "Smart Writing timed out",
                    "Polishing took too long, so keyboard.wtf preserved and delivered the captured transcript without the final AI edit.");
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Smart Writing failed");
            if (!deliveryStarted)
            {
                await DeliverSmartWritingTranscriptRecoveryAsync(
                    transcript,
                    ownedOperationId,
                    actionToken,
                    "AI polish unavailable",
                    "keyboard.wtf preserved and delivered the captured transcript without the final AI edit.");
            }
            else if (IsCurrentSmartWritingOperation(ownedOperationId))
            {
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Error,
                    "Smart Writing delivery failed",
                    "The final text remains available in keyboard.wtf and on the clipboard when Windows allowed it.");
                _notifications.Warning(
                    "Smart Writing delivery failed",
                    "The final text remains available in keyboard.wtf and on the clipboard when Windows allowed it.");
            }
        }
        finally
        {
            // Every Smart Writing operation owns one terminal UI transition.
            // This guards against a completed provider/delivery path leaving
            // the stopwatch overlay in Polishing indefinitely.
            if (IsCurrentSmartWritingOperation(ownedOperationId)
                && NeedsSmartWritingTerminalCleanup(
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

    private async Task DeliverSmartWritingTranscriptRecoveryAsync(
        string transcript,
        int ownedOperationId,
        CancellationToken actionToken,
        string title,
        string message)
    {
        actionToken.ThrowIfCancellationRequested();
        ThrowIfSmartWritingOperationWasReplaced(ownedOperationId, actionToken);
        var recovery = ResolveSmartWritingTranscriptRecovery(transcript);
        if (string.IsNullOrWhiteSpace(recovery))
            throw new InvalidOperationException("No captured transcript was available to recover.");

        KeyboardWtfState.SetTranscript(recovery);
        await DeliverTextAsync(
            recovery,
            actionToken,
            retainFinalTextOnClipboard: true);
        _notifications.Warning(title, message);
    }

    internal static string ResolveSmartWritingTranscriptRecovery(string transcript) =>
        FinalizeSmartWritingText(transcript);

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
                    "The selected text is not a valid web link. Select one URL or hostname and try again.",
                    supported: false);
            }
            args = JsonSerializer.SerializeToElement(new { alias, url });
        }
        else if (!BrowserLauncherService.TryNormalizeUrl(url, out var normalizedUrl))
        {
            return ToolResult(
                false,
                "The supplied destination is not a valid web link.",
                supported: false);
        }
        else
        {
            args = JsonSerializer.SerializeToElement(new { alias, url = normalizedUrl.AbsoluteUri });
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
        return BrowserLauncherService.TryNormalizeUrl(text, out var uri)
            ? uri.AbsoluteUri
            : "";
    }

    internal static bool NeedsSmartWritingTerminalCleanup(
        VoiceUiPhase phase,
        string title) =>
        phase == VoiceUiPhase.Thinking
        && string.Equals(title, "Polishing", StringComparison.Ordinal);

    private bool IsCurrentSmartWritingOperation(int operationId) =>
        Volatile.Read(ref _smartWritingOperationId) == operationId;

    private void ThrowIfSmartWritingOperationWasReplaced(
        int operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentSmartWritingOperation(operationId))
            throw new OperationCanceledException("A newer Smart Writing operation replaced this result.");
    }

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
        _notifications.Outcome(
            ok,
            "Sent",
            "Send failed",
            KeyboardWtfState.LastSendResult ?? destination);
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
        CancellationToken cancellationToken = default,
        bool retainFinalTextOnClipboard = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (retainFinalTextOnClipboard
            && !EnsureFinalTextOnClipboard(text))
        {
            AppLog.Warning("Smart Writing could not place the final text on the clipboard before delivery");
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Error,
                "Clipboard unavailable",
                "The final text remains visible in keyboard.wtf. Please try again.");
            _notifications.Warning(
                "Clipboard unavailable",
                "Smart Writing could not safely copy or paste the final text.");
            return;
        }
        if (retainFinalTextOnClipboard)
            AppLog.Info($"Smart Writing prepared {text.Length} final characters on the clipboard");

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
        if (ok && retainFinalTextOnClipboard)
        {
            if (EnsureFinalTextOnClipboard(text))
            {
                AppLog.Info($"Smart Writing retained {text.Length} final characters on the clipboard");
            }
            else
            {
                AppLog.Warning("Smart Writing inserted the final text but could not retain it on the clipboard");
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Error,
                    "Inserted — clipboard unavailable",
                    "The text was inserted, but Windows did not retain the clipboard copy.");
                _notifications.Warning(
                    "Inserted — clipboard unavailable",
                    "The text was inserted, but Windows did not retain the clipboard copy.");
                return;
            }
        }
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
            _notifications.Outcome(
                ok,
                "Done",
                "Failed",
                KeyboardWtfState.LastSendResult ?? destination);
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

    internal static bool RetainFinalTextOnClipboard(
        string text,
        Func<string, bool> setter = null) =>
        (setter ?? ClipboardHelper.SetText)(text ?? "");

    internal static bool EnsureFinalTextOnClipboard(
        string text,
        Func<string, bool> setter = null,
        Func<string> getter = null)
    {
        text ??= "";
        setter ??= ClipboardHelper.SetText;
        getter ??= ClipboardHelper.GetText;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (setter(text)
                && string.Equals(getter(), text, StringComparison.Ordinal))
                return true;
            if (attempt < 2)
                Thread.Sleep(35 * (attempt + 1));
        }
        return false;
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

    private async Task<Dictionary<string, object>> StartBrowserAutomationAsync(
        JsonElement args,
        CancellationToken token)
    {
        if (_browserAutomation == null)
            return ToolResult(false, "Browser Automation is unavailable.", supported: false);

        var request = ResolveBrowserAutomationTask(
            GetString(args, "task"),
            RecentJarvisUserTurn());
        var classification = _browserAutomation.Classify(request);
        if (classification.Decision != BrowserAutomationDecision.BrowserAutomation)
        {
            AppLog.Info(
                $"Browser tool rejected by authoritative capability routing: decision={classification.Decision} reason={classification.Reason}");
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["started"] = false,
                ["state"] = "idle",
                ["supported"] = false,
                ["needs_clarification"] = classification.Decision == BrowserAutomationDecision.AskUser,
                ["message"] = "Browser Automation was not started because this request can use a direct app, URL, or dedicated capability.",
                ["routing_reason"] = classification.Reason,
            };
        }

        var attachedCapturedContext = TryAttachCapturedBrowserContext(
            request,
            GetBoolean(args, "use_captured_context"),
            out var groundedRequest);
        request = groundedRequest;

        // Selected-context ownership cannot depend on optional model tool
        // order. This existing task-start owner captures the explicitly named
        // source only when no retained context already satisfies the request.
        // A failed capture leaves Browser Automation idle instead of starting
        // a task with silently missing or stale information.
        if (!attachedCapturedContext
            && BrowserAutomationTaskClassifier.RequiresFreshUserContextCapture(request))
        {
            var captureTool = BrowserAutomationTaskClassifier.FreshContextCaptureTool(request);
            if (_automation == null || string.IsNullOrWhiteSpace(captureTool))
                return BrowserContextCaptureRequired();

            var capture = await _automation.ExecuteAsync(
                captureTool,
                JsonSerializer.SerializeToElement(new { }),
                token);
            token.ThrowIfCancellationRequested();
            if (!TryGetCapturedContextText(capture, out var capturedText))
                return BrowserContextCaptureRequired(capture);

            request = BuildBrowserTaskWithCapturedContext(
                request,
                captureTool == "get_selected_text" ? "selected text" : "clipboard text",
                capturedText);
            attachedCapturedContext = true;
            AppLog.Info(
                $"Browser task captured fresh {captureTool} context at its authoritative start boundary; characters={capturedText.Length}");
        }

        token.ThrowIfCancellationRequested();
        var result = _browserAutomation.Start(request);
        if (result.Started && attachedCapturedContext)
            ConsumeCapturedBrowserContext();
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
            RegexOptions.IgnoreCase)
            || BrowserAutomationCoordinator.IsExplicitPageHandoffGuidance(user))
        {
            return model;
        }

        return user;
    }

    private void RecordCapturedBrowserContext(
        string toolName,
        Dictionary<string, object> result)
    {
        if (!TryGetCapturedContextText(result, out var capturedText))
            return;

        var userTurn = RecentJarvisUserTurn();
        lock (_browserPreRouteLock)
        {
            _capturedBrowserContextText = capturedText;
            _capturedBrowserContextSource = toolName == "get_selected_text"
                ? "selected text"
                : "clipboard text";
            _capturedBrowserContextUserTurn = userTurn;
            _capturedBrowserContextUtc = DateTime.UtcNow;
            _capturedBrowserContextAuthorizedForLaterAction =
                BrowserAutomationTaskClassifier.IsDeferredInteractionRequest(userTurn);
        }
        AppLog.Info(
            $"Jarvis captured ephemeral {_capturedBrowserContextSource} for explicit same-conversation use; characters={_capturedBrowserContextText.Length}");
    }

    private bool TryAttachCapturedBrowserContext(
        string request,
        bool currentRequestUsesCapturedContext,
        out string groundedRequest)
    {
        groundedRequest = request ?? "";
        lock (_browserPreRouteLock)
        {
            var fresh = CapturedBrowserContextIsFreshLocked();
            var referencesContext =
                BrowserAutomationTaskClassifier.ReferencesCapturedUserContext(request);
            var capturedForCurrentTurn = string.Equals(
                _capturedBrowserContextUserTurn,
                _latestJarvisUserTurn,
                StringComparison.OrdinalIgnoreCase);
            var pageMutation =
                BrowserAutomationTaskClassifier.RequiresPageMutation(request);
            if (!CanAttachCapturedBrowserContext(
                    fresh,
                    referencesContext,
                    capturedForCurrentTurn,
                    _capturedBrowserContextAuthorizedForLaterAction,
                    pageMutation,
                    currentRequestUsesCapturedContext)
                || !CapturedContextSourceMatchesRequest(
                    request,
                    _capturedBrowserContextSource))
            {
                return false;
            }

            groundedRequest = BuildBrowserTaskWithCapturedContext(
                groundedRequest,
                _capturedBrowserContextSource,
                _capturedBrowserContextText);
            return true;
        }
    }

    internal static bool CanAttachCapturedBrowserContext(
        bool fresh,
        bool requestReferencesContext,
        bool capturedForCurrentTurn,
        bool explicitlyAuthorizedForLaterAction,
        bool requestRequiresPageMutation,
        bool currentRequestUsesCapturedContext = false) =>
        fresh
        && requestRequiresPageMutation
        && ((requestReferencesContext && capturedForCurrentTurn)
            || currentRequestUsesCapturedContext
            || (explicitlyAuthorizedForLaterAction
                && requestReferencesContext));

    internal static bool CapturedContextSourceMatchesRequest(
        string request,
        string capturedSource)
    {
        var requiredTool = BrowserAutomationTaskClassifier.FreshContextCaptureTool(request);
        if (string.IsNullOrWhiteSpace(requiredTool))
            return true;
        var requiredSource = requiredTool == "get_selected_text"
            ? "selected text"
            : "clipboard text";
        return string.Equals(
            requiredSource,
            (capturedSource ?? "").Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildBrowserTaskWithCapturedContext(
        string request,
        string source,
        string context) =>
        BrowserAutomationText.Truncate(
            $"{(request ?? "").Trim()}\n\n"
            + $"AUTHORITATIVE USER-PROVIDED {(source ?? "context").Trim().ToUpperInvariant()} "
            + "(ephemeral for this task; use only supported values and ask for genuinely missing required information):\n"
            + (context ?? "").Trim(),
            10000);

    private bool HasFreshCapturedBrowserContext()
    {
        lock (_browserPreRouteLock)
            return CapturedBrowserContextIsFreshLocked();
    }

    private void ConsumeCapturedBrowserContext()
    {
        lock (_browserPreRouteLock)
            ClearCapturedBrowserContextLocked();
    }

    private bool CapturedBrowserContextIsFreshLocked() =>
        !string.IsNullOrWhiteSpace(_capturedBrowserContextText)
        && DateTime.UtcNow - _capturedBrowserContextUtc <= TimeSpan.FromMinutes(10);

    private void ClearCapturedBrowserContextLocked()
    {
        _capturedBrowserContextText = "";
        _capturedBrowserContextSource = "";
        _capturedBrowserContextUserTurn = "";
        _capturedBrowserContextUtc = default;
        _capturedBrowserContextAuthorizedForLaterAction = false;
    }

    private bool TryGetPreRoutedToolResult(
        string toolName,
        out Dictionary<string, object> result)
    {
        lock (_browserPreRouteLock)
        {
            var receipt = _browserPreRouteReceipt;
            if (receipt?.AppliesTo(toolName, DateTime.UtcNow, _browserPreRouteOwnsCurrentTurn) != true)
            {
                result = null;
                return false;
            }

            if (!receipt.TryAcknowledgeOnce())
            {
                result = SuppressedPreRouteResult(receipt);
                return true;
            }

            if (receipt.Result != null)
            {
                result = new Dictionary<string, object>(receipt.Result);
                return true;
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

    private bool TryConsumePreRoutedAuthoritativeOutcome(
        out Dictionary<string, object> result)
    {
        lock (_browserPreRouteLock)
        {
            var receipt = _browserPreRouteReceipt;
            if (!(receipt?.Authoritative == true
                    || string.Equals(receipt?.ToolName, "run_workflow", StringComparison.Ordinal)
                    || IsDurableMutationTool(receipt?.ToolName))
                || (!_browserPreRouteOwnsCurrentTurn && receipt?.IsFresh(DateTime.UtcNow) != true)
                || receipt.Result == null)
            {
                result = null;
                return false;
            }
            if (!receipt.TryAcknowledgeOnce())
            {
                result = SuppressedPreRouteResult(receipt);
                return true;
            }
            result = new Dictionary<string, object>(receipt.Result)
            {
                ["routing_note"] = string.Equals(receipt.ToolName, "run_workflow", StringComparison.Ordinal)
                    ? "The exact durable workflow trigger was resolved and executed locally; no alternate planner action was run."
                    : IsDurableMutationTool(receipt.ToolName)
                        ? "The explicit durable change was persisted and read back locally; no alternate planner action was run."
                        : "The authoritative local capability completed this turn; no alternate planner action was run.",
            };
            return true;
        }
    }

    private static Dictionary<string, object> SuppressedPreRouteResult(BrowserPreRouteReceipt receipt) =>
        new()
        {
            ["ok"] = receipt?.Ok == true,
            ["supported"] = true,
            ["duplicate_suppressed"] = true,
            ["scheduling"] = "SILENT",
            ["message"] = "",
        };

    private void RecordAuthoritativePreRoute(
        string userTurn,
        string toolName,
        Dictionary<string, object> result)
    {
        var ok = ToolSucceeded(result);
        var message = result?.TryGetValue("message", out var messageValue) == true
            ? messageValue?.ToString() ?? ""
            : "";
        lock (_browserPreRouteLock)
        {
            _browserPreRouteReceipt = new BrowserPreRouteReceipt
            {
                UserTurn = userTurn,
                ToolName = toolName,
                Ok = ok,
                Message = message,
                Result = result == null
                    ? ToolResult(false, "The durable change did not return a result.")
                    : new Dictionary<string, object>(result),
                Authoritative = true,
            };
            _browserPreRouteOwnsCurrentTurn = true;
        }
    }

    internal static bool IsDurableMutationTool(string toolName) =>
        (toolName ?? "").Trim() is
            "remember_routine"
            or "edit_workflow"
            or "remember_app_alias"
            or "remember_path_alias"
            or "remember_link_alias"
            or "forget_learned_mapping"
            or "set_browser_preference";

    private static bool ToolSucceeded(Dictionary<string, object> result) =>
        result?.TryGetValue("ok", out var value) == true
        && value is bool ok
        && ok;

    private Dictionary<string, object> BrowserAutomationStatus()
    {
        if (_browserAutomation == null)
            return ToolResult(false, "Browser Automation is unavailable.", supported: false);

        var status = _browserAutomation.Snapshot();
        var recentOutcomes = _browserAutomation.RecentTerminalOutcomes()
            .Select(outcome => new Dictionary<string, object>
            {
                ["task_id"] = outcome.TaskId,
                ["state"] = NormalizeBrowserTaskState(outcome),
                ["detail_state"] = outcome.State.ToString(),
                ["request"] = BrowserAutomationText.Truncate(outcome.Request, 500),
                ["result"] = BrowserAutomationText.Truncate(outcome.ResultSummary, 700),
                ["last_error"] = BrowserAutomationText.Truncate(outcome.LastError, 700),
                ["updated_utc"] = outcome.UpdatedUtc,
            })
            .ToArray();
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
            ["intervention_explanation"] = status.Intervention?.Explanation ?? "",
            ["intervention_required"] = status.Intervention?.Required ?? false,
            ["intervention_can_skip"] = status.Intervention?.CanSkip ?? false,
            ["result"] = status.ResultSummary,
            ["last_error"] = status.LastError,
            ["recent_outcomes"] = recentOutcomes,
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
        var latestUserTurn = RecentJarvisUserTurn();
        if (!IsModelBrowserTaskControlAuthorized(action, latestUserTurn))
        {
            AppLog.Info(
                $"Browser task model control rejected because the latest user turn did not authorize it: action={action} " +
                $"request={BrowserAutomationText.Truncate(latestUserTurn, 120)}");
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["message"] = "The latest user turn did not request that browser-task control.",
                ["status"] = BrowserAutomationStatus(),
            };
        }

        var ok = action switch
        {
            "pause" => _browserAutomation.Pause(),
            "cancel" => await _browserAutomation.CancelAsync(announceOutcome: false),
            // The user's actual turn is authoritative. A model-generated
            // paraphrase must never turn unrelated conversation into guidance
            // or bypass the coordinator's paused-task relevance checks.
            "resume" or "guide" => AddGuidance(latestUserTurn),
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

    private static Dictionary<string, object> BrowserContextCaptureRequired(
        Dictionary<string, object> capture = null)
    {
        var message = capture != null
            && capture.TryGetValue("message", out var value)
            && !string.IsNullOrWhiteSpace(value?.ToString())
                ? value.ToString()
                : "I couldn't read the selected context for this browser task. Reselect it and try once more.";
        return new Dictionary<string, object>
        {
            ["ok"] = false,
            ["started"] = false,
            ["state"] = "idle",
            ["supported"] = true,
            ["needs_clarification"] = true,
            ["message"] = message,
            ["routing_reason"] = "The requested user-provided context was not available, so no browser task was started.",
        };
    }

    internal static bool TryGetCapturedContextText(
        Dictionary<string, object> result,
        out string text)
    {
        text = "";
        if (result == null
            || !result.TryGetValue("ok", out var ok)
            || ok is not true
            || !result.TryGetValue("text", out var value)
            || string.IsNullOrWhiteSpace(value?.ToString()))
        {
            return false;
        }

        text = BrowserAutomationText.Truncate(value.ToString().Trim(), 6000);
        return text.Length > 0;
    }

    internal static bool IsModelBrowserTaskControlAuthorized(
        string action,
        string latestUserTurn)
    {
        var normalizedAction = (action ?? "").Trim().ToLowerInvariant();
        if (normalizedAction is "pause" or "cancel")
        {
            return TryParseExplicitBrowserTaskStopOrPause(
                    latestUserTurn,
                    out var requestedAction,
                    out var explicitlyTargetsBrowserTask)
                && explicitlyTargetsBrowserTask
                && string.Equals(
                    requestedAction,
                    normalizedAction,
                    StringComparison.Ordinal);
        }

        // Resume and guidance remain subject to the coordinator's existing
        // relevance/intervention policy, but only a real current user turn may
        // be evaluated there. Unknown actions are never authorized.
        return normalizedAction is "resume" or "guide"
            && !string.IsNullOrWhiteSpace(latestUserTurn);
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

    private static bool GetBoolean(JsonElement args, string property) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

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
        "edit_workflow" => "Updating workflow",
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
