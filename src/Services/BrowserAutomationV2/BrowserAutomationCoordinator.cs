namespace KeyboardWtf.Services.BrowserAutomationV2;

using KeyboardWtf.Helpers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using KeyboardWtf.Services.PaidStaging;

public sealed class BrowserAutomationStartResult
{
    public bool Started { get; init; }
    public bool NeedsClarification { get; init; }
    public bool NeedsConsent { get; init; }
    public string TaskId { get; init; } = "";
    public string Message { get; init; } = "";
    public string RoutingReason { get; init; } = "";
}

public sealed class BrowserAutomationCoordinator : IDisposable
{
    private static readonly TimeSpan MaximumTaskRuntime = TimeSpan.FromMinutes(5);

    private sealed class Runtime
    {
        public string TaskId { get; init; } = Guid.NewGuid().ToString("N");
        public string Request { get; set; } = "";
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
        public CancellationTokenSource Cancellation { get; init; } = new();
        public AsyncPauseGate PauseGate { get; } = new();
        public List<string> History { get; } = new();
        public ConcurrentQueue<string> PendingGuidance { get; } = new();
        public List<string> VerifiedCheckpoints { get; } = new();
        public List<BrowserActionReceipt> Receipts { get; } = new();
        public Dictionary<string, int> ActionAttempts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> SemanticActionAttempts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> NoProgressActionStates { get; } = new(StringComparer.Ordinal);
        public HashSet<int> ControlledTabs { get; } = new();
        public bool CurrentPageIntent { get; init; }
        public int InitialTabId { get; set; }
        public bool InitialTabWasProtectedOrBlank { get; set; }
        public bool BootstrapNavigationStabilized { get; set; }
        public bool InitialTabIsEditableDestination { get; set; }
        public bool DestinationTabOpened { get; set; }
        public BrowserTaskState State { get; set; } = BrowserTaskState.Queued;
        public string CurrentAction { get; set; } = "Queued";
        public BrowserPageSnapshot Page { get; set; }
        public BrowserAutomationOutcome? Outcome { get; set; }
        public string ResultSummary { get; set; } = "";
        // Task-scoped only. This is never written to browser workflow memory.
        // It allows a grounded source result to survive a source-to-editor tab
        // transition without treating an old result as evidence for a new task.
        public string PreparedDraft { get; set; } = "";
        public string LastError { get; set; } = "";
        public bool VisionRecovery { get; set; }
        public int RecoveryAttempts { get; set; }
        public int ConsecutiveRejectedStrategies { get; set; }
        public bool UserPaused { get; set; }
        public string CancellationReason { get; set; } = "";
        public BrowserTaskState? LastAnnouncedState { get; set; }
        public bool SuppressCancellationAnnouncement { get; set; }
        public bool MissingTabRecoveryAttempted { get; set; }
        public int GuidanceRevision;
        public int CorrectionRevision;
        public string PendingDestinationCorrectionLabel { get; set; } = "";
        public string PendingDestinationCorrectionUrl { get; set; } = "";
        public string CorrectedDestinationUrl { get; set; } = "";
        public string SupersededDestinationUrl { get; set; } = "";
        public Task Execution { get; set; }
    }

    private readonly SettingsService _settings;
    private readonly NotificationService _notifications;
    private readonly BrowserHelperBridge _bridge;
    private readonly BrowserAutomationPlanner _planner;
    private readonly BrowserAutomationVerifier _verifier = new();
    private readonly BrowserAutomationTaskClassifier _classifier = new();
    private readonly BrowserAutomationSafetyPolicy _safety = new();
    private readonly object _sync = new();
    private Runtime _active;
    private BrowserAutomationSnapshot _last = new();
    private DateTime _lastConsentPromptUtc;
    private bool _disposed;
    private readonly PaidAccountService _paidAccount;

    public BrowserAutomationCoordinator(
        SettingsService settings,
        NotificationService notifications,
        BrowserHelperBridge bridge,
        BrowserAutomationPlanner planner)
        : this(settings, notifications, bridge, planner, null)
    {
    }

    internal BrowserAutomationCoordinator(
        SettingsService settings,
        NotificationService notifications,
        BrowserHelperBridge bridge,
        BrowserAutomationPlanner planner,
        PaidAccountService paidAccount)
    {
        _settings = settings;
        _notifications = notifications;
        _bridge = bridge;
        _planner = planner;
        _paidAccount = paidAccount;
    }

    public event Action<BrowserAutomationSnapshot> StatusChanged;
    public event Action<JarvisLifecycleAnnouncement> AnnouncementRequested;

    public BrowserAutomationTaskClassifier Classifier => _classifier;
    public BrowserHelperStatus HelperStatus => _bridge.Status;

    public BrowserAutomationSnapshot Snapshot()
    {
        lock (_sync)
            return BuildSnapshot(_active) ?? _last;
    }

    public BrowserClassification Classify(string request) => _classifier.Classify(request);

    public async Task<BrowserActionReceipt> ActivateExistingTabAsync(
        string selection,
        CancellationToken token = default)
    {
        var status = _bridge.Status;
        if (!status.Connected
            || !string.Equals(
                status.ServiceWorkerVersion,
                BrowserHelperProtocol.Version,
                StringComparison.Ordinal)
            || status.WindowId is not int windowId)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(status.LastError)
                    ? "Existing Chrome Helper is not connected."
                    : status.LastError);
        }

        return await _bridge.ActivateExistingTabAsync(windowId, selection, token);
    }

    public bool TryAddTaskGuidance(string guidance, out string reason)
    {
        reason = "";
        lock (_sync)
        {
            if (_active == null || IsTerminal(_active.State))
            {
                reason = "There is no active browser task to guide.";
                return false;
            }

            var explicitCorrection = IsExplicitTaskCorrection(guidance);
            if (!IsClearlyTaskRelevantGuidance(guidance)
                && !(_active.State == BrowserTaskState.NeedsUserAction
                    && IsRelevantManualAnswer(_active.CurrentAction, guidance)))
            {
                reason = "That is a separate Jarvis or browser command, not guidance for the active browser task.";
                return false;
            }

            _active.PendingGuidance.Enqueue(
                explicitCorrection
                    ? $"Latest explicit user correction: {guidance.Trim()}"
                    : $"User guidance: {guidance.Trim()}");
            _active.GuidanceRevision++;
            if (explicitCorrection)
                Interlocked.Increment(ref _active.CorrectionRevision);
            var previousDestination = "";
            if (explicitCorrection)
            {
                previousDestination = _active.CorrectedDestinationUrl;
                if (string.IsNullOrWhiteSpace(previousDestination))
                {
                    TryResolveExplicitDestination(
                        _active.Request,
                        out _,
                        out previousDestination);
                }
                _active.Request = BuildCorrectedTaskRequest(_active.Request, guidance);
            }
            if (explicitCorrection
                && TryResolveTaskCorrectionDestination(
                    guidance,
                    out var destinationLabel,
                    out var destinationUrl))
            {
                if (!string.IsNullOrWhiteSpace(previousDestination)
                    && !SameHost(previousDestination, destinationUrl))
                {
                    _active.SupersededDestinationUrl = previousDestination;
                }
                _active.CorrectedDestinationUrl = destinationUrl;
                _active.PendingDestinationCorrectionLabel = destinationLabel;
                _active.PendingDestinationCorrectionUrl = destinationUrl;
            }
            _active.UserPaused = false;
            _active.PauseGate.Resume();
            Publish(_active);
            return true;
        }
    }

    public BrowserAutomationStartResult Start(string request)
    {
        var classification = _classifier.Classify(request);
        AppLog.Info(
            $"Browser routing: decision={classification.Decision} current={classification.RefersToCurrentPage} reason={classification.Reason}");

        if (classification.Decision == BrowserAutomationDecision.SimpleTool)
        {
            return new BrowserAutomationStartResult
            {
                Message = "This request should use the existing fast URL/search/browser tool, not smart browser automation.",
                RoutingReason = classification.Reason,
            };
        }

        if (classification.Decision == BrowserAutomationDecision.AskUser)
        {
            return new BrowserAutomationStartResult
            {
                NeedsClarification = true,
                Message = classification.PrefersSelectedContent
                    ? "Should I answer the selected text, or fill/change the live page?"
                    : "Do you want me to interact with the live webpage, or only open/search it?",
                RoutingReason = classification.Reason,
            };
        }

        if (!_settings.HasBrowserAutomationConsent)
        {
            if (DateTime.UtcNow - _lastConsentPromptUtc > TimeSpan.FromSeconds(20))
            {
                _lastConsentPromptUtc = DateTime.UtcNow;
                WebSettingsService.Instance.OpenInBrowser("#browser-automation");
            }
            return new BrowserAutomationStartResult
            {
                NeedsClarification = true,
                NeedsConsent = true,
                Message = "Review and accept the Browser Automation data disclosure in Settings before the first browser task.",
                RoutingReason = classification.Reason,
            };
        }

        if (!_settings.Current.BrowserAutomationEnabled)
        {
            return new BrowserAutomationStartResult
            {
                Message = "Browser Automation is disabled in Settings.",
                RoutingReason = classification.Reason,
            };
        }

        if (_paidAccount?.Enabled == true)
        {
            if (!_paidAccount.IsSignedIn)
            {
                return new BrowserAutomationStartResult
                {
                    Message = "Sign in with Google in Account & Billing before starting browser automation.",
                    RoutingReason = classification.Reason,
                };
            }
            if (_paidAccount.Account?.AvailableCreditMicros <= 0)
            {
                return new BrowserAutomationStartResult
                {
                    Message = "This account has no usable credits. Open Account & Billing to add credits.",
                    RoutingReason = classification.Reason,
                };
            }
        }

        var helperStatus = _bridge.Status;
        if (!helperStatus.CommandChannelReady
            && !BrowserHelperBridge.CanBootstrapDisposableProtectedTab(helperStatus))
        {
            var error = string.IsNullOrWhiteSpace(helperStatus.LastError)
                ? "Existing Chrome Helper is not connected. Open a normal Chrome page, then use Reconnect Helper in Settings."
                : helperStatus.LastError;
            return new BrowserAutomationStartResult
            {
                Message = error,
                RoutingReason = classification.Reason,
            };
        }

        Runtime runtime;
        lock (_sync)
        {
            if (_active != null
                && !IsTerminal(_active.State)
                && _active.Execution?.IsCompleted == true)
            {
                SetTerminal(
                    _active,
                    BrowserTaskState.Failed,
                    BrowserAutomationOutcome.Failed,
                    "The previous browser task ended without terminal cleanup.",
                    "");
            }
            if (_active != null && !IsTerminal(_active.State))
            {
                return new BrowserAutomationStartResult
                {
                    Message = $"Another browser task is already running: {_active.CurrentAction}",
                    TaskId = _active.TaskId,
                    RoutingReason = classification.Reason,
                };
            }

            runtime = new Runtime
            {
                Request = request.Trim(),
                CurrentPageIntent = classification.RefersToCurrentPage,
                State = BrowserTaskState.Queued,
                CurrentAction = "Queued",
            };
            _active = runtime;
            _planner.BeginTask(runtime.TaskId);
            runtime.Cancellation.CancelAfter(MaximumTaskRuntime);
            Publish(runtime);
            runtime.Execution = Task.Run(
                () => ExecuteAsync(runtime, classification.RefersToCurrentPage),
                runtime.Cancellation.Token);
        }

        return new BrowserAutomationStartResult
        {
            Started = true,
            TaskId = runtime.TaskId,
            Message = "Browser automation started in the existing Chrome session.",
            RoutingReason = classification.Reason,
        };
    }

    public bool Pause()
    {
        lock (_sync)
        {
            if (_active == null || IsTerminal(_active.State))
                return false;
            _active.UserPaused = true;
            _active.PauseGate.Pause();
            SetState(_active, BrowserTaskState.Paused, "Paused by user");
            return true;
        }
    }

    public bool Resume()
    {
        lock (_sync)
        {
            if (_active == null || IsTerminal(_active.State))
                return false;
            _active.UserPaused = false;
            _active.PauseGate.Resume();
            SetState(_active, BrowserTaskState.ReadingPage, "Resuming from the live page");
            return true;
        }
    }

    public async Task<bool> CancelAsync(
        string reason = "Cancelled by user",
        bool announceOutcome = true)
    {
        Runtime runtime;
        lock (_sync)
        {
            runtime = _active;
            if (runtime == null || IsTerminal(runtime.State))
                return false;
            runtime.UserPaused = false;
            runtime.CancellationReason = reason;
            runtime.SuppressCancellationAnnouncement = !announceOutcome;
            SetState(runtime, BrowserTaskState.Cancelling, reason);
            runtime.Cancellation.Cancel();
            runtime.PauseGate.Resume();
        }

        AppLog.Info($"Browser automation cancel requested: task={runtime.TaskId}");
        await _bridge.CancelTaskAsync(runtime.TaskId);
        try
        {
            if (runtime.Execution != null)
                await runtime.Execution.WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch
        {
            // ExecuteAsync owns terminal cleanup and logs.
        }
        return true;
    }

    public void AddGuidance(string guidance)
    {
        TryAddTaskGuidance(guidance, out _);
    }

    private async Task ExecuteAsync(Runtime runtime, bool currentPageIntent)
    {
        var token = runtime.Cancellation.Token;
        BrowserPlannerDecision lastDecision = null;
        try
        {
            _safety.EvaluateRequest(runtime.Request);
            SetState(runtime, BrowserTaskState.ReadingPage, "Connecting to the existing Chrome tab");

            // The active tab is always leased first. For a new-site request the planner
            // opens one task-owned tab from this known same-window context.
            runtime.Page = await _bridge.BeginTaskAsync(runtime.TaskId, useCurrentTab: true, token);
            runtime.InitialTabId = runtime.Page.TabId;
            runtime.InitialTabWasProtectedOrBlank = IsProtectedOrBlankUrl(runtime.Page.Url);
            runtime.InitialTabIsEditableDestination = currentPageIntent
                && runtime.Page.ActiveEditable
                && RequiresDestinationEditorTransfer(runtime.Request);
            AddControlledTab(runtime, runtime.Page);
            runtime.History.Add(currentPageIntent
                ? "Started from the user's active current-page tab."
                : "Started from the active Chrome window; open one task-owned tab if a new site is required.");
            SetState(runtime, BrowserTaskState.Planning, "Planning the next browser step");

            if (TryResolveExplicitDestination(
                    runtime.Request,
                    out var explicitDestinationLabel,
                    out var explicitDestinationUrl))
            {
                var destinationCorrectionRevision = Volatile.Read(
                    ref runtime.CorrectionRevision);
                var destinationEstablished = await EstablishAuthoritativeDestinationAsync(
                    runtime,
                    explicitDestinationLabel,
                    explicitDestinationUrl,
                    "the explicit destination in the original request",
                    token);
                if (!destinationEstablished)
                {
                    if (InFlightResultWasSuperseded(
                            destinationCorrectionRevision,
                            Volatile.Read(ref runtime.CorrectionRevision)))
                    {
                        runtime.History.Add(
                            "Ignored the original destination result because a newer explicit correction is authoritative.");
                    }
                    else
                    {
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Failed,
                            $"The explicitly requested {explicitDestinationLabel} destination could not be established safely.");
                        return;
                    }
                }
            }

            if (currentPageIntent
                && TryBuildDirectActiveCursorPasteAction(
                    runtime.Request,
                    runtime.Page,
                    out var directPaste))
            {
                SetState(runtime, BrowserTaskState.Executing, "Pasting at the verified active editor cursor");
                var pasteReceipt = await ExecutePlannerActionAsync(
                    runtime,
                    directPaste,
                    token);
                runtime.Receipts.Add(pasteReceipt);
                var pasteVerification = _verifier.VerifyAction(
                    directPaste,
                    pasteReceipt);
                runtime.History.Add(pasteVerification.Verified
                    ? $"Direct active-cursor paste verified: {pasteVerification.Evidence}"
                    : $"Direct active-cursor paste did not verify: {pasteVerification.Error}");
                runtime.Page = await _bridge.ObserveAsync(
                    runtime.TaskId,
                    pasteReceipt.TabId ?? runtime.Page.TabId,
                    token);
                AddControlledTab(runtime, runtime.Page);

                if (pasteVerification.Verified)
                {
                    var directDecision = new BrowserPlannerDecision
                    {
                        Action = directPaste,
                        Done = true,
                        CompletionKind = "editor_text",
                        CompletionValue = directPaste.Text,
                        ResultSummary = "The requested text was placed at the active editor cursor.",
                    };
                    var terminal = _verifier.VerifyTerminal(
                        runtime.Request,
                        directDecision,
                        runtime.Page,
                        runtime.Receipts);
                    if (terminal.Verified)
                    {
                        Complete(
                            runtime,
                            BrowserAutomationOutcome.Completed,
                            directDecision.ResultSummary,
                            terminal.Evidence);
                        return;
                    }
                    runtime.History.Add($"Direct active-cursor completion rejected: {terminal.Error}");
                }
            }

            if (currentPageIntent
                && TryBuildDirectCurrentPageAction(
                    runtime.Request,
                    runtime.Page,
                    out var directAction))
            {
                var directControl = runtime.Page.Controls.First(control =>
                    string.Equals(control.Id, directAction.TargetId, StringComparison.Ordinal));
                if (DirectActionAlreadySatisfied(directAction, directControl))
                {
                    Complete(
                        runtime,
                        BrowserAutomationOutcome.Completed,
                        $"The requested {directAction.TargetName} control is already in the requested state.",
                        "Fresh live control state independently verified the request.");
                    return;
                }

                var directSafety = _safety.EvaluateAction(
                    runtime.Request,
                    directAction);
                if (directSafety.MayStart)
                {
                    SetState(
                        runtime,
                        BrowserTaskState.Executing,
                        $"Using the exact visible {directAction.TargetName} control");
                    var directReceipt = await ExecutePlannerActionAsync(
                        runtime,
                        directAction,
                        token);
                    runtime.Receipts.Add(directReceipt);
                    var directVerification = _verifier.VerifyAction(
                        directAction,
                        directReceipt);
                    runtime.History.Add(directVerification.Verified
                        ? $"Direct current-page action verified: {directVerification.Evidence}"
                        : $"Direct current-page action did not verify: {directVerification.Error}");
                    runtime.Page = await _bridge.ObserveAsync(
                        runtime.TaskId,
                        directReceipt.TabId ?? runtime.Page.TabId,
                        token);
                    AddControlledTab(runtime, runtime.Page);

                    if (directVerification.Verified)
                    {
                        Complete(
                            runtime,
                            BrowserAutomationOutcome.Completed,
                            $"The requested {directAction.TargetName} control was activated.",
                            directVerification.Evidence);
                        return;
                    }

                    if (CanExhaustStrategy(directReceipt))
                    {
                        RememberNoProgressStrategy(
                            runtime.NoProgressActionStates,
                            runtime.Page,
                            directAction);
                        runtime.History.Add(
                            $"No-progress ledger: {DescribeAction(directAction)} is exhausted on this unchanged page state; the planner must use a different mechanism.");
                    }
                }
            }

            for (var step = 1; step <= 24; step++)
            {
                token.ThrowIfCancellationRequested();
                await runtime.PauseGate.WaitAsync(token);
                token.ThrowIfCancellationRequested();

                DrainPendingGuidance(runtime);
                if (TryTakePendingDestinationCorrection(
                        runtime,
                        out var correctedDestinationLabel,
                        out var correctedDestinationUrl))
                {
                    var correctionRevision = Volatile.Read(
                        ref runtime.CorrectionRevision);
                    var correctionEstablished = await EstablishAuthoritativeDestinationAsync(
                        runtime,
                        correctedDestinationLabel,
                        correctedDestinationUrl,
                        "the latest explicit user correction",
                        token);
                    if (!correctionEstablished)
                    {
                        if (InFlightResultWasSuperseded(
                                correctionRevision,
                                Volatile.Read(ref runtime.CorrectionRevision)))
                        {
                            runtime.History.Add(
                                "Ignored a corrected destination result because an even newer explicit correction is authoritative.");
                            continue;
                        }
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Failed,
                            $"The corrected {correctedDestinationLabel} destination could not be established safely.");
                        return;
                    }
                    continue;
                }

                SetState(runtime, BrowserTaskState.Planning, $"Planning step {step}");
                var plannerGuidanceRevision = Volatile.Read(ref runtime.GuidanceRevision);
                var plannerCorrectionRevision = Volatile.Read(ref runtime.CorrectionRevision);
                lastDecision = await _planner.PlanAsync(
                    runtime.Request,
                    runtime.Page,
                    BuildPlannerHistory(runtime.History, runtime.VerifiedCheckpoints),
                    _settings.Current.BrowserAutomationModel,
                    token);
                await runtime.PauseGate.WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (plannerGuidanceRevision != Volatile.Read(ref runtime.GuidanceRevision))
                {
                    runtime.History.Add(
                        "Discarded a planner decision because newer explicit task guidance arrived before execution.");
                    continue;
                }
                runtime.History.Add(
                    $"Planner ({_planner.LastModel}) evaluated: {Short(lastDecision.Evaluation, 360)}; next goal: {Short(lastDecision.NextGoal, 240)}");
                CapturePreparedDraft(runtime, lastDecision);

                if (ShouldForceVisualRecovery(
                    runtime.Request,
                    runtime.Page,
                    lastDecision,
                    runtime.VisionRecovery))
                {
                    runtime.History.Add(
                        "No grounded controls were available for the requested page mutation; using one bounded visual recovery before accepting completion or requesting manual help.");
                    lastDecision.Done = false;
                    lastDecision.NeedsUserAction = false;
                    lastDecision.UserAction = "";
                    lastDecision.NextGoal = "Visually inspect the current viewport and map the requested control to a live page element";
                    lastDecision.Action = new BrowserPlannerAction { Type = "vision" };
                }

                if (lastDecision.NeedsUserAction
                    || lastDecision.Action.Type == "needs_user_action")
                {
                    SetState(
                        runtime,
                        BrowserTaskState.NeedsUserAction,
                        string.IsNullOrWhiteSpace(lastDecision.UserAction)
                            ? "Waiting for a required manual step"
                            : lastDecision.UserAction);
                    _notifications.WarningTransient(
                        "Browser automation needs you",
                        runtime.CurrentAction);
                    runtime.PauseGate.Pause();
                    await runtime.PauseGate.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    runtime.Page = await _bridge.ObserveAsync(
                        runtime.TaskId,
                        runtime.Page.TabId,
                        token);
                    runtime.History.Add("User resumed after the manual step; page state was refreshed.");
                    continue;
                }

                if (lastDecision.Done || lastDecision.Action.Type == "done")
                {
                    SetState(runtime, BrowserTaskState.Verifying, "Verifying the requested outcome");
                    runtime.Page = await _bridge.ObserveAsync(
                        runtime.TaskId,
                        runtime.Page.TabId,
                        token);
                    var terminal = _verifier.VerifyTerminal(
                        runtime.Request,
                        lastDecision,
                        runtime.Page,
                        runtime.Receipts);
                    if (terminal.Verified)
                    {
                        Complete(
                            runtime,
                            BrowserAutomationOutcome.Completed,
                            lastDecision.ResultSummary,
                            terminal.Evidence);
                        return;
                    }

                    runtime.RecoveryAttempts++;
                    runtime.History.Add($"Completion rejected by verifier: {terminal.Error}");
                    if (runtime.RecoveryAttempts >= 3)
                    {
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Partial,
                            $"Useful browser progress remains open, but completion could not be verified: {terminal.Error}");
                        return;
                    }
                    continue;
                }

                if (lastDecision.Action.Type == "copy_clipboard")
                {
                    await runtime.PauseGate.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    var prepared = BrowserAutomationText.Truncate(
                        string.IsNullOrWhiteSpace(lastDecision.Action.Text)
                            ? runtime.PreparedDraft
                            : lastDecision.Action.Text.Trim(),
                        8000);
                    if (prepared.Length == 0)
                        throw new InvalidOperationException("The planner provided no result to transfer.");

                    if (RequiresDestinationEditorTransfer(runtime.Request))
                    {
                        runtime.PreparedDraft = prepared;
                        lastDecision.CompletionValue = prepared;
                        runtime.History.Add(
                            $"Prepared an ephemeral source result for the destination editor (length {prepared.Length}); clipboard was not treated as terminal completion.");

                        if (runtime.InitialTabIsEditableDestination
                            && runtime.Page.TabId != runtime.InitialTabId
                            && runtime.ControlledTabs.Contains(runtime.InitialTabId))
                        {
                            SetState(
                                runtime,
                                BrowserTaskState.Executing,
                                "Returning to the task's verified destination editor tab");
                            var activation = await _bridge.ExecuteAsync(
                                new BrowserActionRequest
                                {
                                    TaskId = runtime.TaskId,
                                    Type = "activate_tab",
                                    WindowId = runtime.Page.WindowId,
                                    TabId = runtime.InitialTabId,
                                    ControlId = $"tab:{runtime.InitialTabId}",
                                },
                                token);
                            runtime.Receipts.Add(activation);
                            LogActionReceipt(runtime.TaskId, activation);
                            if (!activation.Ok)
                            {
                                throw new InvalidOperationException(
                                    string.IsNullOrWhiteSpace(activation.Error)
                                        ? "The destination editor tab could not be reactivated."
                                        : activation.Error);
                            }
                            runtime.Page = await _bridge.ObserveAsync(
                                runtime.TaskId,
                                runtime.InitialTabId,
                                token);
                            AddControlledTab(runtime, runtime.Page);
                            runtime.History.Add(
                                "Returned deterministically to the task's original verified editor tab.");
                        }

                        if (runtime.InitialTabIsEditableDestination
                            && runtime.Page.TabId == runtime.InitialTabId
                            && runtime.Page.ActiveEditable)
                        {
                            var transfer = new BrowserPlannerAction
                            {
                                Type = "paste",
                                TargetId = runtime.Page.ActiveEditorId,
                                TargetRole = runtime.Page.ActiveEditorRole,
                                TargetName = runtime.Page.ActiveEditorName,
                                Text = runtime.PreparedDraft,
                            };
                            SetState(
                                runtime,
                                BrowserTaskState.Executing,
                                "Placing the prepared result in the destination editor");
                            var transferReceipt = await ExecutePlannerActionAsync(
                                runtime,
                                transfer,
                                token);
                            runtime.Receipts.Add(transferReceipt);
                            var transferVerification = _verifier.VerifyAction(
                                transfer,
                                transferReceipt);
                            runtime.History.Add(transferVerification.Verified
                                ? $"Destination transfer verified: {transferVerification.Evidence}"
                                : $"Destination transfer not verified: {transferVerification.Error}");
                            runtime.Page = await _bridge.ObserveAsync(
                                runtime.TaskId,
                                transferReceipt.TabId ?? runtime.Page.TabId,
                                token);
                            AddControlledTab(runtime, runtime.Page);
                            if (transferVerification.Verified)
                            {
                                var terminal = _verifier.VerifyTerminal(
                                    runtime.Request,
                                    lastDecision,
                                    runtime.Page,
                                    runtime.Receipts);
                                if (terminal.Verified)
                                {
                                    Complete(
                                        runtime,
                                        BrowserAutomationOutcome.Completed,
                                        string.IsNullOrWhiteSpace(lastDecision.ResultSummary)
                                            ? "The requested result was placed in the destination editor."
                                            : lastDecision.ResultSummary,
                                        terminal.Evidence);
                                    return;
                                }
                                runtime.History.Add(
                                    $"Destination transfer terminal verification rejected: {terminal.Error}");
                            }
                        }

                        SetState(
                            runtime,
                            BrowserTaskState.Planning,
                            "Continuing to the destination editor");
                        continue;
                    }

                    SetState(runtime, BrowserTaskState.Executing, "Copying verified result to clipboard");
                    var before = ClipboardHelper.GetText();
                    if (!ClipboardHelper.SetText(prepared))
                        throw new InvalidOperationException("Windows did not accept the clipboard update.");
                    var after = ClipboardHelper.GetText();
                    if (!string.Equals(after, prepared, StringComparison.Ordinal))
                        throw new InvalidOperationException("Clipboard read-back did not match.");
                    lastDecision.CompletionValue = after;
                    lastDecision.ResultSummary = string.IsNullOrWhiteSpace(lastDecision.ResultSummary)
                        ? "The requested result was copied to the clipboard."
                        : lastDecision.ResultSummary;
                    runtime.History.Add($"Clipboard changed and was read back (previous length {before.Length}, new length {after.Length}).");
                    SetState(runtime, BrowserTaskState.Verifying, "Verifying clipboard");
                    var verified = _verifier.VerifyTerminal(
                        runtime.Request,
                        lastDecision,
                        runtime.Page,
                        runtime.Receipts);
                    if (!verified.Verified)
                        throw new InvalidOperationException(verified.Error);
                    Complete(
                        runtime,
                        BrowserAutomationOutcome.Completed,
                        lastDecision.ResultSummary,
                        verified.Evidence);
                    return;
                }

                if (lastDecision.Action.Type == "vision")
                {
                    runtime.VisionRecovery = true;
                    runtime.RecoveryAttempts++;
                    runtime.History.Add("DOM/accessibility grounding was insufficient; one current-viewport visual recovery was requested.");
                    if (runtime.RecoveryAttempts > 3)
                    {
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Blocked,
                            "The requested visible control could not be grounded after bounded recovery.");
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(lastDecision.Action.TargetId))
                    {
                        var visualAnchor = FindMatchingControl(
                            runtime.Page,
                            lastDecision.Action);
                        if (visualAnchor == null)
                        {
                            throw new InvalidOperationException(
                                "The visual question anchor is no longer present in the live page.");
                        }

                        await runtime.PauseGate.WaitAsync(token);
                        token.ThrowIfCancellationRequested();
                        var anchorReceipt = await FocusControlAsync(
                            runtime.TaskId,
                            runtime.Page,
                            visualAnchor,
                            token);
                        runtime.Receipts.Add(anchorReceipt);
                        if (!anchorReceipt.Ok)
                        {
                            throw new InvalidOperationException(
                                string.IsNullOrWhiteSpace(anchorReceipt.Error)
                                    ? "The visual question could not be brought into view."
                                    : anchorReceipt.Error);
                        }
                        runtime.Page = await _bridge.ObserveAsync(
                            runtime.TaskId,
                            runtime.Page.TabId,
                            token);
                    }

                    // The next planner call receives the same fresh controls plus a
                    // screenshot-bearing action receipt once visual planning is enabled.
                    var capture = await _bridge.ExecuteAsync(
                        new BrowserActionRequest
                        {
                            TaskId = runtime.TaskId,
                            Type = "capture_screenshot",
                            TabId = runtime.Page.TabId,
                        },
                        token);
                    runtime.Receipts.Add(capture);
                    if (!capture.Ok || string.IsNullOrWhiteSpace(capture.ScreenshotDataUrl))
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(capture.Error)
                                ? "The Chrome Helper could not capture the visible tab."
                                : capture.Error);
                    runtime.Page = await _bridge.ObserveAsync(
                        runtime.TaskId,
                        runtime.Page.TabId,
                        token);
                    SetState(runtime, BrowserTaskState.AnalyzingImage, "Analyzing the visible page");
                    var visualDecision = await _planner.PlanWithVisionAsync(
                        runtime.Request,
                        runtime.Page,
                        runtime.History,
                        capture.ScreenshotDataUrl,
                        _settings.Current.BrowserAutomationModel,
                        token);
                    await runtime.PauseGate.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    runtime.History.Add(
                        $"Visual recovery ({_planner.LastModel}) selected: {Short(visualDecision.NextGoal, 260)}");

                    if (visualDecision.NeedsUserAction
                        || visualDecision.Action.Type is "needs_user_action" or "vision")
                    {
                        SetState(
                            runtime,
                            BrowserTaskState.NeedsUserAction,
                            string.IsNullOrWhiteSpace(visualDecision.UserAction)
                                ? "The visual target could not be grounded confidently."
                                : visualDecision.UserAction);
                        runtime.PauseGate.Pause();
                        await runtime.PauseGate.WaitAsync(token);
                        runtime.Page = await _bridge.ObserveAsync(
                            runtime.TaskId,
                            runtime.Page.TabId,
                            token);
                        continue;
                    }

                    if (visualDecision.Done || visualDecision.Action.Type == "done")
                    {
                        runtime.History.Add("Visual analysis found no further action; terminal state will be rechecked from fresh live data.");
                        continue;
                    }

                    var visualSafety = _safety.EvaluateAction(
                        runtime.Request,
                        visualDecision.Action);
                    if (!visualSafety.MayStart)
                    {
                        SetState(
                            runtime,
                            BrowserTaskState.NeedsUserAction,
                            visualSafety.Reason);
                        runtime.PauseGate.Pause();
                        await runtime.PauseGate.WaitAsync(token);
                        runtime.Page = await _bridge.ObserveAsync(
                            runtime.TaskId,
                            runtime.Page.TabId,
                            token);
                        continue;
                    }

                    SetState(
                        runtime,
                        BrowserTaskState.Executing,
                        string.IsNullOrWhiteSpace(visualDecision.NextGoal)
                            ? "Using the visually grounded control"
                            : visualDecision.NextGoal);
                    if (RejectKnownNoProgressStrategy(
                        runtime,
                        visualDecision.Action,
                        "visual"))
                    {
                        if (runtime.ConsecutiveRejectedStrategies >= 3)
                        {
                            Fail(
                                runtime,
                                BrowserAutomationOutcome.Partial,
                                "The browser remains at the last useful state, but the planner could not produce a genuinely different recovery strategy.");
                            return;
                        }
                        continue;
                    }
                    runtime.ConsecutiveRejectedStrategies = 0;
                    var visualReceipt = await ExecutePlannerActionAsync(
                        runtime,
                        visualDecision.Action,
                        token);
                    runtime.Receipts.Add(visualReceipt);
                    var visualVerification = _verifier.VerifyAction(
                        visualDecision.Action,
                        visualReceipt);
                    runtime.History.Add(visualVerification.Verified
                        ? $"Visual action verified: {visualVerification.Evidence}"
                        : $"Visual action not verified: {visualVerification.Error}");
                    if (!visualVerification.Verified
                        && CanExhaustStrategy(visualReceipt))
                    {
                        RememberNoProgressStrategy(
                            runtime.NoProgressActionStates,
                            runtime.Page,
                            visualDecision.Action);
                        runtime.History.Add(
                            $"No-progress ledger: {DescribeAction(visualDecision.Action)} is exhausted on this unchanged page state; the next attempt must change strategy.");
                    }
                    if (!visualVerification.Verified && runtime.RecoveryAttempts >= 3)
                    {
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Partial,
                            $"Visual recovery could not produce a verified page change: {visualVerification.Error}");
                        return;
                    }
                    runtime.Page = await _bridge.ObserveAsync(
                        runtime.TaskId,
                        visualReceipt.TabId ?? runtime.Page.TabId,
                        token);
                    continue;
                }

                TryGroundSearchEnter(
                    runtime.Page,
                    lastDecision.Action,
                    lastDecision.NextGoal);
                var groundedControl = GroundActionIdentity(
                    runtime.Page,
                    lastDecision.Action);
                if (TryGetSatisfiedSearchPrerequisite(
                    runtime.Page,
                    lastDecision.Action,
                    lastDecision.NextGoal,
                    runtime.Receipts,
                    out var satisfiedSearchPrerequisite))
                {
                    runtime.ConsecutiveRejectedStrategies++;
                    runtime.History.Add(
                        $"Stale search prerequisite rejected: {satisfiedSearchPrerequisite} "
                        + "Inspect the current progressed page and complete the remaining user goal without repeating the old search.");
                    SetState(
                        runtime,
                        BrowserTaskState.Planning,
                        "Preserving the verified search result page");
                    if (runtime.ConsecutiveRejectedStrategies >= 3)
                    {
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Partial,
                            "The browser remains on the verified result page, but the planner repeatedly tried to rerun an already completed search prerequisite.");
                        return;
                    }
                    continue;
                }
                if (TryGetMissingRequiredDate(
                    BuildUserConstraintContext(runtime),
                    lastDecision.NextGoal,
                    lastDecision.Action,
                    groundedControl,
                    out var missingDate))
                {
                    SetState(
                        runtime,
                        BrowserTaskState.NeedsUserAction,
                        missingDate);
                    _notifications.WarningTransient(
                        "Browser automation needs you",
                        missingDate);
                    runtime.PauseGate.Pause();
                    await runtime.PauseGate.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    runtime.Page = await _bridge.ObserveAsync(
                        runtime.TaskId,
                        runtime.Page.TabId,
                        token);
                    runtime.History.Add(
                        "User supplied the requested missing date information; the live page was refreshed before continuing.");
                    continue;
                }
                if (TryGetAmbiguousExplicitDateTarget(
                    lastDecision.NextGoal,
                    lastDecision.Action,
                    groundedControl,
                    out var ambiguousDateTarget))
                {
                    runtime.ConsecutiveRejectedStrategies++;
                    runtime.History.Add(
                        $"Grounded date action rejected: {ambiguousDateTarget} "
                        + "Use a date control with full month/year semantics, type the date, navigate the calendar, or use one bounded visual recovery.");
                    SetState(
                        runtime,
                        BrowserTaskState.Planning,
                        "Re-grounding an ambiguous date control");
                    if (runtime.ConsecutiveRejectedStrategies >= 3)
                    {
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Partial,
                            "The browser remains at the last useful state, but the requested date could not be grounded unambiguously.");
                        return;
                    }
                    continue;
                }
                if (groundedControl != null
                    && DirectActionAlreadySatisfied(lastDecision.Action, groundedControl))
                {
                    runtime.History.Add(
                        $"Live checkpoint preserved: {DescribeAction(lastDecision.Action)} "
                        + "is already in the requested state. Continue from here without repeating it.");
                    RememberVerifiedCheckpoint(
                        runtime.VerifiedCheckpoints,
                        lastDecision.Action,
                        lastDecision.NextGoal,
                        new BrowserActionReceipt
                        {
                            Ok = true,
                            Action = lastDecision.Action.Type,
                            TargetId = groundedControl.Id,
                            TargetRole = groundedControl.Role,
                            TargetName = ControlName(groundedControl),
                            ValueMatches = lastDecision.Action.Type is "type" or "paste",
                            SelectedChanged = groundedControl.Selected,
                            CheckedChanged = groundedControl.Checked,
                            WindowId = runtime.Page.WindowId,
                            TabId = runtime.Page.TabId,
                        });
                    SetState(
                        runtime,
                        BrowserTaskState.Planning,
                        "Preserving an already completed browser step");
                    continue;
                }
                if (TryGetNavigationRegression(
                    runtime.Request,
                    runtime.Page,
                    lastDecision.Action,
                    out var navigationRegression))
                {
                    runtime.ConsecutiveRejectedStrategies++;
                    runtime.History.Add(
                        $"Navigation rejected before execution: {navigationRegression} "
                        + "Continue from the current progressed page or choose a genuinely different live control.");
                    SetState(
                        runtime,
                        BrowserTaskState.Planning,
                        "Preserving the current progressed page");
                    if (runtime.ConsecutiveRejectedStrategies >= 3)
                    {
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Partial,
                            "The browser remains at the last useful state, but the planner repeatedly tried to discard current progress.");
                        return;
                    }
                    continue;
                }
                if (IsNavigationToSupersededDestination(
                        lastDecision.Action,
                        runtime.CorrectedDestinationUrl,
                        runtime.SupersededDestinationUrl))
                {
                    runtime.History.Add(
                        "Rejected a stale navigation back to the destination superseded by the latest explicit user correction.");
                    SetState(
                        runtime,
                        BrowserTaskState.Planning,
                        "Keeping the corrected browser destination");
                    continue;
                }
                if (TryGetExplicitConstraintConflict(
                    runtime.Request,
                    lastDecision.Action,
                    groundedControl,
                    out var constraintConflict))
                {
                    runtime.ConsecutiveRejectedStrategies++;
                    runtime.History.Add(
                        $"Grounded action rejected: {constraintConflict} "
                        + "Re-plan from the current live page without undoing verified checkpoints.");
                    SetState(
                        runtime,
                        BrowserTaskState.Planning,
                        "Rejecting a control that contradicts the requested outcome");
                    if (runtime.ConsecutiveRejectedStrategies >= 3)
                    {
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Partial,
                            "The browser remains at the last useful state, but the available grounded actions conflict with an explicit task constraint.");
                        return;
                    }
                    continue;
                }
                var actionSafety = _safety.EvaluateAction(
                    runtime.Request,
                    lastDecision.Action);
                if (!actionSafety.MayStart)
                {
                    if (IsUnrequestedFinalActionRejection(actionSafety))
                    {
                        runtime.ConsecutiveRejectedStrategies++;
                        runtime.History.Add(
                            $"Unrequested final action rejected: {actionSafety.Reason} "
                            + "Do not ask the user to approve an action they did not request. "
                            + "If every requested goal is already satisfied, return done; otherwise choose the next requested grounded action.");
                        SetState(
                            runtime,
                            BrowserTaskState.Planning,
                            "Skipping an unrequested submit or send action");
                        if (runtime.ConsecutiveRejectedStrategies >= 3)
                        {
                            Fail(
                                runtime,
                                BrowserAutomationOutcome.Partial,
                                "Useful browser progress remains open, but the planner repeatedly proposed a final action that the user did not request.");
                            return;
                        }
                        continue;
                    }

                    SetState(
                        runtime,
                        BrowserTaskState.NeedsUserAction,
                        actionSafety.Reason);
                    runtime.PauseGate.Pause();
                    await runtime.PauseGate.WaitAsync(token);
                    runtime.Page = await _bridge.ObserveAsync(
                        runtime.TaskId,
                        runtime.Page.TabId,
                        token);
                    continue;
                }

                if (plannerGuidanceRevision != Volatile.Read(ref runtime.GuidanceRevision))
                {
                    runtime.History.Add(
                        "Discarded a pending browser action because newer explicit task guidance superseded it.");
                    continue;
                }

                SetState(
                    runtime,
                    BrowserTaskState.Executing,
                    string.IsNullOrWhiteSpace(lastDecision.NextGoal)
                        ? $"Executing {lastDecision.Action.Type}"
                        : lastDecision.NextGoal);
                if (RejectKnownNoProgressStrategy(runtime, lastDecision.Action, "planner"))
                {
                    if (runtime.ConsecutiveRejectedStrategies >= 3)
                    {
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Partial,
                            "The browser remains at the last useful state, but the planner could not produce a genuinely different strategy.");
                        return;
                    }
                    continue;
                }
                if (!TryRegisterSemanticActionAttempt(
                    runtime.SemanticActionAttempts,
                    lastDecision.Action,
                    lastDecision.NextGoal,
                    maximumAttempts: 2,
                    out var semanticAttempts))
                {
                    runtime.ConsecutiveRejectedStrategies++;
                    runtime.History.Add(
                        $"Cross-state strategy rejected before execution: {DescribeAction(lastDecision.Action)} "
                        + $"was already attempted {semanticAttempts - 1} times for the same immediate goal across page rerenders. "
                        + "Preserve verified subgoals and choose a genuinely different control or mechanism.");
                    SetState(
                        runtime,
                        BrowserTaskState.Planning,
                        "Choosing a different strategy after repeated behavior");
                    if (runtime.ConsecutiveRejectedStrategies >= 3)
                    {
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Partial,
                            "The browser remains at the last useful state, but the planner exhausted distinct strategies for the same immediate goal.");
                        return;
                    }
                    continue;
                }
                if (!TryRegisterActionAttempt(
                    runtime.ActionAttempts,
                    lastDecision.Action,
                    PageStateIdentity(runtime.Page),
                    lastDecision.NextGoal,
                    maximumAttempts: 3,
                    out var repeatedAttempts))
                {
                    Fail(
                        runtime,
                        BrowserAutomationOutcome.Partial,
                        $"The browser remains at the last useful state, but the planner repeated the same grounded action {repeatedAttempts} times without completing the task.");
                    return;
                }
                runtime.ConsecutiveRejectedStrategies = 0;
                var receipt = await ExecutePlannerActionAsync(runtime, lastDecision.Action, token);
                if (InFlightResultWasSuperseded(
                        plannerCorrectionRevision,
                        Volatile.Read(ref runtime.CorrectionRevision)))
                {
                    runtime.History.Add(
                        "Ignored the in-flight browser action result because a newer explicit correction is authoritative.");
                    continue;
                }
                runtime.Receipts.Add(receipt);
                var actionVerification = _verifier.VerifyAction(lastDecision.Action, receipt);
                runtime.History.Add(actionVerification.Verified
                    ? $"Action verified: {actionVerification.Evidence}"
                    : $"Action not verified: {actionVerification.Error}");
                var hasStrongCheckpoint = actionVerification.Verified
                    && HasStrongCheckpointEvidence(receipt);

                if (!actionVerification.Verified)
                {
                    if (actionVerification.Partial)
                    {
                        runtime.RecoveryAttempts = 0;
                        runtime.History.Add(
                            $"Intermediate progress observed: {actionVerification.Evidence} "
                            + "Refresh state and ground the next control; do not claim the field is complete yet.");
                    }
                    else if (CanExhaustStrategy(receipt))
                    {
                        RememberNoProgressStrategy(
                            runtime.NoProgressActionStates,
                            runtime.Page,
                            lastDecision.Action);
                        runtime.History.Add(
                            $"No-progress ledger: {DescribeAction(lastDecision.Action)} is exhausted on this unchanged page state; the next attempt must change strategy.");
                    }
                    if (!actionVerification.Partial)
                        runtime.RecoveryAttempts++;
                    if (!actionVerification.Partial
                        && runtime.RecoveryAttempts >= 3)
                    {
                        Fail(
                            runtime,
                            BrowserAutomationOutcome.Partial,
                            $"The browser remains at the last useful state, but an action could not be verified: {actionVerification.Error}");
                        return;
                    }
                }
                else
                {
                    runtime.RecoveryAttempts = 0;
                }

                token.ThrowIfCancellationRequested();
                SetState(runtime, BrowserTaskState.WaitingForPage, "Reading the updated page");
                if (ShouldStabilizeProtectedBootstrapNavigation(
                    runtime.InitialTabWasProtectedOrBlank,
                    runtime.BootstrapNavigationStabilized,
                    lastDecision.Action.Type,
                    receipt))
                {
                    runtime.BootstrapNavigationStabilized = true;
                    SetState(
                        runtime,
                        BrowserTaskState.WaitingForPage,
                        "Waiting for the new web page to become ready");
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
                runtime.Page = await _bridge.ObserveAsync(
                    runtime.TaskId,
                    receipt.TabId ?? runtime.Page.TabId,
                    token);
                AddControlledTab(runtime, runtime.Page);
                var persistedInFreshState = FreshStateConfirmsPersistedValue(
                    runtime.Page,
                    lastDecision.Action,
                    receipt);
                if (hasStrongCheckpoint && persistedInFreshState)
                {
                    RememberVerifiedCheckpoint(
                        runtime.VerifiedCheckpoints,
                        lastDecision.Action,
                        lastDecision.NextGoal,
                        receipt);
                }
                else if (actionVerification.Verified && !persistedInFreshState)
                {
                    runtime.RecoveryAttempts++;
                    RememberNoProgressStrategy(
                        runtime.NoProgressActionStates,
                        runtime.Page,
                        lastDecision.Action);
                    runtime.History.Add(
                        "Fresh-state verification rejected a transient field write: the page cleared or changed the requested value after the immediate action receipt. "
                        + "Do not alternate between fields or repeat the same format; use a different supported input strategy or ask for the missing information.");
                    continue;
                }

                // A request containing exactly one explicit control action is
                // complete once that same grounded action produces verified
                // fresh-page evidence. Do not ask the planner for an unrelated
                // follow-up (for example, pressing Enter after a verified icon
                // click) merely because the model omitted an explicit Done.
                if (actionVerification.Verified
                    && IsExplicitSingleControlActionRequest(runtime.Request))
                {
                    var terminal = _verifier.VerifyTerminal(
                        runtime.Request,
                        lastDecision,
                        runtime.Page,
                        runtime.Receipts);
                    if (terminal.Verified)
                    {
                        Complete(
                            runtime,
                            BrowserAutomationOutcome.Completed,
                            string.IsNullOrWhiteSpace(lastDecision.ResultSummary)
                                ? "The requested control action was completed."
                                : lastDecision.ResultSummary,
                            terminal.Evidence);
                        return;
                    }
                }

                // A verified transfer into the requested destination editor is
                // terminal evidence. Do not wait for another planner turn that
                // can overwrite the same editor or consume the step budget.
                if (actionVerification.Verified
                    && IsVerifiedEditorTransferAction(runtime.Request, receipt, runtime.Page))
                {
                    var terminal = _verifier.VerifyTerminal(
                        runtime.Request,
                        lastDecision,
                        runtime.Page,
                        runtime.Receipts);
                    if (terminal.Verified)
                    {
                        Complete(
                            runtime,
                            BrowserAutomationOutcome.Completed,
                            string.IsNullOrWhiteSpace(lastDecision.ResultSummary)
                                ? "The requested text was placed in the destination editor."
                                : lastDecision.ResultSummary,
                            terminal.Evidence);
                        return;
                    }
                }
            }

            Fail(
                runtime,
                BrowserAutomationOutcome.Blocked,
                "The task reached its bounded step limit without verified completion.");
        }
        catch (OperationCanceledException)
        {
            if (string.IsNullOrWhiteSpace(runtime.CancellationReason))
            {
                Fail(
                    runtime,
                    BrowserAutomationOutcome.Failed,
                    "The browser task reached its five-minute safety timeout. Chrome and its tabs were left open.");
                AppLog.Warning($"Browser automation timed out: task={runtime.TaskId}");
            }
            else
            {
                SetTerminal(
                    runtime,
                    BrowserTaskState.Cancelled,
                    BrowserAutomationOutcome.Cancelled,
                    "Browser automation cancelled.",
                    "");
                AppLog.Info($"Browser automation final status cancelled: task={runtime.TaskId}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, $"Browser automation failed: task={runtime.TaskId}");
            Fail(runtime, BrowserAutomationOutcome.Failed, FriendlyError(ex));
        }
        finally
        {
            try { await _bridge.EndTaskAsync(runtime.TaskId, CancellationToken.None); } catch { }
            runtime.VisionRecovery = false;
            Publish(runtime);
        }
    }

    internal static bool TryBuildDirectCurrentPageAction(
        string request,
        BrowserPageSnapshot page,
        out BrowserPlannerAction action)
    {
        action = null;
        var match = Regex.Match(
            request ?? "",
            @"^\s*(?:(?:on|in)\s+(?:this|the)\s+(?:page|tab)\s*,?\s*)?(?:please\s+)?(?<verb>click|press|select|choose|check|uncheck)\s+(?:the\s+)?(?<target>.+?)\s*[.!?]?\s*$",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        var verb = match.Groups["verb"].Value.ToLowerInvariant();
        var target = match.Groups["target"].Value;
        target = Regex.Replace(
            target,
            @"^on\s+(?:the\s+)?",
            "",
            RegexOptions.IgnoreCase);
        target = Regex.Replace(
            target,
            @"\s+and\s+(?:stop|leave|keep)\b.*$",
            "",
            RegexOptions.IgnoreCase);
        target = Regex.Replace(
            target,
            @"\s+(?:on|in)\s+(?:this|the)\s+(?:page|tab)$",
            "",
            RegexOptions.IgnoreCase);
        target = Regex.Replace(
            target,
            @"\s+(?:page\s+)?(?:button|tab|control|option|checkbox|radio)$",
            "",
            RegexOptions.IgnoreCase);
        target = target.Trim().Trim('\'', '"', '.', '!', '?');
        if (target.Length is 0 or > 100 || target.Contains(" and ", StringComparison.OrdinalIgnoreCase))
            return false;

        var normalizedTarget = NormalizeControlText(target);
        var exact = (page?.Controls ?? Array.Empty<BrowserControl>())
            .Where(control => control.Visible && control.Enabled)
            .Where(control => verb is not ("check" or "uncheck")
                || control.Role.Equals("checkbox", StringComparison.OrdinalIgnoreCase)
                || control.Role.Equals("radio", StringComparison.OrdinalIgnoreCase))
            .Where(control => new[]
            {
                control.AccessibleName,
                control.Text,
                control.AriaLabel,
                control.Title,
                control.NearbyLabel,
            }.Any(value => NormalizeControlText(value) == normalizedTarget))
            .GroupBy(control => control.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (exact.Length != 1)
            return false;

        var selected = exact[0];
        action = new BrowserPlannerAction
        {
            Type = verb switch
            {
                "check" => "check",
                "uncheck" => "uncheck",
                _ => "click",
            },
            TargetId = selected.Id,
            TargetRole = selected.Role,
            TargetName = string.IsNullOrWhiteSpace(selected.AccessibleName)
                ? selected.Text
                : selected.AccessibleName,
            CaptureBeforeAction = true,
        };
        return true;
    }

    internal static bool TryBuildDirectActiveCursorPasteAction(
        string request,
        BrowserPageSnapshot page,
        out BrowserPlannerAction action)
    {
        action = null;
        if (page?.ActiveEditable != true
            || string.IsNullOrWhiteSpace(page.ActiveEditorId)
            || !Regex.IsMatch(
                request ?? "",
                @"\b(?:paste|insert|type|write|place|put)\b",
                RegexOptions.IgnoreCase)
            || !Regex.IsMatch(
                request ?? "",
                @"\b(?:current|active)\s+cursor\b|\b(?:this|the|current)\s+(?:document|doc|editor|text\s*box|field)\b",
                RegexOptions.IgnoreCase))
        {
            return false;
        }

        var quoted = Regex.Matches(
                request ?? "",
                "[\"“](?<text>[^\"”]{1,8000})[\"”]")
            .Cast<Match>()
            .LastOrDefault();
        var text = quoted?.Groups["text"].Value ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        action = new BrowserPlannerAction
        {
            Type = "paste",
            TargetId = page.ActiveEditorId,
            TargetRole = page.ActiveEditorRole,
            TargetName = page.ActiveEditorName,
            Text = text,
        };
        return true;
    }

    internal static bool RequiresDestinationEditorTransfer(string request) =>
        Regex.IsMatch(
            request ?? "",
            @"\b(?:paste|insert|type|write|place|put)\b",
            RegexOptions.IgnoreCase)
        && Regex.IsMatch(
            request ?? "",
            @"\b(?:google docs|document|docs|doc|editor|textbox|text area|current cursor|active cursor)\b",
            RegexOptions.IgnoreCase);

    internal static bool TryResolveTaskTabTarget(
        BrowserPlannerAction action,
        IReadOnlySet<int> controlledTabs,
        out int tabId)
    {
        tabId = 0;
        var match = Regex.Match(
            action?.TargetId ?? "",
            @"^\s*tab:(?<id>\d+)\s*$",
            RegexOptions.IgnoreCase);
        return match.Success
            && int.TryParse(match.Groups["id"].Value, out tabId)
            && controlledTabs?.Contains(tabId) == true;
    }

    private static string NormalizeControlText(string value) =>
        Regex.Replace(value ?? "", @"\s+", " ").Trim().ToLowerInvariant();

    internal static IReadOnlyList<string> BuildPlannerHistory(
        IReadOnlyList<string> history,
        IReadOnlyList<string> verifiedCheckpoints)
    {
        var result = (history ?? Array.Empty<string>()).TakeLast(7).ToList();
        if (verifiedCheckpoints?.Count > 0)
        {
            result.Add(
                "PRESERVED VERIFIED CHECKPOINTS — do not repeat, reset, or navigate behind these unless live evidence proves one is wrong: "
                + string.Join(" | ", verifiedCheckpoints.TakeLast(10)));
        }
        return result;
    }

    internal static BrowserControl GroundActionIdentity(
        BrowserPageSnapshot page,
        BrowserPlannerAction action)
    {
        if (page == null || action == null)
            return null;
        if (action.Type is "navigate" or "open_tab" or "activate_tab"
            or "scroll" or "vision" or "done" or "needs_user_action"
            or "copy_clipboard")
        {
            return null;
        }

        var live = FindMatchingControl(page, action);
        if (live == null)
            return null;

        action.TargetId = live.Id;
        if (!string.IsNullOrWhiteSpace(live.Role))
            action.TargetRole = live.Role;
        var liveName = ControlName(live);
        if (!string.IsNullOrWhiteSpace(liveName)
            && (string.IsNullOrWhiteSpace(action.TargetName)
                || liveName.Length >= action.TargetName.Trim().Length))
        {
            action.TargetName = liveName;
        }
        return live;
    }

    internal static bool TryGetExplicitConstraintConflict(
        string request,
        BrowserPlannerAction action,
        BrowserControl control,
        out string reason)
    {
        reason = "";
        var task = NormalizeControlText(request);
        var target = NormalizeControlText(string.Join(
            " ",
            action?.TargetName,
            action?.Option,
            control == null ? "" : ControlName(control),
            control?.NearbyLabel));
        if (task.Length == 0 || target.Length == 0)
            return false;

        var exclusivePairs = new[]
        {
            (Requested: @"\bround[- ]?trip\b", Contradictory: @"\bone[- ]?way\b"),
            (Requested: @"\bone[- ]?way\b", Contradictory: @"\bround[- ]?trip\b"),
            (Requested: @"\b(?:oldest|earliest)\b", Contradictory: @"\b(?:latest|newest|most recent)\b"),
            (Requested: @"\b(?:latest|newest|most recent)\b", Contradictory: @"\b(?:oldest|earliest)\b"),
            (Requested: @"\bascending\b", Contradictory: @"\bdescending\b"),
            (Requested: @"\bdescending\b", Contradictory: @"\bascending\b"),
        };
        foreach (var pair in exclusivePairs)
        {
            if (!Regex.IsMatch(task, pair.Requested, RegexOptions.IgnoreCase)
                || !Regex.IsMatch(target, pair.Contradictory, RegexOptions.IgnoreCase))
            {
                continue;
            }

            reason =
                $"The live target '{BrowserAutomationText.Truncate(target, 140)}' contradicts an explicit task constraint.";
            return true;
        }

        var months = new[]
        {
            "january", "february", "march", "april", "may", "june",
            "july", "august", "september", "october", "november", "december",
        };
        var requestedMonths = months.Where(month =>
            Regex.IsMatch(task, $@"\b{month}\b", RegexOptions.IgnoreCase)).ToArray();
        var targetMonths = months.Where(month =>
            Regex.IsMatch(target, $@"\b{month}\b", RegexOptions.IgnoreCase)).ToArray();
        if (requestedMonths.Length > 0
            && targetMonths.Length > 0
            && !requestedMonths.Intersect(
                targetMonths,
                StringComparer.OrdinalIgnoreCase).Any())
        {
            reason =
                $"The live target '{BrowserAutomationText.Truncate(target, 140)}' names a different month than the task.";
            return true;
        }

        var requestedYears = Regex.Matches(task, @"\b20\d{2}\b")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var targetYears = Regex.Matches(target, @"\b20\d{2}\b")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requestedYears.Length > 0
            && targetYears.Length > 0
            && !requestedYears.Intersect(targetYears, StringComparer.Ordinal).Any())
        {
            reason =
                $"The live target '{BrowserAutomationText.Truncate(target, 140)}' names a different year than the task.";
            return true;
        }

        return false;
    }

    internal static bool TryGetAmbiguousExplicitDateTarget(
        string immediateGoal,
        BrowserPlannerAction action,
        BrowserControl control,
        out string reason)
    {
        reason = "";
        if (action?.Type != "click" || control == null)
            return false;

        var requested = NormalizeControlText(immediateGoal);
        var target = NormalizeControlText(string.Join(
            " ",
            ControlName(control),
            control.NearbyLabel,
            control.Value));
        var explicitDate = Regex.Match(
            requested,
            @"\b(january|february|march|april|may|june|july|august|september|october|november|december)\s+([0-3]?\d)(?:st|nd|rd|th)?(?:,|\s)+\s*(20\d{2})\b",
            RegexOptions.IgnoreCase);
        if (!explicitDate.Success
            || !Regex.IsMatch(target, @"^\s*[0-3]?\d\s*$"))
        {
            return false;
        }

        reason =
            $"The requested date is {explicitDate.Value}, but the live target is only the context-free number '{target}'.";
        return true;
    }

    internal static bool TryGetMissingRequiredDate(
        string userConstraintContext,
        string immediateGoal,
        BrowserPlannerAction action,
        BrowserControl control,
        out string reason)
    {
        reason = "";
        if (action == null || HasUsableDateConstraint(userConstraintContext))
            return false;

        var target = NormalizeControlText(string.Join(
            " ",
            action.TargetRole,
            action.TargetName,
            control == null ? "" : ControlName(control),
            control?.NearbyLabel,
            control?.Placeholder));
        var proposal = NormalizeControlText(string.Join(
            " ",
            immediateGoal,
            action.Text,
            action.Option,
            action.Url));
        var dateTarget = Regex.IsMatch(
            $"{target} {proposal}",
            @"\b(?:date|departure|depart|return|arrival|arrive|check[- ]?in|check[- ]?out|calendar|day|month)\b",
            RegexOptions.IgnoreCase);
        if (!dateTarget || !ContainsConcreteDateValue(proposal))
            return false;

        reason =
            "This task requires a date that was not provided. Tell me the needed date or range (for example, “use August 1 to August 8”), and I’ll continue from this page.";
        return true;
    }

    internal static bool HasUsableDateConstraint(string text)
    {
        var normalized = NormalizeControlText(text);
        if (normalized.Length == 0)
            return false;
        return Regex.IsMatch(
            normalized,
            @"\b(?:january|february|march|april|may|june|july|august|september|october|november|december)\b"
            + @"|\b20\d{2}[-/][01]?\d[-/][0-3]?\d\b"
            + @"|\b[0-3]?\d[-/][01]?\d(?:[-/](?:20)?\d{2})?\b"
            + @"|\b(?:today|tomorrow|tonight|this weekend|next (?:day|week|weekend|month|monday|tuesday|wednesday|thursday|friday|saturday|sunday))\b"
            + @"|\bin \d{1,3} (?:day|days|week|weeks|month|months)\b",
            RegexOptions.IgnoreCase);
    }

    private static bool ContainsConcreteDateValue(string text) =>
        Regex.IsMatch(
            text ?? "",
            @"\b20\d{2}[-/][01]?\d[-/][0-3]?\d\b"
            + @"|\b[0-3]?\d[-/][01]?\d(?:[-/](?:20)?\d{2})?\b"
            + @"|\b(?:january|february|march|april|may|june|july|august|september|october|november|december)\s+[0-3]?\d(?:st|nd|rd|th)?\b",
            RegexOptions.IgnoreCase);

    private static string BuildUserConstraintContext(Runtime runtime)
    {
        var guidance = runtime.History
            .Where(item => item.StartsWith(
                "User guidance:",
                StringComparison.OrdinalIgnoreCase));
        return string.Join("\n", new[] { runtime.Request }.Concat(guidance));
    }

    internal static bool HasStrongCheckpointEvidence(BrowserActionReceipt receipt) =>
        receipt?.Ok == true
        && !receipt.Stale
        && !receipt.NoVisibleChange
        && (receipt.ValueMatches
            || receipt.SelectedChanged
            || receipt.CheckedChanged
            || receipt.Navigation);

    internal static bool FreshStateConfirmsPersistedValue(
        BrowserPageSnapshot page,
        BrowserPlannerAction action,
        BrowserActionReceipt receipt)
    {
        if (action == null
            || receipt?.Ok != true
            || !receipt.ValueMatches
            || action.Type is not ("type" or "paste"))
        {
            return true;
        }

        var live = (page?.Controls ?? Array.Empty<BrowserControl>())
            .FirstOrDefault(control =>
                string.Equals(control.Id, receipt.TargetId, StringComparison.Ordinal))
            ?? FindMatchingControl(page, action);
        if (live != null)
        {
            return EquivalentInputValue(
                live.Value,
                string.IsNullOrWhiteSpace(receipt.ExpectedValue)
                    ? action.Text
                    : receipt.ExpectedValue,
                allowContainedText: !action.Replace || action.Type == "paste");
        }

        // Canvas-backed editors can expose only a task-leased active cursor,
        // not readable document text. A trusted native paste is accepted here
        // only when its atomic copy/read-back receipt still belongs to that
        // same live editor lease.
        if (receipt.NativeInputUsed
            && action.Type == "paste"
            && page?.ActiveEditable == true
            && !string.IsNullOrWhiteSpace(page.ActiveEditorId)
            && string.Equals(
                page.ActiveEditorId,
                receipt.TargetId,
                StringComparison.Ordinal))
        {
            return true;
        }

        // Search submission and navigation commonly replace the input itself.
        // That is a durable state transition, unlike a field that remains live
        // but silently clears after an unsupported format.
        return receipt.Navigation
            || !string.Equals(
                receipt.AfterUrl,
                page?.Url,
                StringComparison.Ordinal);
    }

    private static bool EquivalentInputValue(
        string observed,
        string expected,
        bool allowContainedText)
    {
        var actual = NormalizeControlText(observed);
        var wanted = NormalizeControlText(expected);
        if (wanted.Length == 0)
            return true;
        if (actual.Equals(wanted, StringComparison.Ordinal)
            || (allowContainedText
                && actual.Contains(wanted, StringComparison.Ordinal)))
        {
            return true;
        }

        return DateTime.TryParse(
                observed,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var actualDate)
            && DateTime.TryParse(
                expected,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var wantedDate)
            && actualDate.Date == wantedDate.Date;
    }

    internal static void RememberVerifiedCheckpoint(
        IList<string> checkpoints,
        BrowserPlannerAction action,
        string immediateGoal,
        BrowserActionReceipt receipt)
    {
        if (checkpoints == null || action == null || receipt == null)
            return;
        var identity = ImmediateGoalIdentity(immediateGoal);
        var summary =
            $"{identity}: {BrowserAutomationText.Truncate(immediateGoal, 180)} "
            + $"via {NormalizeControlText(action.Type)} on "
            + $"'{BrowserAutomationText.Truncate(receipt.TargetName, 100)}' "
            + $"(value={receipt.ValueMatches}, selected={receipt.SelectedChanged}, checked={receipt.CheckedChanged}, navigation={receipt.Navigation})";
        if (!string.IsNullOrWhiteSpace(identity))
        {
            for (var index = checkpoints.Count - 1; index >= 0; index--)
            {
                if (checkpoints[index].StartsWith(
                    identity + ":",
                    StringComparison.OrdinalIgnoreCase))
                {
                    checkpoints.RemoveAt(index);
                }
            }
        }
        checkpoints.Add(summary);
        while (checkpoints.Count > 12)
            checkpoints.RemoveAt(0);
    }

    internal static bool DirectActionAlreadySatisfied(
        BrowserPlannerAction action,
        BrowserControl control)
    {
        if (action == null || control == null)
            return false;
        if (action.Type == "check")
            return control.Checked;
        if (action.Type == "uncheck")
            return !control.Checked;
        if (action.Type is "type" or "paste")
            return !string.IsNullOrWhiteSpace(action.Text)
                && NormalizeControlText(control.Value)
                    .Equals(
                        NormalizeControlText(action.Text),
                        StringComparison.Ordinal);
        if (action.Type == "select_option")
        {
            var requested = string.IsNullOrWhiteSpace(action.Option)
                ? action.Text
                : action.Option;
            return !string.IsNullOrWhiteSpace(requested)
                && NormalizeControlText(control.Value)
                    .Equals(
                        NormalizeControlText(requested),
                        StringComparison.Ordinal);
        }
        return action.Type == "click"
            && (control.Selected
                || (control.Role.Equals("radio", StringComparison.OrdinalIgnoreCase)
                    && control.Checked));
    }

    internal static bool ShouldNormalizeChoiceTyping(
        BrowserPlannerAction action,
        BrowserControl control) =>
        action?.Type == "type"
        && control != null
        && !control.Editable
        && !string.IsNullOrWhiteSpace(action.Text)
        && (control.Role.Equals("combobox", StringComparison.OrdinalIgnoreCase)
            || control.Role.Equals("listbox", StringComparison.OrdinalIgnoreCase));

    internal static bool TryNormalizeNativeDateTyping(
        BrowserPlannerAction action,
        BrowserControl control,
        out string normalized)
    {
        normalized = "";
        if (action?.Type is not ("type" or "paste")
            || control == null
            || !control.InputType.Equals("date", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(action.Text))
        {
            return false;
        }

        var explicitDate = Regex.Replace(
                action.Text.Trim().TrimEnd('.', ',', ';', ':'),
                @"(?<=\d)(?:st|nd|rd|th)\b",
                "",
                RegexOptions.IgnoreCase)
            .Trim();
        var formats = new[]
        {
            "yyyy-MM-dd",
            "MMMM d yyyy",
            "MMMM d, yyyy",
            "MMM d yyyy",
            "MMM d, yyyy",
            "d MMMM yyyy",
            "d MMM yyyy",
        };
        if (!DateTime.TryParseExact(
                explicitDate,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var date))
        {
            return false;
        }

        normalized = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    private async Task<BrowserActionReceipt> ExecutePlannerActionAsync(
        Runtime runtime,
        BrowserPlannerAction action,
        CancellationToken token)
    {
        await runtime.PauseGate.WaitAsync(token);
        token.ThrowIfCancellationRequested();

        if (ShouldUseTrustedEditorPaste(runtime.Request, runtime.Page, action))
        {
            action.Type = "paste";
            runtime.History.Add(
                "Routed document-editor text entry through trusted native paste and destination read-back.");
        }

        // A native date field is a structured value control, not a clipboard
        // destination. Normalize explicit human-readable dates before the
        // generic trusted-paste path so retry strategy changes cannot turn a
        // valid date into repeated Ctrl+V no-ops.
        if (action.Type == "paste")
        {
            var pasteTarget = FindMatchingControl(runtime.Page, action);
            if (TryNormalizeNativeDateTyping(action, pasteTarget, out var pastedDate))
            {
                action.Type = "type";
                action.Text = pastedDate;
                action.Replace = true;
                action.SubmitAfter = false;
                runtime.History.Add(
                    "Normalized a date paste request to the native date control's ISO value action.");
            }
        }

        if (action.Type == "paste")
        {
            if (string.IsNullOrWhiteSpace(action.Text)
                && !string.IsNullOrWhiteSpace(runtime.PreparedDraft))
            {
                action.Text = runtime.PreparedDraft;
                runtime.History.Add("Used the task-scoped prepared result for the destination paste.");
            }
            var pasteReceipt = await ExecuteTrustedPasteAsync(runtime, action, token);
            AttachExpectedState(pasteReceipt, action);
            LogActionReceipt(runtime.TaskId, pasteReceipt);
            return pasteReceipt;
        }

        var liveTarget = action.Type == "activate_tab"
            ? null
            : FindMatchingControl(runtime.Page, action);
        if (ShouldNormalizeChoiceTyping(action, liveTarget))
        {
            action.Type = "select_option";
            action.Option = action.Text;
            action.Text = "";
            action.SubmitAfter = false;
            runtime.History.Add(
                "Normalized text entry on a non-editable choice control to semantic option selection.");
        }
        if (TryNormalizeNativeDateTyping(action, liveTarget, out var normalizedDate))
        {
            action.Text = normalizedDate;
            action.Replace = true;
            action.SubmitAfter = false;
            runtime.History.Add(
                "Normalized an explicit natural-language date to the native date control's ISO value format.");
        }
        if (IsBroadNonEditorContainerAction(action, liveTarget))
        {
            return new BrowserActionReceipt
            {
                TaskId = runtime.TaskId,
                Ok = false,
                Stale = false,
                Action = action.Type,
                TargetId = liveTarget.Id,
                TargetRole = liveTarget.Role,
                TargetName = ControlName(liveTarget),
                BeforeUrl = runtime.Page.Url,
                AfterUrl = runtime.Page.Url,
                BeforeFingerprint = runtime.Page.Fingerprint,
                AfterFingerprint = runtime.Page.Fingerprint,
                WindowId = runtime.Page.WindowId,
                TabId = runtime.Page.TabId,
                GraphVersion = runtime.Page.GraphVersion,
                DomEpoch = runtime.Page.DomEpoch,
                Error = "The selected target is a broad non-editable page container, not a grounded interactive control. Re-plan using a real editor or active cursor.",
            };
        }

        var effectiveActionType = action.Type;
        var requestedTabId = runtime.Page.TabId;
        if (effectiveActionType == "activate_tab"
            && !TryResolveTaskTabTarget(
                action,
                runtime.ControlledTabs,
                out requestedTabId))
        {
            return new BrowserActionReceipt
            {
                TaskId = runtime.TaskId,
                Ok = false,
                Stale = true,
                Action = action.Type,
                TargetId = action.TargetId,
                BeforeUrl = runtime.Page.Url,
                AfterUrl = runtime.Page.Url,
                BeforeFingerprint = runtime.Page.Fingerprint,
                AfterFingerprint = runtime.Page.Fingerprint,
                WindowId = runtime.Page.WindowId,
                TabId = runtime.Page.TabId,
                GraphVersion = runtime.Page.GraphVersion,
                DomEpoch = runtime.Page.DomEpoch,
                Error = "The requested destination is not one of this task's live owned tabs.",
            };
        }
        if (ShouldOpenDestinationInNewTab(
                runtime.CurrentPageIntent,
                runtime.DestinationTabOpened,
                runtime.InitialTabWasProtectedOrBlank,
                runtime.InitialTabId,
                runtime.Page.TabId,
                action.Type,
                runtime.Page.Url,
                action.Url))
        {
            effectiveActionType = "open_tab";
            runtime.History.Add("Opened the first requested website in one same-window task tab instead of replacing the user's starting tab.");
        }

        if (effectiveActionType is "navigate" or "open_tab")
        {
            if (!Uri.TryCreate(action.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("The planner did not provide a safe absolute URL.");
            }
        }

        var request = new BrowserActionRequest
        {
            TaskId = runtime.TaskId,
            Type = effectiveActionType,
            WindowId = runtime.Page.WindowId,
            TabId = requestedTabId,
            GraphVersion = runtime.Page.GraphVersion,
            DomEpoch = runtime.Page.DomEpoch,
            ControlId = liveTarget?.Id ?? action.TargetId,
            TargetRole = action.TargetRole,
            TargetName = action.TargetName,
            Text = action.Text,
            Url = action.Url,
            Key = action.Key,
            Option = action.Option,
            Replace = action.Replace,
            CaptureBeforeAction = action.CaptureBeforeAction
                && SupportsSafeActionCapture(_bridge.Status.ServiceWorkerVersion),
        };
        var receipt = await _bridge.ExecuteAsync(request, token);
        if (!receipt.Ok
            && ShouldRecoverMissingTaskTab(
                effectiveActionType,
                receipt.Error,
                runtime.MissingTabRecoveryAttempted))
        {
            runtime.MissingTabRecoveryAttempted = true;
            runtime.History.Add(
                "The controlled tab disappeared. Re-leasing the current Chrome window once and opening the requested destination without replacing its active page.");
            SetState(runtime, BrowserTaskState.ReadingPage, "Recovering the closed browser task tab");

            var recoveryAnchor = await _bridge.BeginTaskAsync(
                runtime.TaskId,
                useCurrentTab: true,
                token);
            runtime.Page = recoveryAnchor;
            AddControlledTab(runtime, recoveryAnchor);
            effectiveActionType = "open_tab";
            request = new BrowserActionRequest
            {
                TaskId = runtime.TaskId,
                Type = effectiveActionType,
                WindowId = recoveryAnchor.WindowId,
                TabId = recoveryAnchor.TabId,
                GraphVersion = recoveryAnchor.GraphVersion,
                DomEpoch = recoveryAnchor.DomEpoch,
                Url = action.Url,
            };
            receipt = await _bridge.ExecuteAsync(request, token);
        }
        LogActionReceipt(runtime.TaskId, receipt);
        if (effectiveActionType == "open_tab"
            && receipt.Ok
            && receipt.TabId is int openedTab
            && openedTab != runtime.InitialTabId)
        {
            runtime.DestinationTabOpened = true;
        }

        var targetSuggestsSearch = (action.TargetName ?? "").Contains(
                "search",
                StringComparison.OrdinalIgnoreCase)
            || (action.TargetRole ?? "").Equals("searchbox", StringComparison.OrdinalIgnoreCase);
        if (action.Type == "type"
            && action.SubmitAfter
            && targetSuggestsSearch
            && receipt.Ok
            && receipt.ValueMatches)
        {
            var fresh = await _bridge.ObserveAsync(
                runtime.TaskId,
                receipt.TabId ?? runtime.Page.TabId,
                token);
            var matching = fresh.Controls.FirstOrDefault(control =>
                string.Equals(control.Role, action.TargetRole, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(
                        control.AccessibleName,
                        action.TargetName,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        control.Placeholder,
                        action.TargetName,
                        StringComparison.OrdinalIgnoreCase)));
            if (matching != null)
            {
                var enterReceipt = await _bridge.ExecuteAsync(
                    new BrowserActionRequest
                    {
                        TaskId = runtime.TaskId,
                        Type = "press_key",
                        WindowId = fresh.WindowId,
                        TabId = fresh.TabId,
                        GraphVersion = fresh.GraphVersion,
                        DomEpoch = fresh.DomEpoch,
                        ControlId = matching.Id,
                        TargetRole = matching.Role,
                        TargetName = matching.AccessibleName,
                        Key = "Enter",
                    },
                    token);
                runtime.Receipts.Add(enterReceipt);
                if (enterReceipt.Ok && enterReceipt.NoVisibleChange)
                {
                    enterReceipt = await ExecuteTrustedEnterAsync(
                        runtime,
                        action,
                        fresh,
                        matching,
                        token);
                    runtime.Receipts.Add(enterReceipt);
                }
                runtime.History.Add(enterReceipt.Ok
                    ? "Submitted the verified search field with Enter without another model call."
                    : $"Search submission was not acknowledged: {enterReceipt.Error}");
            }
        }

        if (action.Type == "press_key"
            && string.Equals(action.Key, "Enter", StringComparison.OrdinalIgnoreCase)
            && receipt.Ok
            && receipt.NoVisibleChange)
        {
            var fresh = await _bridge.ObserveAsync(
                runtime.TaskId,
                receipt.TabId ?? runtime.Page.TabId,
                token);
            var matching = FindMatchingControl(fresh, action);
            if (matching != null)
                receipt = await ExecuteTrustedEnterAsync(runtime, action, fresh, matching, token);
        }

        if (ShouldUseListboxKeyboardFallback(action, receipt))
        {
            var fresh = await _bridge.ObserveAsync(
                runtime.TaskId,
                receipt.TabId ?? runtime.Page.TabId,
                token);
            var matching = FindMatchingControl(fresh, action)
                ?? FindMatchingControl(
                    fresh,
                    new BrowserPlannerAction
                    {
                        TargetId = receipt.TargetId,
                        TargetRole = receipt.TargetRole,
                        TargetName = receipt.TargetName,
                    });
            if (matching != null)
            {
                var keyboardReceipt = await ExecuteTrustedEnterAsync(
                    runtime,
                    action,
                    fresh,
                    matching,
                    token);
                if (keyboardReceipt.Ok && keyboardReceipt.NoVisibleChange)
                {
                    var afterEnter = await _bridge.ObserveAsync(
                        runtime.TaskId,
                        keyboardReceipt.TabId ?? fresh.TabId,
                        token);
                    var afterEnterControl = FindMatchingControl(afterEnter, action)
                        ?? FindMatchingControl(
                            afterEnter,
                            new BrowserPlannerAction
                            {
                                TargetId = keyboardReceipt.TargetId,
                                TargetRole = keyboardReceipt.TargetRole,
                                TargetName = keyboardReceipt.TargetName,
                            });
                    if (afterEnterControl != null)
                    {
                        keyboardReceipt = await ExecuteTrustedSpaceAsync(
                            runtime,
                            action,
                            afterEnter,
                            afterEnterControl,
                            token);
                    }
                }
                if (keyboardReceipt.Ok && keyboardReceipt.NoVisibleChange)
                {
                    var afterSpace = await _bridge.ObserveAsync(
                        runtime.TaskId,
                        keyboardReceipt.TabId ?? fresh.TabId,
                        token);
                    var afterSpaceControl = FindMatchingControl(afterSpace, action)
                        ?? FindMatchingControl(
                            afterSpace,
                            new BrowserPlannerAction
                            {
                                TargetId = keyboardReceipt.TargetId,
                                TargetRole = keyboardReceipt.TargetRole,
                                TargetName = keyboardReceipt.TargetName,
                            });
                    if (afterSpaceControl != null)
                    {
                        keyboardReceipt = await ExecuteTrustedAltArrowDownAsync(
                            runtime,
                            action,
                            afterSpace,
                            afterSpaceControl,
                            token);
                    }
                }
                if (keyboardReceipt.Ok)
                {
                    var requestedOption = ResolveRequestedCustomOption(
                        action,
                        runtime.CurrentAction,
                        runtime.Request);
                    if (!string.IsNullOrWhiteSpace(requestedOption))
                    {
                        var opened = await _bridge.ObserveAsync(
                            runtime.TaskId,
                            keyboardReceipt.TabId ?? fresh.TabId,
                            token);
                        var openedControl = FindMatchingControl(opened, action)
                            ?? FindMatchingControl(
                                opened,
                                new BrowserPlannerAction
                                {
                                    TargetId = keyboardReceipt.TargetId,
                                    TargetRole = keyboardReceipt.TargetRole,
                                    TargetName = keyboardReceipt.TargetName,
                                });
                        if (openedControl != null)
                        {
                            keyboardReceipt = await ExecuteTrustedListboxSelectionAsync(
                                runtime,
                                action,
                                opened,
                                openedControl,
                                requestedOption,
                                token);
                        }
                    }
                }
                runtime.History.Add(keyboardReceipt.Ok && !keyboardReceipt.NoVisibleChange
                    ? "The custom listbox used a bounded alternative input strategy and produced a verified live state change."
                    : "The custom listbox exhausted distinct DOM click, Enter, Space, Alt+Down, and explicit type-ahead strategies without a verified state change.");
                AppLog.Info(
                    $"Browser listbox keyboard fallback: task={runtime.TaskId} ok={keyboardReceipt.Ok} "
                    + $"noVisibleChange={keyboardReceipt.NoVisibleChange} target={Short(keyboardReceipt.TargetName, 80)}");
                receipt = keyboardReceipt;
            }
            else
            {
                AppLog.Info(
                    $"Browser listbox keyboard fallback skipped: task={runtime.TaskId} "
                    + "the live semantic control could not be re-resolved.");
            }
        }

        if (ShouldResolveCustomOption(action, receipt))
        {
            var fresh = await _bridge.ObserveAsync(
                runtime.TaskId,
                receipt.TabId ?? runtime.Page.TabId,
                token);
            var option = FindCustomOptionControl(
                fresh,
                action,
                runtime.CurrentAction);
            if (option != null)
            {
                receipt = await _bridge.ExecuteAsync(
                    new BrowserActionRequest
                    {
                        TaskId = runtime.TaskId,
                        Type = "click",
                        WindowId = fresh.WindowId,
                        TabId = fresh.TabId,
                        GraphVersion = fresh.GraphVersion,
                        DomEpoch = fresh.DomEpoch,
                ControlId = option.Id,
                        TargetRole = option.Role,
                        TargetName = ControlName(option),
                        CaptureBeforeAction = action.CaptureBeforeAction
                            && SupportsSafeActionCapture(_bridge.Status.ServiceWorkerVersion),
                    },
                    token);
                runtime.History.Add(receipt.Ok
                    ? "Resolved the requested custom-listbox option to its live accessibility option control."
                    : $"The live custom-listbox option could not be activated: {receipt.Error}");
                AppLog.Info(
                    $"Browser custom option receipt: task={runtime.TaskId} ok={receipt.Ok} "
                    + $"target={Short(receipt.TargetName, 80)} error={Short(receipt.Error, 120)}");
            }
            else
            {
                var requestedOption = ResolveRequestedCustomOption(
                    action,
                    runtime.CurrentAction,
                    runtime.Request);
                var container = FindMatchingControl(fresh, action)
                    ?? (fresh.Controls ?? Array.Empty<BrowserControl>())
                        .Where(control =>
                            control.Visible
                            && control.Enabled
                            && IsCustomOptionContainerRole(control.Role))
                        .OrderBy(control =>
                            control.Bounds.Width * control.Bounds.Height)
                        .FirstOrDefault();
                if (container != null && !string.IsNullOrWhiteSpace(requestedOption))
                {
                    var activation = await ExecuteTrustedAltArrowDownAsync(
                        runtime,
                        action,
                        fresh,
                        container,
                        token);
                    var selectionPage = await _bridge.ObserveAsync(
                        runtime.TaskId,
                        activation.TabId ?? fresh.TabId,
                        token);
                    var liveContainer = FindMatchingControl(selectionPage, action)
                        ?? FindMatchingControl(
                            selectionPage,
                            new BrowserPlannerAction
                            {
                                TargetRole = container.Role,
                                TargetName = ControlName(container),
                            });
                    receipt = liveContainer == null
                        ? new BrowserActionReceipt
                        {
                            TaskId = runtime.TaskId,
                            Ok = false,
                            Stale = true,
                            Action = "select_option",
                            TargetRole = container.Role,
                            TargetName = requestedOption,
                            BeforeUrl = fresh.Url,
                            AfterUrl = selectionPage.Url,
                            BeforeFingerprint = fresh.Fingerprint,
                            AfterFingerprint = selectionPage.Fingerprint,
                            WindowId = selectionPage.WindowId,
                            TabId = selectionPage.TabId,
                            GraphVersion = selectionPage.GraphVersion,
                            DomEpoch = selectionPage.DomEpoch,
                            Error = "The custom listbox changed before its requested option could be re-resolved.",
                        }
                        : await ExecuteTrustedListboxSelectionAsync(
                            runtime,
                            action,
                            selectionPage,
                            liveContainer,
                            requestedOption,
                            token);
                    runtime.History.Add(receipt.Ok
                        ? "The non-native option was selected with bounded trusted keyboard type-ahead and verified from fresh state."
                        : "The non-native option was not exposed in the accessibility graph and bounded trusted keyboard selection did not verify it.");
                }
            }
        }

        if (receipt.Ok
            && (action.Type == "select_option"
                || string.Equals(
                    receipt.TargetRole,
                    "option",
                    StringComparison.OrdinalIgnoreCase)))
        {
            var fresh = await _bridge.ObserveAsync(
                runtime.TaskId,
                receipt.TabId ?? runtime.Page.TabId,
                token);
            if (FreshStateConfirmsOption(fresh, action, receipt))
            {
                receipt.SelectedChanged = true;
                receipt.NoVisibleChange = false;
            }
        }

        receipt = await PreserveProgressAfterUnexpectedNavigationAsync(
            runtime,
            action,
            receipt,
            token);
        AttachExpectedState(receipt, action);
        return receipt;
    }

    private async Task<BrowserActionReceipt> PreserveProgressAfterUnexpectedNavigationAsync(
        Runtime runtime,
        BrowserPlannerAction action,
        BrowserActionReceipt receipt,
        CancellationToken token)
    {
        if (receipt?.Ok != true
            || !receipt.Navigation
            || !BrowserAutomationVerifier.IsLikelyNavigationRegression(
                receipt.BeforeUrl,
                receipt.AfterUrl)
            || BrowserAutomationVerifier.RequestAllowsNavigationRegression(runtime.Request))
        {
            return receipt;
        }

        var progressedUrl = receipt.BeforeUrl;
        var regressed = await _bridge.ObserveAsync(
            runtime.TaskId,
            receipt.TabId ?? runtime.Page.TabId,
            token);
        AddControlledTab(runtime, regressed);
        if (!BrowserAutomationVerifier.IsLikelyNavigationRegression(
                progressedUrl,
                regressed.Url))
        {
            // The action continued asynchronously to a different useful state;
            // do not overwrite newer live progress based on an intermediate
            // receipt URL.
            runtime.Page = regressed;
            return receipt;
        }

        runtime.History.Add(
            "A stale browser action navigated to a less specific same-site state. "
            + "Restoring the last verified page before allowing another plan.");
        var restore = await _bridge.ExecuteAsync(
            new BrowserActionRequest
            {
                TaskId = runtime.TaskId,
                Type = "navigate",
                WindowId = regressed.WindowId,
                TabId = regressed.TabId,
                GraphVersion = regressed.GraphVersion,
                DomEpoch = regressed.DomEpoch,
                Url = progressedUrl,
            },
            token);
        LogActionReceipt(runtime.TaskId, restore);
        var restored = await _bridge.ObserveAsync(
            runtime.TaskId,
            restore.TabId ?? regressed.TabId,
            token);
        AddControlledTab(runtime, restored);
        runtime.Page = restored;

        receipt.Ok = false;
        receipt.Stale = true;
        receipt.Navigation = false;
        receipt.NoVisibleChange = true;
        receipt.AfterUrl = restored.Url;
        receipt.AfterFingerprint = restored.Fingerprint;
        receipt.GraphVersion = restored.GraphVersion;
        receipt.DomEpoch = restored.DomEpoch;
        receipt.Error = restore.Ok
            ? "The action tried to discard a more specific verified page and was safely rolled back."
            : "The action discarded a more specific verified page, and the previous page could not be restored automatically.";
        return receipt;
    }

    private async Task<bool> EstablishAuthoritativeDestinationAsync(
        Runtime runtime,
        string label,
        string url,
        string source,
        CancellationToken token)
    {
        if (SameHost(runtime.Page?.Url, url))
        {
            runtime.History.Add(
                $"Verified {source}: the live page is already on {label} ({BrowserAutomationText.RedactUrlForProvider(url)}).");
            return true;
        }

        var action = new BrowserPlannerAction
        {
            Type = "navigate",
            Url = url,
            TargetName = label,
        };
        var safety = _safety.EvaluateAction(runtime.Request, action);
        if (!safety.MayStart)
        {
            runtime.History.Add(
                $"Could not establish {source} safely: {safety.Reason}");
            return false;
        }

        SetState(runtime, BrowserTaskState.Executing, $"Opening the requested {label} destination");
        var correctionRevision = Volatile.Read(ref runtime.CorrectionRevision);
        var receipt = await ExecutePlannerActionAsync(runtime, action, token);
        if (InFlightResultWasSuperseded(
                correctionRevision,
                Volatile.Read(ref runtime.CorrectionRevision)))
        {
            runtime.History.Add(
                $"Ignored the in-flight {source} result because a newer explicit correction is authoritative.");
            return false;
        }
        runtime.Receipts.Add(receipt);
        if (ShouldStabilizeProtectedBootstrapNavigation(
                runtime.InitialTabWasProtectedOrBlank,
                runtime.BootstrapNavigationStabilized,
                action.Type,
                receipt))
        {
            runtime.BootstrapNavigationStabilized = true;
            SetState(runtime, BrowserTaskState.WaitingForPage, "Waiting for the requested website to become ready");
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }

        runtime.Page = await _bridge.ObserveAsync(
            runtime.TaskId,
            receipt.TabId ?? runtime.Page.TabId,
            token);
        AddControlledTab(runtime, runtime.Page);
        // A verified navigation may legitimately end on the destination's
        // sign-in or canonical redirect host. What matters here is that the
        // exact requested URL—not a fuzzy planner substitute—caused the live
        // navigation. The planner still receives the fresh redirected page.
        var established = receipt.Ok
            && receipt.Navigation
            && !IsProtectedOrBlankUrl(runtime.Page.Url);
        if (established)
        {
            runtime.History.Add(
                $"Established {source}: navigation to the exact {label} URL was verified. The live page is {BrowserAutomationText.RedactUrlForProvider(runtime.Page.Url)}; continue there and do not substitute a similar service.");
        }
        else
        {
            runtime.History.Add(
                $"Failed to establish {source} ({label}): {Short(receipt.Error, 180)}");
        }
        return established;
    }

    private static void DrainPendingGuidance(Runtime runtime)
    {
        while (runtime.PendingGuidance.TryDequeue(out var guidance))
            runtime.History.Add(guidance);
    }

    private bool TryTakePendingDestinationCorrection(
        Runtime runtime,
        out string label,
        out string url)
    {
        lock (_sync)
        {
            label = runtime.PendingDestinationCorrectionLabel;
            url = runtime.PendingDestinationCorrectionUrl;
            runtime.PendingDestinationCorrectionLabel = "";
            runtime.PendingDestinationCorrectionUrl = "";
            return !string.IsNullOrWhiteSpace(url);
        }
    }

    internal static bool IsProtectedOrBlankUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;
        return !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps);
    }

    internal static bool ShouldStabilizeProtectedBootstrapNavigation(
        bool initialTabWasProtectedOrBlank,
        bool alreadyStabilized,
        string actionType,
        BrowserActionReceipt receipt) =>
        initialTabWasProtectedOrBlank
        && !alreadyStabilized
        && string.Equals(actionType, "navigate", StringComparison.Ordinal)
        && receipt?.Ok == true
        && receipt.Navigation;

    internal static bool ShouldOpenDestinationInNewTab(
        bool currentPageIntent,
        bool destinationTabOpened,
        bool initialTabWasProtectedOrBlank,
        int initialTabId,
        int currentTabId,
        string actionType,
        string currentUrl,
        string destinationUrl) =>
        !currentPageIntent
        && !destinationTabOpened
        && actionType == "navigate"
        && !(initialTabWasProtectedOrBlank && currentTabId == initialTabId)
        && !SameHost(currentUrl, destinationUrl);

    internal static void AttachExpectedState(
        BrowserActionReceipt receipt,
        BrowserPlannerAction action)
    {
        if (receipt == null || action == null)
            return;

        receipt.ExpectedValue = action.Type switch
        {
            "type" or "paste" => action.Text ?? "",
            "select_option" => string.IsNullOrWhiteSpace(action.Option)
                ? action.Text ?? ""
                : action.Option,
            _ when receipt.SelectedChanged
                && string.Equals(
                    receipt.TargetRole,
                    "option",
                    StringComparison.OrdinalIgnoreCase) =>
                receipt.TargetName ?? "",
            _ => receipt.ExpectedValue ?? "",
        };
    }

    internal static bool ShouldUseTrustedEditorPaste(
        string request,
        BrowserPageSnapshot page,
        BrowserPlannerAction action)
    {
        if (action?.Type != "type"
            || page?.ActiveEditable != true
            || !RequiresDestinationEditorTransfer(request))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(action.TargetId))
            return true;
        if (string.Equals(action.TargetId, page.ActiveEditorId, StringComparison.Ordinal))
            return true;

        var target = FindMatchingControl(page, action);
        if (target == null || !target.Editable)
            return false;
        var identity = NormalizeControlText(string.Join(
            " ",
            target.Role,
            target.AccessibleName,
            target.AriaLabel,
            target.NearbyLabel,
            page.ActiveEditorName));
        return Regex.IsMatch(
            identity,
            @"\b(document|doc|editor|document content|editing area)\b",
            RegexOptions.IgnoreCase);
    }

    private static void LogActionReceipt(string taskId, BrowserActionReceipt receipt)
    {
        receipt ??= new BrowserActionReceipt { Error = "No action receipt was returned." };
        AppLog.Info(
            $"Browser action receipt: task={taskId} action={receipt.Action} ok={receipt.Ok} stale={receipt.Stale} "
            + $"targetRole={Short(receipt.TargetRole, 40)} targetName={Short(receipt.TargetName, 80)} "
            + $"navigation={receipt.Navigation} noVisibleChange={receipt.NoVisibleChange} "
            + $"selectedChanged={receipt.SelectedChanged} checkedChanged={receipt.CheckedChanged} "
            + $"valueMatches={receipt.ValueMatches} screenshot={(!string.IsNullOrWhiteSpace(receipt.ScreenshotDataUrl))} "
            + $"error={Short(receipt.Error, 160)}");
    }

    internal static bool ShouldUseListboxKeyboardFallback(
        BrowserPlannerAction action,
        BrowserActionReceipt receipt) =>
        action?.Type == "click"
        && receipt?.Ok == true
        && receipt.NoVisibleChange
        && (string.Equals(
                receipt.TargetRole,
                "listbox",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                receipt.TargetRole,
                "combobox",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                action.TargetRole,
                "listbox",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                action.TargetRole,
                "combobox",
                StringComparison.OrdinalIgnoreCase));

    internal static bool ShouldResolveCustomOption(
        BrowserPlannerAction action,
        BrowserActionReceipt receipt) =>
        action?.Type == "select_option"
        && receipt?.Ok == false
        && receipt.Error.Contains(
            "not a native select",
            StringComparison.OrdinalIgnoreCase);

    internal static BrowserControl FindCustomOptionControl(
        BrowserPageSnapshot page,
        BrowserPlannerAction action,
        string immediateGoal = "")
    {
        var requestedText = !string.IsNullOrWhiteSpace(action?.Option)
            ? action.Option
            : action?.Text;
        if (string.IsNullOrWhiteSpace(requestedText))
        {
            var quoted = Regex.Match(
                immediateGoal ?? "",
                @"['""](?<value>[^'""]{1,100})['""]");
            if (quoted.Success)
                requestedText = quoted.Groups["value"].Value;
        }
        var requested = NormalizeControlText(requestedText);
        if (requested.Length == 0)
            return null;

        return (page?.Controls ?? Array.Empty<BrowserControl>())
            .Where(control =>
                control.Visible
                && control.Enabled
                && !IsCustomOptionContainerRole(control.Role)
                && (IsCustomOptionRole(control.Role)
                    || control.SafeActions.Contains(
                        "click",
                        StringComparer.OrdinalIgnoreCase)))
            .Select(control => new
            {
                Control = control,
                Names = new[]
                {
                    control.AccessibleName,
                    control.Text,
                    control.AriaLabel,
                    control.Title,
                    control.NearbyLabel,
                    control.Value,
                }.Select(NormalizeControlText).Where(value => value.Length > 0).ToArray(),
            })
            .Where(candidate => candidate.Names.Any(name => name == requested))
            .Select(candidate => candidate.Control)
            .OrderBy(control => control.Bounds.Width * control.Bounds.Height)
            .FirstOrDefault();
    }

    internal static string ResolveRequestedCustomOption(
        BrowserPlannerAction action,
        string immediateGoal,
        string request)
    {
        var value = !string.IsNullOrWhiteSpace(action?.Option)
            ? action.Option
            : action?.Text;
        if (string.IsNullOrWhiteSpace(value))
        {
            var quoted = Regex.Match(
                immediateGoal ?? "",
                @"['""](?<value>[^'""]{1,100})['""]");
            if (quoted.Success)
                value = quoted.Groups["value"].Value;
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            var explicitChoice = Regex.Match(
                request ?? "",
                @"\b(?:choose|select)\s+(?:the\s+)?(?<value>[^,.;]{1,100}?)\s+(?:for|from|in|on)\b",
                RegexOptions.IgnoreCase);
            if (explicitChoice.Success)
                value = explicitChoice.Groups["value"].Value;
        }

        value = Regex.Replace(
            value ?? "",
            @"^(?:option|answer|value)\s+",
            "",
            RegexOptions.IgnoreCase).Trim().Trim('\'', '"');
        return value.Length <= 100 ? value : "";
    }

    private static bool IsCustomOptionRole(string role) =>
        role.Equals("option", StringComparison.OrdinalIgnoreCase)
        || role.Equals("menuitem", StringComparison.OrdinalIgnoreCase)
        || role.Equals("menuitemradio", StringComparison.OrdinalIgnoreCase)
        || role.Equals("menuitemcheckbox", StringComparison.OrdinalIgnoreCase)
        || role.Equals("radio", StringComparison.OrdinalIgnoreCase);

    private static bool IsCustomOptionContainerRole(string role) =>
        role.Equals("listbox", StringComparison.OrdinalIgnoreCase)
        || role.Equals("combobox", StringComparison.OrdinalIgnoreCase)
        || role.Equals("group", StringComparison.OrdinalIgnoreCase)
        || role.Equals("radiogroup", StringComparison.OrdinalIgnoreCase)
        || role.Equals("form", StringComparison.OrdinalIgnoreCase);

    internal static bool TryRegisterActionAttempt(
        IDictionary<string, int> attempts,
        BrowserPlannerAction action,
        int maximumAttempts,
        out int count) =>
        TryRegisterActionAttempt(
            attempts,
            action,
            "",
            maximumAttempts,
            out count);

    internal static bool TryRegisterActionAttempt(
        IDictionary<string, int> attempts,
        BrowserPlannerAction action,
        string pageState,
        int maximumAttempts,
        out int count)
        => TryRegisterActionAttempt(
            attempts,
            action,
            pageState,
            immediateGoal: "",
            maximumAttempts,
            out count);

    internal static bool TryRegisterActionAttempt(
        IDictionary<string, int> attempts,
        BrowserPlannerAction action,
        string pageState,
        string immediateGoal,
        int maximumAttempts,
        out int count)
    {
        var key = string.Join(
            "|",
            NormalizeControlText(pageState),
            ActionSignature(action),
            ImmediateGoalIdentity(immediateGoal));
        attempts.TryGetValue(key, out count);
        count += 1;
        attempts[key] = count;
        return count <= Math.Max(1, maximumAttempts);
    }

    internal static bool TryRegisterSemanticActionAttempt(
        IDictionary<string, int> attempts,
        BrowserPlannerAction action,
        string immediateGoal,
        int maximumAttempts,
        out int count)
    {
        var key = string.Join(
            "|",
            ActionSignature(action),
            ImmediateGoalIdentity(immediateGoal));
        attempts.TryGetValue(key, out count);
        count += 1;
        attempts[key] = count;
        return count <= Math.Max(1, maximumAttempts);
    }

    internal static string ActionStateKey(
        BrowserPageSnapshot page,
        BrowserPlannerAction action) =>
        string.Join("|", PageStateIdentity(page), ActionSignature(action));

    internal static bool IsKnownNoProgressStrategy(
        ISet<string> noProgressActionStates,
        BrowserPageSnapshot page,
        BrowserPlannerAction action) =>
        noProgressActionStates?.Contains(ActionStateKey(page, action)) == true;

    internal static void RememberNoProgressStrategy(
        ISet<string> noProgressActionStates,
        BrowserPageSnapshot page,
        BrowserPlannerAction action)
    {
        noProgressActionStates?.Add(ActionStateKey(page, action));
    }

    internal static bool CanExhaustStrategy(BrowserActionReceipt receipt) =>
        receipt != null
        && !receipt.Cancelled
        && !receipt.Stale
        && (receipt.Ok
            || !string.IsNullOrWhiteSpace(receipt.BeforeFingerprint));

    private static string PageStateIdentity(BrowserPageSnapshot page)
    {
        if (page == null)
            return "";
        var fingerprint = string.IsNullOrWhiteSpace(page.Fingerprint)
            ? string.Join(
                "|",
                NormalizeControlText(page.Url),
                NormalizeControlText(page.Title),
                NormalizeControlText(page.DomEpoch))
            : page.Fingerprint;
        return $"{page.TabId}|{fingerprint}";
    }

    private static string ActionSignature(BrowserPlannerAction action)
    {
        var carriesMeaningfulText = action?.Type is "type" or "paste" or "copy_clipboard";
        var textHash = !carriesMeaningfulText || string.IsNullOrEmpty(action?.Text)
            ? 0
            : action.Text.GetHashCode(StringComparison.Ordinal);
        var semanticTarget = string.IsNullOrWhiteSpace(action?.TargetName)
            ? action?.TargetId
            : action.TargetName;
        return string.Join(
            "|",
            NormalizeControlText(action?.Type),
            NormalizeControlText(action?.TargetRole),
            NormalizeControlText(semanticTarget),
            NormalizeControlText(action?.Option),
            NavigationTargetIdentity(action),
            NormalizeControlText(action?.Key),
            textHash);
    }

    private static string ImmediateGoalIdentity(string value)
    {
        var normalized = NormalizeControlText(value);
        if (normalized.Length == 0)
            return "";

        // A date-range interaction may legitimately reuse one live control for
        // a different semantic phase. Require date/range context so ordinary
        // language such as "return to the page" or "start a search" is not
        // misclassified as the end/start date phase.
        var hasDateContext = Regex.IsMatch(
            normalized,
            @"\b(?:date|calendar|day|range|inbound|outbound|depart(?:ure)?)\b");
        if (hasDateContext
            && Regex.IsMatch(normalized, @"\b(?:return|inbound|end\s+date|second)\b"))
        {
            return "range-end";
        }
        if (hasDateContext
            && Regex.IsMatch(normalized, @"\b(?:depart(?:ure)?|outbound|start\s+date|first)\b"))
        {
            return "range-start";
        }
        if (Regex.IsMatch(
            normalized,
            @"\b(?:navigate|open|go|return|restart|start)\b.{0,100}\b(?:page|site|interface|home|search|results?)\b"))
        {
            return "site-navigation";
        }
        if (Regex.IsMatch(
            normalized,
            @"\b(?:trigger|initiate|perform|submit|start)\b.{0,100}\b(?:search|results?)\b"))
        {
            return "search-submit";
        }
        return BrowserAutomationText.Truncate(normalized, 160);
    }

    private static string NavigationTargetIdentity(BrowserPlannerAction action)
    {
        if (action?.Type is not ("navigate" or "open_tab")
            || !Uri.TryCreate(action.Url, UriKind.Absolute, out var uri))
        {
            return NormalizeControlText(action?.Url);
        }

        return NormalizeControlText($"{uri.Host}/{uri.AbsolutePath.Trim('/')}");
    }

    internal static bool TryGetNavigationRegression(
        string request,
        BrowserPageSnapshot page,
        BrowserPlannerAction action,
        out string reason)
    {
        reason = "";
        if (action?.Type != "navigate"
            || !BrowserAutomationVerifier.IsLikelyNavigationRegression(
                page?.Url,
                action.Url)
            || BrowserAutomationVerifier.RequestAllowsNavigationRegression(request))
        {
            return false;
        }

        reason =
            "The proposed same-site navigation removes path/query state from the current page.";
        return true;
    }

    internal static bool TryGetSatisfiedSearchPrerequisite(
        BrowserPageSnapshot page,
        BrowserPlannerAction action,
        string immediateGoal,
        IReadOnlyList<BrowserActionReceipt> receipts,
        out string reason)
    {
        reason = "";
        if (page == null
            || action == null
            || !TargetsSearchControl(action)
            || receipts == null
            || receipts.Count == 0)
        {
            return false;
        }

        var priorSearchIndex = -1;
        BrowserActionReceipt priorSearch = null;
        for (var index = receipts.Count - 1; index >= 0; index--)
        {
            var candidate = receipts[index];
            if (candidate?.Ok == true
                && !candidate.Stale
                && candidate.Action is "type" or "paste"
                && TargetsSearchControl(candidate)
                && !string.IsNullOrWhiteSpace(candidate.ExpectedValue))
            {
                priorSearchIndex = index;
                priorSearch = candidate;
                break;
            }
        }

        if (priorSearch == null)
            return false;

        var reachedRequestedResult = false;
        for (var index = priorSearchIndex + 1; index < receipts.Count; index++)
        {
            var candidate = receipts[index];
            if (candidate?.Ok != true
                || candidate.Stale
                || !candidate.Navigation
                || !SameLivePage(candidate.AfterUrl, page.Url))
            {
                continue;
            }

            reachedRequestedResult = BrowserAutomationVerifier.ContainsMeaningfulTokens(
                    page.Title,
                    priorSearch.ExpectedValue,
                    minimumMatches: 2)
                || BrowserAutomationVerifier.ContainsMeaningfulTokens(
                    candidate.TargetName,
                    priorSearch.ExpectedValue,
                    minimumMatches: 2);
            if (reachedRequestedResult)
                break;
        }

        if (!reachedRequestedResult)
            return false;

        if (!string.IsNullOrWhiteSpace(action.Text)
            && !BrowserAutomationVerifier.ContainsMeaningfulTokens(
                string.Join(" ", action.Text, immediateGoal),
                priorSearch.ExpectedValue,
                minimumMatches: 2))
        {
            // A new query is a new requested browser action, not the stale
            // prerequisite that led to the current verified result page.
            return false;
        }

        reason =
            "the same search already led to the current verified target page; submitting it again would discard useful progress.";
        return true;
    }

    private static bool TargetsSearchControl(BrowserPlannerAction action) =>
        action != null
        && action.Type is "type" or "paste" or "press_key" or "click"
        && (string.Equals(
                action.TargetRole,
                "searchbox",
                StringComparison.OrdinalIgnoreCase)
            || (action.TargetName ?? "").Contains(
                "search",
                StringComparison.OrdinalIgnoreCase));

    private static bool TargetsSearchControl(BrowserActionReceipt receipt) =>
        receipt != null
        && (string.Equals(
                receipt.TargetRole,
                "searchbox",
                StringComparison.OrdinalIgnoreCase)
            || (receipt.TargetName ?? "").Contains(
                "search",
                StringComparison.OrdinalIgnoreCase));

    private static bool SameLivePage(string firstUrl, string secondUrl)
    {
        if (!Uri.TryCreate(firstUrl, UriKind.Absolute, out var first)
            || !Uri.TryCreate(secondUrl, UriKind.Absolute, out var second))
        {
            return false;
        }

        return first.Host.Equals(second.Host, StringComparison.OrdinalIgnoreCase)
            && first.AbsolutePath.TrimEnd('/').Equals(
                second.AbsolutePath.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase)
            && first.Query.Equals(second.Query, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsClearlyTaskRelevantGuidance(string guidance)
    {
        var text = NormalizeControlText(guidance);
        if (text.Length == 0)
            return false;

        // A normal Chrome/Jarvis command must never become input for a live
        // webpage just because a browser task happens to be running.
        if (Regex.IsMatch(text, @"\b(?:ctrl|control)\s*\+?\s*\d\b")
            || Regex.IsMatch(text, @"\b(?:switch|go|change)\b.{0,40}\b(?:chrome )?(?:first|second|third|fourth|fifth|last|second[- ]to[- ]last|second last|\d+(?:st|nd|rd|th)?)\s+tab\b")
            || Regex.IsMatch(text, @"\b(?:new|close|next|previous|reopen)\s+tab\b"))
        {
            return false;
        }

        if (IsExplicitTaskCorrection(text))
            return true;

        return Regex.IsMatch(
            text,
            @"^(?:please )?(?:continue|resume|pause|cancel|stop|wait|do(?: not|n't) submit|i (?:have )?(?:completed|finished|signed in|logged in)|use (?:the )?(?:first|second|third|other|different) (?:result|option)|click (?:the )?(?:first|second|third|other|different) (?:result|option)|try (?:the )?(?:other|different)|keep .{0,120} open|stop before .{0,120})\b",
            RegexOptions.IgnoreCase);
    }

    internal static bool TryResolveExplicitDestination(
        string request,
        out string label,
        out string url)
    {
        label = "";
        url = "";
        var text = request ?? "";
        if (text.Length == 0)
            return false;

        // Ground only the destination syntactically attached to an explicit
        // navigation verb. This intentionally avoids fuzzy service matching:
        // an unrelated current page or a similar product name is not evidence
        // for substituting a different destination.
        var matches = Regex.Matches(
            text,
            @"\b(?:go\s+to|navigate\s+to|visit|open)\s+(?:the\s+)?(?<destination>[^,;!?]{1,100}?)(?=\s+(?:and|then)\s+(?:search|find|look\s+for|locate|click|press|choose|select|fill|read|inspect|open)\b|[,;!?]|\.\s|$)",
            RegexOptions.IgnoreCase);
        foreach (Match match in matches.Cast<Match>().Reverse())
        {
            if (JarvisAutomationService.TryResolveKnownWebDestination(
                    match.Groups["destination"].Value,
                    out label,
                    out url))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsExplicitTaskCorrection(string guidance)
    {
        var text = NormalizeControlText(guidance);
        if (text.Length == 0)
            return false;

        var correctionSignal = Regex.IsMatch(
            text,
            @"^(?:no\b|actually\b|wrong\b|not\s+(?:that|this)\b|i\s+meant\b)|\b(?:instead|rather\s+than)\b|^(?:do\s+not|don't)\b",
            RegexOptions.IgnoreCase);
        if (!correctionSignal)
            return false;

        var taskReference = Regex.IsMatch(
            text,
            @"\b(?:website|site|page|tab|result|destination|search|find|click|open|navigate|go\s+to|use|back|that|this)\b",
            RegexOptions.IgnoreCase);
        return taskReference
            || JarvisAutomationService.TryResolveKnownWebDestinationMention(
                guidance,
                out _,
                out _);
    }

    internal static bool TryResolveTaskCorrectionDestination(
        string guidance,
        out string label,
        out string url)
    {
        label = "";
        url = "";
        if (!IsExplicitTaskCorrection(guidance))
            return false;

        // Prefer the positive replacement clause over a negated old target,
        // e.g. "not that site; use X instead". The resolver itself accepts
        // only exact known services or explicit domains.
        var replacements = Regex.Matches(
            guidance ?? "",
            @"\b(?:i\s+meant|use|open|go\s+to|navigate\s+to|on)\s+(?:the\s+)?(?<destination>[^,;!?]{1,100}?)(?:\s+instead)?(?=[,;!?]|\.\s|$)",
            RegexOptions.IgnoreCase);
        foreach (Match replacement in replacements.Cast<Match>().Reverse())
        {
            var candidate = Regex.Replace(
                replacement.Groups["destination"].Value,
                @"\s+instead\s*$",
                "",
                RegexOptions.IgnoreCase).Trim();
            if (JarvisAutomationService.TryResolveKnownWebDestinationMention(
                    candidate,
                    out label,
                    out url))
            {
                return true;
            }
        }

        return JarvisAutomationService.TryResolveKnownWebDestinationMention(
            guidance,
            out label,
            out url);
    }

    internal static string BuildCorrectedTaskRequest(
        string existingRequest,
        string correction)
    {
        var correctedDestination = TryResolveTaskCorrectionDestination(
            correction,
            out _,
            out var destinationUrl)
            ? $"\nEstablish the corrected destination first: navigate to {destinationUrl}."
            : "";
        return BrowserAutomationText.Truncate(
            $"Earlier task objective (retain only details compatible with the correction): {(existingRequest ?? "").Trim()}\n"
            + $"Latest explicit user correction (authoritative): {(correction ?? "").Trim()}"
            + correctedDestination,
            4000);
    }

    internal static bool IsNavigationToSupersededDestination(
        BrowserPlannerAction action,
        string correctedDestinationUrl,
        string supersededDestinationUrl) =>
        action?.Type is "navigate" or "open_tab"
        && !string.IsNullOrWhiteSpace(correctedDestinationUrl)
        && !string.IsNullOrWhiteSpace(supersededDestinationUrl)
        && SameHost(action.Url, supersededDestinationUrl)
        && !SameHost(action.Url, correctedDestinationUrl);

    internal static bool InFlightResultWasSuperseded(
        int capturedCorrectionRevision,
        int currentCorrectionRevision) =>
        currentCorrectionRevision > capturedCorrectionRevision;

    internal static bool IsRelevantManualAnswer(
        string requestedUserAction,
        string guidance)
    {
        var request = NormalizeControlText(requestedUserAction);
        var answer = NormalizeControlText(guidance);
        if (request.Length == 0 || answer.Length == 0)
            return false;

        if (Regex.IsMatch(
                request,
                @"\b(?:date|departure|depart|return|arrival|arrive|check[- ]?in|check[- ]?out|calendar)\b",
                RegexOptions.IgnoreCase)
            && HasUsableDateConstraint(answer))
        {
            return true;
        }

        // A paused task may ask for a missing non-sensitive field value. A
        // direct "use/enter/choose X" answer is task guidance, not a new
        // browser command. Keep this bounded to an explicit request for input
        // and reject tab/window/navigation commands.
        return Regex.IsMatch(
                request,
                @"\b(?:provide|enter|type|fill|specify|choose|select)\b",
                RegexOptions.IgnoreCase)
            && Regex.IsMatch(
                answer,
                @"^(?:please\s+)?(?:use|enter|type|choose|select)\s+\S.{0,240}$",
                RegexOptions.IgnoreCase)
            && !Regex.IsMatch(
                answer,
                @"\b(?:new|close|next|previous|reopen|switch|change)\s+(?:browser\s+)?(?:tab|window)\b",
                RegexOptions.IgnoreCase);
    }

    internal static bool IsExplicitSingleControlActionRequest(string request)
    {
        var text = NormalizeControlText(request);
        if (!Regex.IsMatch(
                text,
                @"^(?:please\s+)?(?:click|press|choose|select|check|uncheck|open)\b",
                RegexOptions.IgnoreCase)
            || Regex.IsMatch(
                text,
                @"[,;]|\b(?:and|then|after|before|followed by|also)\b",
                RegexOptions.IgnoreCase))
        {
            return false;
        }

        return Regex.Matches(
            text,
            @"\b(?:click|press|choose|select|check|uncheck|open|type|fill|search|navigate|copy|paste|play|add)\b",
            RegexOptions.IgnoreCase).Count == 1;
    }

    internal static bool IsUnrequestedFinalActionRejection(
        BrowserAutomationSafetyDecision decision) =>
        decision?.MayStart == false
        && decision.PauseBeforeSensitiveStep
        && (decision.Reason.Contains(
                "not explicitly requested",
                StringComparison.OrdinalIgnoreCase)
            || decision.Reason.Contains(
                "did not explicitly request",
                StringComparison.OrdinalIgnoreCase));

    private static void CapturePreparedDraft(Runtime runtime, BrowserPlannerDecision decision)
    {
        if (string.IsNullOrWhiteSpace(decision?.CompletionValue)
            || string.IsNullOrWhiteSpace(runtime?.Request)
            || !Regex.IsMatch(
                runtime.Request,
                @"\b(?:summari[sz]e|extract|research|read)\b[\s\S]{0,800}\b(?:paste|insert|document|docs|editor)\b",
                RegexOptions.IgnoreCase))
        {
            return;
        }

        var draft = BrowserAutomationText.Truncate(decision.CompletionValue.Trim(), 8000);
        if (draft.Length == 0 || string.Equals(draft, runtime.PreparedDraft, StringComparison.Ordinal))
            return;

        runtime.PreparedDraft = draft;
        runtime.History.Add($"Prepared an ephemeral source result for the destination editor (length {draft.Length}).");
    }

    private bool RejectKnownNoProgressStrategy(
        Runtime runtime,
        BrowserPlannerAction action,
        string source)
    {
        if (!IsKnownNoProgressStrategy(
            runtime.NoProgressActionStates,
            runtime.Page,
            action))
        {
            return false;
        }

        runtime.ConsecutiveRejectedStrategies++;
        runtime.History.Add(
            $"Strategy rejected before execution ({source}): {DescribeAction(action)} already made no progress on this unchanged page. Choose a different control, navigation path, input mechanism, or bounded visual recovery.");
        SetState(
            runtime,
            BrowserTaskState.Planning,
            "Reconsidering after an unchanged action");
        return true;
    }

    private static string DescribeAction(BrowserPlannerAction action)
    {
        var target = string.IsNullOrWhiteSpace(action?.TargetName)
            ? action?.TargetRole
            : action.TargetName;
        return string.IsNullOrWhiteSpace(target)
            ? action?.Type ?? "action"
            : $"{action?.Type} on {Short(target, 80)}";
    }

    internal static bool TryGroundSearchEnter(
        BrowserPageSnapshot page,
        BrowserPlannerAction action,
        string immediateGoal)
    {
        if (action?.Type != "press_key"
            || !string.Equals(action.Key, "Enter", StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(
                immediateGoal ?? "",
                @"\b(search|find|look\s*up|lookup|query)\b",
                RegexOptions.IgnoreCase))
        {
            return false;
        }

        var candidates = (page?.Controls ?? Array.Empty<BrowserControl>())
            .Where(control => control.Visible && control.Enabled)
            .Where(control =>
                control.Role.Equals("searchbox", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(
                    string.Join(
                        " ",
                        control.AccessibleName,
                        control.AriaLabel,
                        control.Placeholder,
                        control.NearbyLabel),
                    @"\b(search|find|query)\b",
                    RegexOptions.IgnoreCase))
            .GroupBy(control => control.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (candidates.Length != 1)
            return false;

        var search = candidates[0];
        action.TargetId = search.Id;
        action.TargetRole = search.Role;
        action.TargetName = ControlName(search);
        return true;
    }

    internal static bool FreshStateConfirmsOption(
        BrowserPageSnapshot page,
        BrowserPlannerAction action,
        BrowserActionReceipt receipt)
    {
        var wanted = Normalize(
            !string.IsNullOrWhiteSpace(action?.Option)
                ? action.Option
                : !string.IsNullOrWhiteSpace(receipt?.TargetName)
                    ? receipt.TargetName
                    : action?.TargetName);
        if (wanted.Length == 0
            || wanted is "choose" or "select" or "select an option")
        {
            return false;
        }

        return (page?.Controls ?? Array.Empty<BrowserControl>()).Any(control =>
        {
            var names = new[]
            {
                control.AccessibleName,
                control.Text,
                control.Value,
            }.Select(Normalize).Where(value => value.Length > 0).ToArray();
            var exact = names.Any(value => value == wanted);
            if (!exact)
                return false;
            return control.Selected
                || control.Checked
                || control.Role.Equals("combobox", StringComparison.OrdinalIgnoreCase)
                || control.Role.Equals("listbox", StringComparison.OrdinalIgnoreCase)
                || (action?.Type == "select_option" && control.Value.Length > 0);
        });
    }

    internal static bool IsVerifiedEditorTransferAction(
        string request,
        BrowserActionReceipt receipt,
        BrowserPageSnapshot page)
    {
        if (receipt == null
            || !receipt.Ok
            || !receipt.ValueMatches
            || receipt.Action is not ("paste" or "type")
            || !Regex.IsMatch(request ?? "", @"\b(paste|insert|type|write|place|put)\b")
            || !Regex.IsMatch(request ?? "", @"\b(google docs|document|docs|doc|editor|textbox|text area)\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        // Paste uses the trusted active-editor contract. For direct typing, do
        // not mistake a source-page searchbox for the destination editor.
        if (receipt.Action.Equals("paste", StringComparison.OrdinalIgnoreCase))
            return true;

        var target = (page?.Controls ?? Array.Empty<BrowserControl>()).FirstOrDefault(control =>
            string.Equals(control.Id, receipt.TargetId, StringComparison.Ordinal));
        var name = string.Join(
            " ",
            receipt.TargetName,
            target?.AccessibleName,
            target?.AriaLabel,
            target?.NearbyLabel);
        if (Regex.IsMatch(name, @"\b(editor|document|docs|doc)\b", RegexOptions.IgnoreCase))
            return true;

        return target != null
            && target.Editable
            && !target.Role.Equals("searchbox", StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.Id, page.ActiveEditorId, StringComparison.Ordinal);
    }

    private static bool SameHost(string left, string right) =>
        Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
        && Uri.TryCreate(right, UriKind.Absolute, out var rightUri)
        && string.Equals(leftUri.Host, rightUri.Host, StringComparison.OrdinalIgnoreCase);

    internal static bool SupportsSafeActionCapture(string helperVersion) =>
        Version.TryParse(helperVersion, out var parsed)
        && parsed >= new Version(2, 1, 1);

    private async Task<BrowserActionReceipt> ExecuteTrustedPasteAsync(
        Runtime runtime,
        BrowserPlannerAction action,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(action.Text))
            throw new InvalidOperationException("The planner provided no text to paste.");

        var before = runtime.Page;
        var control = FindMatchingControl(before, action);
        BrowserActionReceipt focus;
        if (IsSafePasteTarget(control))
        {
            focus = await FocusControlAsync(runtime.TaskId, before, control, token);
        }
        else if (before.ActiveEditable)
        {
            // Do not click a panel/container to "find" a document. When the
            // user already has a real insertion point, preserve that cursor.
            focus = await FocusActiveCursorAsync(runtime.TaskId, before, token);
            control ??= FindActiveEditorControl(before);
        }
        else
        {
            return new BrowserActionReceipt
            {
                TaskId = runtime.TaskId,
                Ok = false,
                Action = "paste",
                TargetId = action.TargetId,
                TargetRole = action.TargetRole,
                TargetName = action.TargetName,
                BeforeUrl = before.Url,
                AfterUrl = before.Url,
                BeforeFingerprint = before.Fingerprint,
                AfterFingerprint = before.Fingerprint,
                WindowId = before.WindowId,
                TabId = before.TabId,
                GraphVersion = before.GraphVersion,
                DomEpoch = before.DomEpoch,
                Error = "No verified editable destination is focused. Place the cursor in the destination editor, then ask me to continue.",
            };
        }
        if (!focus.Ok)
            return focus;

        token.ThrowIfCancellationRequested();
        var clipboardSnapshot = ClipboardHelper.Capture();
        var clipboardText = ClipboardHelper.GetText();
        var useAtomicEditorReceipt = before.ActiveEditable
            && RequiresDestinationEditorTransfer(runtime.Request);
        var editorReceiptToken = Guid.NewGuid().ToString("N")[..12];
        var editorReceiptMarker = useAtomicEditorReceipt
            ? $"[[kwtf:{editorReceiptToken}]]"
            : "";
        var clipboardPayload = action.Text + editorReceiptMarker;
        try
        {
            if (!ClipboardHelper.SetText(clipboardPayload)
                || !string.Equals(ClipboardHelper.GetText(), clipboardPayload, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The clipboard could not be prepared for the requested paste.");
            }

            await Task.Delay(90, token);
            token.ThrowIfCancellationRequested();
            BrowserTrustedInput.Paste(before.Title);
            await Task.Delay(550, token);
            token.ThrowIfCancellationRequested();
            var after = await _bridge.ObserveAsync(runtime.TaskId, before.TabId, token);
            var live = FindMatchingControl(after, action)
                ?? FindActiveEditorControl(after);
            var verified = await _bridge.ExecuteAsync(
                new BrowserActionRequest
                {
                    TaskId = runtime.TaskId,
                    Type = "verify_active_text",
                    WindowId = after.WindowId,
                    TabId = after.TabId,
                    GraphVersion = after.GraphVersion,
                    DomEpoch = after.DomEpoch,
                    ControlId = live?.Id ?? after.ActiveEditorId,
                    Text = action.Text,
                },
                token);
            var valueMatches = verified.Ok && verified.ValueMatches
                || ContainsRequestedText(live?.Value, action.Text)
                || ContainsRequestedText(after.PageText, action.Text);
            if (useAtomicEditorReceipt
                && !string.IsNullOrWhiteSpace(after.ActiveEditorId))
            {
                var refocus = await FocusActiveCursorAsync(
                    runtime.TaskId,
                    after,
                    token);
                if (refocus.Ok)
                {
                    token.ThrowIfCancellationRequested();
                    var unchangedCanary = $"kwtf-copy-canary-{Guid.NewGuid():N}";
                    if (ClipboardHelper.SetText(unchangedCanary))
                    {
                        var markerElementCount = StringInfo.ParseCombiningCharacters(editorReceiptMarker).Length;
                        if (markerElementCount <= 256)
                        {
                            BrowserTrustedInput.SelectPreviousText(
                                markerElementCount,
                                after.Title);
                            await Task.Delay(140, token);
                            BrowserTrustedInput.CopySelection(after.Title);
                            await Task.Delay(280, token);
                            var copiedText = ClipboardHelper.GetText();
                            valueMatches = string.Equals(
                                copiedText,
                                editorReceiptMarker,
                                StringComparison.Ordinal);
                            if (valueMatches)
                            {
                                BrowserTrustedInput.DeleteSelection(after.Title);
                                await Task.Delay(180, token);
                                after = await _bridge.ObserveAsync(
                                    runtime.TaskId,
                                    after.TabId,
                                    token);
                                live = FindMatchingControl(after, action)
                                    ?? FindActiveEditorControl(after);
                            }
                            else
                            {
                                // Never delete an unverified selection: if the
                                // paste did not land, that could remove content
                                // that was already in the user's document.
                                BrowserTrustedInput.CollapseSelectionToEnd(after.Title);
                            }
                            AppLog.Info(
                                $"Browser editor read-back: task={runtime.TaskId} expectedLength={action.Text.Length} "
                                + $"receiptLength={editorReceiptMarker.Length} copiedLength={copiedText.Length} "
                                + $"clipboardChanged={!string.Equals(copiedText, unchangedCanary, StringComparison.Ordinal)} "
                                + $"valueMatches={valueMatches}");
                            runtime.History.Add(valueMatches
                                ? $"Atomic native editor paste was verified (content length {action.Text.Length})."
                                : "The atomic native editor paste receipt could not be verified.");
                        }
                    }
                }
            }
            var finalControl = live ?? control;
            var finalTargetId = finalControl?.Id;
            if (string.IsNullOrWhiteSpace(finalTargetId))
                finalTargetId = string.IsNullOrWhiteSpace(after.ActiveEditorId)
                    ? before.ActiveEditorId
                    : after.ActiveEditorId;
            var finalTargetRole = finalControl?.Role;
            if (string.IsNullOrWhiteSpace(finalTargetRole))
                finalTargetRole = string.IsNullOrWhiteSpace(after.ActiveEditorRole)
                    ? before.ActiveEditorRole
                    : after.ActiveEditorRole;
            var finalTargetName = ControlName(finalControl);
            if (string.IsNullOrWhiteSpace(finalTargetName))
                finalTargetName = string.IsNullOrWhiteSpace(after.ActiveEditorName)
                    ? before.ActiveEditorName
                    : after.ActiveEditorName;
            return new BrowserActionReceipt
            {
                CommandId = verified.CommandId.Length > 0 ? verified.CommandId : focus.CommandId,
                TaskId = runtime.TaskId,
                Ok = true,
                Action = "paste",
                TargetId = finalTargetId ?? "",
                TargetRole = finalTargetRole ?? "",
                TargetName = finalTargetName ?? "",
                BeforeUrl = before.Url,
                AfterUrl = after.Url,
                BeforeFingerprint = before.Fingerprint,
                AfterFingerprint = after.Fingerprint,
                ValueMatches = valueMatches,
                Navigation = !string.Equals(before.Url, after.Url, StringComparison.Ordinal),
                NoVisibleChange = string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal)
                    && !valueMatches,
                NativeInputUsed = true,
                WindowId = after.WindowId,
                TabId = after.TabId,
                GraphVersion = after.GraphVersion,
                DomEpoch = after.DomEpoch,
                TargetBounds = verified.TargetBounds.Width > 0
                    ? verified.TargetBounds
                    : focus.TargetBounds,
            };
        }
        finally
        {
            if (!ClipboardHelper.Restore(clipboardSnapshot))
                _ = ClipboardHelper.SetText(clipboardText);
        }
    }

    private static bool IsSafePasteTarget(BrowserControl control) =>
        control != null
        && control.Visible
        && control.Enabled
        && control.Editable
        && control.SafeActions.Contains("type", StringComparer.OrdinalIgnoreCase);

    internal static bool IsBroadNonEditorContainerAction(
        BrowserPlannerAction action,
        BrowserControl control)
    {
        if (action == null
            || control == null
            || action.Type is not ("click" or "type" or "press_key")
            || control.Editable)
        {
            return false;
        }

        var role = NormalizeControlText(control.Role);
        var area = Math.Max(0, control.Bounds.Width) * Math.Max(0, control.Bounds.Height);
        return area >= 45_000
            && role is "div" or "document" or "region" or "main" or "group" or "tabpanel";
    }

    private static BrowserControl FindActiveEditorControl(BrowserPageSnapshot page) =>
        (page?.Controls ?? Array.Empty<BrowserControl>()).FirstOrDefault(control =>
            string.Equals(control.Id, page.ActiveEditorId, StringComparison.Ordinal));

    private async Task<BrowserActionReceipt> FocusActiveCursorAsync(
        string taskId,
        BrowserPageSnapshot page,
        CancellationToken token) =>
        await _bridge.ExecuteAsync(
            new BrowserActionRequest
            {
                TaskId = taskId,
                Type = "focus_active_cursor",
                WindowId = page.WindowId,
                TabId = page.TabId,
                GraphVersion = page.GraphVersion,
                DomEpoch = page.DomEpoch,
                ControlId = page.ActiveEditorId,
                TargetRole = page.ActiveEditorRole,
                TargetName = page.ActiveEditorName,
            },
            token);

    private async Task<BrowserActionReceipt> ExecuteTrustedEnterAsync(
        Runtime runtime,
        BrowserPlannerAction action,
        BrowserPageSnapshot before,
        BrowserControl control,
        CancellationToken token) =>
        await ExecuteTrustedKeyAsync(
            runtime,
            action,
            before,
            control,
            () => BrowserTrustedInput.PressEnter(before.Title),
            token);

    private async Task<BrowserActionReceipt> ExecuteTrustedSpaceAsync(
        Runtime runtime,
        BrowserPlannerAction action,
        BrowserPageSnapshot before,
        BrowserControl control,
        CancellationToken token) =>
        await ExecuteTrustedKeyAsync(
            runtime,
            action,
            before,
            control,
            () => BrowserTrustedInput.PressSpace(before.Title),
            token);

    private async Task<BrowserActionReceipt> ExecuteTrustedAltArrowDownAsync(
        Runtime runtime,
        BrowserPlannerAction action,
        BrowserPageSnapshot before,
        BrowserControl control,
        CancellationToken token) =>
        await ExecuteTrustedKeyAsync(
            runtime,
            action,
            before,
            control,
            () => BrowserTrustedInput.PressAltArrowDown(before.Title),
            token);

    private async Task<BrowserActionReceipt> ExecuteTrustedListboxSelectionAsync(
        Runtime runtime,
        BrowserPlannerAction action,
        BrowserPageSnapshot before,
        BrowserControl control,
        string requestedOption,
        CancellationToken token)
    {
        var focus = await FocusControlAsync(runtime.TaskId, before, control, token);
        if (!focus.Ok)
            return focus;

        token.ThrowIfCancellationRequested();
        await Task.Delay(75, token);
        BrowserTrustedInput.TypeTextAndPressEnter(requestedOption, before.Title);
        await Task.Delay(650, token);
        token.ThrowIfCancellationRequested();
        var after = await _bridge.ObserveAsync(runtime.TaskId, before.TabId, token);
        var live = FindMatchingControl(after, action)
            ?? FindMatchingControl(
                after,
                new BrowserPlannerAction
                {
                    TargetRole = control.Role,
                    TargetName = ControlName(control),
                });
        var wanted = NormalizeControlText(requestedOption);
        var valueMatches = new[]
        {
            live?.Value,
            live?.Text,
            live?.AccessibleName,
        }.Any(value => NormalizeControlText(value) == wanted);
        var changed = !string.Equals(
            before.Fingerprint,
            after.Fingerprint,
            StringComparison.Ordinal);
        return new BrowserActionReceipt
        {
            CommandId = focus.CommandId,
            TaskId = runtime.TaskId,
            Ok = changed && valueMatches,
            Error = changed && valueMatches
                ? ""
                : "The custom listbox did not display the requested option after native type-ahead.",
            Action = "select_option",
            TargetId = live?.Id ?? control.Id,
            TargetRole = live?.Role ?? control.Role,
            TargetName = requestedOption,
            BeforeUrl = before.Url,
            AfterUrl = after.Url,
            BeforeFingerprint = before.Fingerprint,
            AfterFingerprint = after.Fingerprint,
            ValueMatches = valueMatches,
            SelectedChanged = changed && valueMatches,
            Navigation = !string.Equals(before.Url, after.Url, StringComparison.Ordinal),
            NoVisibleChange = !changed,
            NativeInputUsed = true,
            WindowId = after.WindowId,
            TabId = after.TabId,
            GraphVersion = after.GraphVersion,
            DomEpoch = after.DomEpoch,
            TargetBounds = focus.TargetBounds,
        };
    }

    private async Task<BrowserActionReceipt> ExecuteTrustedKeyAsync(
        Runtime runtime,
        BrowserPlannerAction action,
        BrowserPageSnapshot before,
        BrowserControl control,
        Action sendKey,
        CancellationToken token)
    {
        var focus = await FocusControlAsync(runtime.TaskId, before, control, token);
        if (!focus.Ok)
            return focus;

        token.ThrowIfCancellationRequested();
        await Task.Delay(75, token);
        sendKey();
        await Task.Delay(550, token);
        token.ThrowIfCancellationRequested();
        var after = await _bridge.ObserveAsync(runtime.TaskId, before.TabId, token);
        return new BrowserActionReceipt
        {
            CommandId = focus.CommandId,
            TaskId = runtime.TaskId,
            Ok = true,
            Action = "press_key",
            TargetId = control.Id,
            TargetRole = control.Role,
            TargetName = ControlName(control),
            BeforeUrl = before.Url,
            AfterUrl = after.Url,
            BeforeFingerprint = before.Fingerprint,
            AfterFingerprint = after.Fingerprint,
            Navigation = !string.Equals(before.Url, after.Url, StringComparison.Ordinal),
            NoVisibleChange = string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal),
            NativeInputUsed = true,
            WindowId = after.WindowId,
            TabId = after.TabId,
            GraphVersion = after.GraphVersion,
            DomEpoch = after.DomEpoch,
            TargetBounds = focus.TargetBounds,
        };
    }

    private async Task<BrowserActionReceipt> FocusControlAsync(
        string taskId,
        BrowserPageSnapshot page,
        BrowserControl control,
        CancellationToken token) =>
        await _bridge.ExecuteAsync(
            new BrowserActionRequest
            {
                TaskId = taskId,
                Type = "focus",
                WindowId = page.WindowId,
                TabId = page.TabId,
                GraphVersion = page.GraphVersion,
                DomEpoch = page.DomEpoch,
                ControlId = control.Id,
                TargetRole = control.Role,
                TargetName = ControlName(control),
            },
            token);

    private static BrowserControl FindMatchingControl(
        BrowserPageSnapshot page,
        BrowserPlannerAction action)
    {
        var byId = page.Controls.FirstOrDefault(control =>
            string.Equals(control.Id, action.TargetId, StringComparison.Ordinal));
        if (byId != null)
            return byId;

        var wanted = Normalize(action.TargetName);
        return page.Controls
            .Where(control =>
                string.IsNullOrWhiteSpace(action.TargetRole)
                || string.Equals(control.Role, action.TargetRole, StringComparison.OrdinalIgnoreCase))
            .Select(control => new
            {
                Control = control,
                Score = NameScore(wanted, Normalize(ControlName(control))),
            })
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault(candidate => candidate.Score >= 2)
            ?.Control;
    }

    private static int NameScore(string wanted, string actual)
    {
        if (wanted.Length == 0 || actual.Length == 0)
            return 0;
        if (wanted == actual)
            return 4;
        if (actual.StartsWith(wanted, StringComparison.Ordinal)
            || wanted.StartsWith(actual, StringComparison.Ordinal))
            return 3;
        return actual.Contains(wanted, StringComparison.Ordinal)
            || wanted.Contains(actual, StringComparison.Ordinal)
                ? 2
                : 0;
    }

    private static string ControlName(BrowserControl control) =>
        new[]
        {
            control?.AccessibleName,
            control?.AriaLabel,
            control?.Placeholder,
            control?.Text,
            control?.NearbyLabel,
        }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static string Normalize(string value) =>
        string.Join(
            " ",
            (value ?? "").Split(
                (char[])null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .ToLowerInvariant();

    private static bool ContainsRequestedText(string observed, string expected)
    {
        if (string.IsNullOrWhiteSpace(observed) || string.IsNullOrWhiteSpace(expected))
            return false;
        var normalizedObserved = string.Join(
            " ",
            observed.Split(
                (char[])null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var normalizedExpected = string.Join(
            " ",
            expected.Split(
                (char[])null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var marker = BrowserAutomationText.Truncate(normalizedExpected, 120);
        return normalizedObserved.Contains(marker, StringComparison.Ordinal);
    }

    private void Complete(
        Runtime runtime,
        BrowserAutomationOutcome outcome,
        string summary,
        string evidence)
    {
        var finalSummary = string.IsNullOrWhiteSpace(summary)
            ? "The browser task completed."
            : summary.Trim();
        runtime.History.Add($"Terminal verification: {evidence}");
        SetTerminal(
            runtime,
            outcome == BrowserAutomationOutcome.CompletedWithNote
                ? BrowserTaskState.CompletedWithNote
                : BrowserTaskState.Completed,
            outcome,
            "Completed",
            finalSummary);
        _notifications.InfoTransient("Browser automation", finalSummary);
    }

    private void Fail(Runtime runtime, BrowserAutomationOutcome outcome, string message)
    {
        var state = outcome switch
        {
            BrowserAutomationOutcome.Partial => BrowserTaskState.Partial,
            BrowserAutomationOutcome.Blocked => BrowserTaskState.Blocked,
            BrowserAutomationOutcome.NeedsUserAction => BrowserTaskState.NeedsUserAction,
            _ => BrowserTaskState.Failed,
        };
        SetTerminal(runtime, state, outcome, message, "");
        _notifications.WarningTransient("Browser automation", message);
    }

    private void SetTerminal(
        Runtime runtime,
        BrowserTaskState state,
        BrowserAutomationOutcome outcome,
        string action,
        string result)
    {
        runtime.UserPaused = false;
        runtime.PauseGate.Resume();
        runtime.State = state;
        runtime.Outcome = outcome;
        runtime.CurrentAction = action;
        runtime.ResultSummary = result;
        runtime.LastError = outcome is BrowserAutomationOutcome.Failed
            or BrowserAutomationOutcome.Blocked
            or BrowserAutomationOutcome.Partial
                ? action
                : "";
        Publish(runtime);
        AnnounceOutcome(runtime, state);
        ReleaseTaskContent(runtime);
    }

    private static void ReleaseTaskContent(Runtime runtime)
    {
        runtime.PreparedDraft = "";
        runtime.History.Clear();
        runtime.VerifiedCheckpoints.Clear();
        runtime.ActionAttempts.Clear();
        runtime.SemanticActionAttempts.Clear();
        runtime.NoProgressActionStates.Clear();
        runtime.Receipts.Clear();
        if (runtime.Page != null)
        {
            runtime.Page.PageText = "";
            runtime.Page.SelectedText = "";
            runtime.Page.Controls = Array.Empty<BrowserControl>();
        }
    }

    private void AnnounceOutcome(Runtime runtime, BrowserTaskState state)
    {
        if (runtime.LastAnnouncedState == state)
            return;
        if (!ShouldAnnounceOutcome(state, runtime.SuppressCancellationAnnouncement))
        {
            runtime.LastAnnouncedState = state;
            return;
        }

        var spoken = state switch
        {
            BrowserTaskState.Completed or BrowserTaskState.CompletedWithNote =>
                "Okay, the browser task is complete. The result should now be visible in Chrome.",
            BrowserTaskState.NeedsUserAction => BuildInterventionSpeech(runtime.CurrentAction),
            BrowserTaskState.Partial =>
                "I completed part of the browser task, but stopped before the full result. Chrome is left at the useful stopping point.",
            BrowserTaskState.Blocked =>
                "The browser task is blocked. I left Chrome at the useful stopping point.",
            BrowserTaskState.Cancelled =>
                "Browser automation cancelled.",
            BrowserTaskState.Failed =>
                "The browser task stopped because it could not complete safely.",
            _ => "",
        };
        if (string.IsNullOrWhiteSpace(spoken))
            return;

        runtime.LastAnnouncedState = state;
        var detail = state switch
        {
            BrowserTaskState.Completed or BrowserTaskState.CompletedWithNote => runtime.ResultSummary,
            BrowserTaskState.NeedsUserAction => runtime.CurrentAction,
            _ => runtime.LastError,
        };
        var announcement = new JarvisLifecycleAnnouncement
        {
            Source = "browser_automation",
            State = state.ToString(),
            Detail = string.IsNullOrWhiteSpace(detail) ? spoken : detail,
            FallbackSpeech = spoken,
        };
        var handler = AnnouncementRequested;
        if (handler == null)
        {
            KeyboardWtf.Services.TtsService.Instance.Speak(spoken, "en");
            return;
        }

        try
        {
            handler(announcement);
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Browser lifecycle announcement handler failed");
            KeyboardWtf.Services.TtsService.Instance.Speak(spoken, "en");
        }
    }

    private void SetState(Runtime runtime, BrowserTaskState state, string action)
    {
        if (runtime.UserPaused
            && state is not BrowserTaskState.Cancelling
            and not BrowserTaskState.Cancelled
            and not BrowserTaskState.Completed
            and not BrowserTaskState.CompletedWithNote
            and not BrowserTaskState.Partial
            and not BrowserTaskState.Blocked
            and not BrowserTaskState.Failed)
        {
            runtime.State = BrowserTaskState.Paused;
            runtime.CurrentAction = "Paused by user";
            Publish(runtime);
            AppLog.Info(
                $"Browser task state: task={runtime.TaskId} state=Paused action=Paused by user");
            return;
        }

        runtime.State = state;
        runtime.CurrentAction = action;
        Publish(runtime);
        if (state == BrowserTaskState.NeedsUserAction)
            AnnounceOutcome(runtime, state);
        AppLog.Info(
            $"Browser task state: task={runtime.TaskId} state={state} action={Short(action, 180)}");
    }

    private void AddControlledTab(Runtime runtime, BrowserPageSnapshot page)
    {
        if (runtime.ControlledTabs.Add(page.TabId))
        {
            var domain = Uri.TryCreate(page.Url, UriKind.Absolute, out var uri)
                ? uri.Host
                : "";
            runtime.History.Add(
                $"Task-owned tab registered: tab:{page.TabId}; title={Short(page.Title, 100)}; domain={Short(domain, 80)}. Use activate_tab with this exact targetId to return to it.");
        }
        Publish(runtime);
    }

    private void Publish(Runtime runtime)
    {
        var snapshot = BuildSnapshot(runtime);
        if (snapshot == null)
            return;
        lock (_sync)
            _last = snapshot;
        StatusChanged?.Invoke(snapshot);
    }

    private static BrowserAutomationSnapshot BuildSnapshot(Runtime runtime)
    {
        if (runtime == null)
            return null;
        return new BrowserAutomationSnapshot
        {
            TaskId = runtime.TaskId,
            Request = runtime.Request,
            State = runtime.State,
            CurrentAction = runtime.CurrentAction,
            CurrentDomain = SafeDomain(runtime.Page?.Url),
            CurrentTabTitle = Short(runtime.Page?.Title, 160),
            WindowId = runtime.Page?.WindowId,
            TabId = runtime.Page?.TabId,
            ControlledTabIds = runtime.ControlledTabs.OrderBy(id => id).ToArray(),
            StartedUtc = runtime.StartedUtc,
            UpdatedUtc = DateTime.UtcNow,
            CanPause = !IsTerminal(runtime.State)
                && !runtime.UserPaused
                && runtime.State != BrowserTaskState.NeedsUserAction,
            CanResume = runtime.UserPaused
                || runtime.State == BrowserTaskState.NeedsUserAction,
            CanCancel = !IsTerminal(runtime.State),
            VisionRecovery = runtime.VisionRecovery,
            InterventionKind = runtime.State == BrowserTaskState.NeedsUserAction
                ? ClassifyIntervention(runtime.CurrentAction)
                : "",
            InterventionReason = runtime.State == BrowserTaskState.NeedsUserAction
                ? runtime.CurrentAction
                : "",
            LastError = runtime.LastError,
            Outcome = runtime.Outcome,
            ResultSummary = runtime.ResultSummary,
        };
    }

    private static bool IsTerminal(BrowserTaskState state) =>
        state is BrowserTaskState.Completed
            or BrowserTaskState.CompletedWithNote
            or BrowserTaskState.Partial
            or BrowserTaskState.Blocked
            or BrowserTaskState.Cancelled
            or BrowserTaskState.Failed;

    internal static bool ShouldAnnounceOutcome(
        BrowserTaskState state,
        bool suppressCancellationAnnouncement) =>
        state != BrowserTaskState.Cancelled || !suppressCancellationAnnouncement;

    internal static bool IsMissingTaskTabError(string error) =>
        !string.IsNullOrWhiteSpace(error)
        && (error.Contains("No tab with id", StringComparison.OrdinalIgnoreCase)
            || error.Contains(
                "tab is not owned by this browser task",
                StringComparison.OrdinalIgnoreCase));

    internal static bool ShouldRecoverMissingTaskTab(
        string actionType,
        string error,
        bool recoveryAlreadyAttempted) =>
        !recoveryAlreadyAttempted
        && actionType is "navigate" or "open_tab"
        && IsMissingTaskTabError(error);

    internal static string ClassifyIntervention(string reason)
    {
        var value = reason ?? "";
        if (Regex.IsMatch(value, @"\b(?:captcha|verification challenge)\b", RegexOptions.IgnoreCase))
            return "captcha_required";
        if (Regex.IsMatch(value, @"\b(?:sign[- ]?in|log[- ]?in|login|authenticated?)\b", RegexOptions.IgnoreCase))
            return "login_required";
        if (Regex.IsMatch(value, @"\b(?:confirm|confirmation|approve|approval)\b", RegexOptions.IgnoreCase))
            return "confirmation_required";
        if (Regex.IsMatch(value, @"\b(?:permission|allow access|grant access)\b", RegexOptions.IgnoreCase))
            return "permission_required";
        if (Regex.IsMatch(value, @"\b(?:which|choose|ambiguous|specify|provide the (?:url|target))\b", RegexOptions.IgnoreCase))
            return "ambiguous_target";
        if (Regex.IsMatch(value, @"\b(?:unsupported|not supported|cannot automate)\b", RegexOptions.IgnoreCase))
            return "unsupported_interaction";
        return "manual_step";
    }

    internal static string BuildInterventionSpeech(string reason)
    {
        var step = string.IsNullOrWhiteSpace(reason)
            ? "Complete the required step in Chrome."
            : Short(reason.Trim(), 320);
        return $"Browser automation is paused. {step} Then say continue to resume the same task, or say cancel browser automation.";
    }

    internal static bool ShouldForceVisualRecovery(
        string request,
        BrowserPageSnapshot page,
        BrowserPlannerDecision decision,
        bool visionRecoveryAlreadyUsed)
    {
        if (visionRecoveryAlreadyUsed
            || page?.Controls?.Length != 0
            || decision == null
            || !BrowserAutomationTaskClassifier.RequiresPageMutation(request))
        {
            return false;
        }

        var proposedTerminalOrBlocker = decision.Done
            || decision.NeedsUserAction
            || decision.Action?.Type is "done" or "needs_user_action";
        if (!proposedTerminalOrBlocker)
            return false;

        var blocker = $"{decision.UserAction} {decision.Evaluation}";
        return !Regex.IsMatch(
            blocker,
            @"\b(?:captcha|password|passcode|otp|two[- ]factor|2fa|sign[- ]?in|log[- ]?in|payment|card|bank|security challenge)\b",
            RegexOptions.IgnoreCase);
    }

    private static string SafeDomain(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";

    private static string FriendlyError(Exception ex) => ex switch
    {
        TimeoutException => "The browser task timed out. Chrome and its tabs were left open.",
        HttpRequestException => "The browser planner is temporarily unavailable. Chrome and its tabs were left open.",
        InvalidOperationException => Short(ex.Message, 240),
        _ => "The browser task stopped safely because an unexpected error occurred.",
    };

    private static string Short(string value, int length) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= length
            ? value ?? ""
            : value[..length];

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { CancelAsync("Jarvis is closing").GetAwaiter().GetResult(); } catch { }
        _planner.Dispose();
        _bridge.Dispose();
    }

    private sealed class AsyncPauseGate
    {
        private readonly object _lock = new();
        private TaskCompletionSource _resume = Completed();

        public void Pause()
        {
            lock (_lock)
            {
                if (_resume.Task.IsCompleted)
                    _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Resume()
        {
            lock (_lock)
                _resume.TrySetResult();
        }

        public Task WaitAsync(CancellationToken token)
        {
            Task task;
            lock (_lock)
                task = _resume.Task;
            return task.WaitAsync(token);
        }

        private static TaskCompletionSource Completed()
        {
            var source = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
