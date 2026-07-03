namespace KeyboardWtf.Services;

using System.Diagnostics;
using KeyboardWtf.Automation.Input;
using KeyboardWtf.Models;

public sealed class BrowserAgentService : IAsyncDisposable
{
    private const int MaxActionsPerTask = 30;
    private readonly SettingsService _settings;
    private readonly JarvisTaskManager _tasks;
    private readonly TaskContextCaptureService _contextCapture;
    private readonly IBrowserAutomationBackend _backend;
    private readonly BrowserAgentRouter _router;
    private readonly ISmartBrowserTaskBackend _smartBackend;
    private readonly BrowserWorkflowMemoryService _workflowMemory;
    private readonly BrowserCompanionBridgeService _companionBridge;
    private readonly KeyboardNavigationExecutor _keyboardNavigation;
    private readonly object _pendingLock = new();
    private readonly Dictionary<string, BrowserAgentActionRequest> _pendingConfirmations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BrowserAgentActionRequest> _pendingClarifications =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BrowserTaskRequest> _pendingStarts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BrowserTaskRequest> _pendingSmartStarts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BrowserAgentRouteDecision> _pendingSmartRoutes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _actionCounts = new(StringComparer.OrdinalIgnoreCase);

    public BrowserAgentService(
        SettingsService settings,
        JarvisTaskManager tasks,
        TaskContextCaptureService contextCapture = null,
        IBrowserAutomationBackend backend = null,
        BrowserAgentRouter router = null,
        ISmartBrowserTaskBackend smartBackend = null,
        BrowserWorkflowMemoryService workflowMemory = null,
        BrowserCompanionBridgeService companionBridge = null)
    {
        _settings = settings;
        _tasks = tasks;
        _contextCapture = contextCapture ?? new TaskContextCaptureService();
        _backend = backend ?? new PlaywrightBrowserBackend();
        _router = router ?? new BrowserAgentRouter(settings);
        _smartBackend = smartBackend ?? new BrowserUseSidecarBackend();
        _workflowMemory = workflowMemory;
        _companionBridge = companionBridge;
        _keyboardNavigation = companionBridge == null
            ? null
            : new KeyboardNavigationExecutor(
                new WindowsInputController(),
                new BrowserCompanionFocusInspector(companionBridge));
        _tasks.ConfirmationResponded += OnConfirmationResponded;
        _tasks.ClarificationResponded += OnClarificationResponded;
    }

    public BrowserBackendStatus Status
    {
        get
        {
            var direct = _backend.Status;
            var companion = _companionBridge?.Status;
            var smart = _smartBackend?.Status;
            var current = _settings.Current;
            if (current.BrowserAgentEnabled
                && current.BrowserCompanionEnabled
                && companion?.Initialized == true)
            {
                return new BrowserBackendStatus
                {
                    Initialized = true,
                    Available = companion.Available || (_settings.Current.AllowAutomationProfileFallback && direct.Available),
                    BrowserChannel = companion.Available
                        ? $"{companion.BrowserChannel} (Direct Playwright fallback)"
                        : current.AllowAutomationProfileFallback
                            ? $"{(string.IsNullOrWhiteSpace(direct.BrowserChannel) ? "Direct Playwright" : direct.BrowserChannel)} fallback; companion disconnected"
                            : "Browser Companion disconnected; automation profile fallback disabled",
                    ProfilePath = companion.ProfilePath,
                    LastError = companion.Available ? "" : companion.LastError,
                };
            }
            if (current.BrowserAgentEnabled
                && current.BrowserUseEnabled
                && smart?.Initialized == true)
            {
                return new BrowserBackendStatus
                {
                    Initialized = true,
                    Available = smart.Available || direct.Available,
                    BrowserChannel = smart.Available
                        ? $"{smart.BrowserChannel} (Direct Playwright fallback)"
                        : $"{direct.BrowserChannel} fallback",
                    ProfilePath = smart.ProfilePath,
                    LastError = smart.Available ? "" : smart.LastError,
                };
            }

            return direct;
        }
    }

    public async Task<BrowserAgentResult> StartAsync(
        BrowserTaskRequest request,
        CancellationToken token)
    {
        if (!_settings.Current.BrowserAgentEnabled)
            return Failure("Jarvis Browser Agent is disabled in Settings.");
        if (request == null || string.IsNullOrWhiteSpace(request.OriginalUserCommand))
            return Failure("A browser task description is required.");
        request = NormalizeRequest(request);
        if (!BrowserAgentSafetyPolicy.IsSafeHttpUrl(request.StartUrl, out _))
            return Failure("A safe http or https start URL is required. Jarvis could not infer the target website from this browser task.");
        var route = ChooseRoute(request);

        JarvisTask task;
        try
        {
            task = _tasks.Create(
                request.Title,
                request.OriginalUserCommand,
                [
                    "Capture requested context",
                    "Open controlled browser",
                    "Inspect the page",
                    "Perform safe browser actions",
                    "Verify the result",
                ],
                ["Controlled browser access"],
                TimeSpan.FromMinutes(15));
        }
        catch (JarvisTaskCapacityException ex)
        {
            return Failure(ex.Message);
        }

        _tasks.Start(task.TaskId);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _tasks.TokenFor(task.TaskId));
        try
        {
            _tasks.SetStepByIndex(task.TaskId, 0, JarvisTaskStepStatus.Running);
            if (request.ContextExplicitlyRequested)
            {
                var context = await _contextCapture.CaptureAsync(new TaskContextRequest
                {
                    UserExplicitlyRequested = true,
                    UseSelectedText = request.UseSelectedText,
                    UseClipboard = request.UseClipboard,
                    IncludeActiveWindow = true,
                }, linked.Token);
                if (!context.Captured && (request.UseSelectedText || request.UseClipboard))
                {
                    lock (_pendingLock)
                        _pendingStarts[task.TaskId] = request;
                    var waiting = _tasks.WaitForClarification(
                        task.TaskId,
                        task.Steps[0].Id,
                        context.Error,
                        ["Select the text and try again", "Continue without private context", "Cancel"]);
                    return new BrowserAgentResult
                    {
                        Ok = false,
                        TaskId = task.TaskId,
                        Message = context.Error,
                        ClarificationRequired = true,
                        Task = waiting,
                    };
                }
                var contextSummary = BuildContextSummary(context);
                _tasks.SetContext(task.TaskId, contextSummary);
                _tasks.AddReceipt(task.TaskId, ContextReceipt(context));
                _tasks.SetStepByIndex(task.TaskId, 0, JarvisTaskStepStatus.Completed, ContextReceipt(context));
            }
            else
            {
                _tasks.SetStepByIndex(
                    task.TaskId,
                    0,
                    JarvisTaskStepStatus.Completed,
                    "No private context was requested.");
            }
            return await StartRoutedBrowserTaskAsync(task.TaskId, request, route, linked.Token);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(task.TaskId);
        }
        catch (Exception ex)
        {
            _tasks.Fail(task.TaskId, ex.Message);
            return Failure(ex.Message, task.TaskId);
        }
        finally
        {
            linked.Dispose();
        }
    }

    public async Task<BrowserAgentResult> ObserveAsync(string taskId, CancellationToken token)
    {
        var task = _tasks.Get(taskId);
        if (task == null)
            return Failure("Browser task was not found.");
        try
        {
            var page = await _backend.ObserveAsync(taskId, token);
            _tasks.SetStepByIndex(taskId, 2, JarvisTaskStepStatus.Completed, PageSummary(page));
            return new BrowserAgentResult
            {
                Ok = true,
                TaskId = taskId,
                Message = "Current controlled page inspected.",
                Page = page,
                Task = _tasks.Get(taskId),
            };
        }
        catch (Exception ex)
        {
            return Failure(ex.Message, taskId);
        }
    }

    public async Task<BrowserAgentResult> ActAsync(
        BrowserAgentActionRequest request,
        CancellationToken token)
    {
        var task = _tasks.Get(request?.TaskId);
        if (task == null)
            return Failure("Browser task was not found.");
        if (!_tasks.IsRunnable(task.TaskId))
        {
            return new BrowserAgentResult
            {
                Ok = false,
                TaskId = task.TaskId,
                Message = $"The task is {task.Status}.",
                ConfirmationRequired = task.Status == JarvisTaskStatus.WaitingForConfirmation,
                ClarificationRequired = task.Status == JarvisTaskStatus.WaitingForClarification,
                Task = task,
            };
        }

        lock (_pendingLock)
        {
            var count = _actionCounts.TryGetValue(task.TaskId, out var existing) ? existing + 1 : 1;
            _actionCounts[task.TaskId] = count;
            if (count > MaxActionsPerTask)
            {
                _tasks.Fail(task.TaskId, "The browser task exceeded its 30-action safety limit.");
                return Failure("The browser task exceeded its 30-action safety limit.", task.TaskId);
            }
        }

        var decision = BrowserAgentSafetyPolicy.Evaluate(request, task.OriginalUserCommand);
        if (decision.Risk == BrowserActionRisk.Blocked)
            return Failure(decision.Reason, task.TaskId);
        if (decision.Risk == BrowserActionRisk.ManualOnly)
        {
            var waiting = _tasks.WaitForClarification(
                task.TaskId,
                task.Steps[3].Id,
                decision.Reason + " Complete it manually in the controlled browser, then tell Jarvis what happened.",
                ["I completed it manually", "Cancel the task"]);
            return new BrowserAgentResult
            {
                Ok = false,
                TaskId = task.TaskId,
                Message = decision.Reason,
                ClarificationRequired = true,
                ManualActionRequired = true,
                Task = waiting,
            };
        }
        if (decision.Risk == BrowserActionRisk.ConfirmationRequired)
        {
            lock (_pendingLock)
                _pendingConfirmations[task.TaskId] = request;
            var waiting = _tasks.WaitForConfirmation(
                task.TaskId,
                task.Steps[3].Id,
                $"{decision.Reason} Confirm: {Describe(request)}",
                Describe(request));
            return new BrowserAgentResult
            {
                Ok = false,
                TaskId = task.TaskId,
                Message = decision.Reason,
                ConfirmationRequired = true,
                Task = waiting,
            };
        }
        return await ExecuteApprovedAsync(request, decision, token);
    }

    public async Task<BrowserAgentResult> FinishAsync(
        string taskId,
        string expectedText,
        string outcome,
        CancellationToken token)
    {
        var task = _tasks.Get(taskId);
        if (task == null)
            return Failure("Browser task was not found.");
        try
        {
            _tasks.SetStepByIndex(taskId, 4, JarvisTaskStepStatus.Running);
            var page = await _backend.ObserveAsync(taskId, token);
            var verified = !string.IsNullOrWhiteSpace(expectedText)
                ? page.VisibleText.Contains(expectedText, StringComparison.OrdinalIgnoreCase)
                : page.Loaded && !string.IsNullOrWhiteSpace(page.Title);
            if (!verified)
            {
                _tasks.SetStepByIndex(
                    taskId,
                    4,
                    JarvisTaskStepStatus.Failed,
                    $"Could not verify {expectedText}.");
                _tasks.Fail(taskId, "The requested outcome could not be verified on the page.");
                return new BrowserAgentResult
                {
                    Ok = false,
                    TaskId = taskId,
                    Message = "The requested outcome could not be verified on the page.",
                    Page = page,
                    Task = _tasks.Get(taskId),
                };
            }
            var result = string.IsNullOrWhiteSpace(outcome)
                ? $"Verified the task on {page.Title}."
                : outcome.Trim();
            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Completed, "Safe actions completed.");
            _tasks.SetStepByIndex(taskId, 4, JarvisTaskStepStatus.Completed, $"Verified on {page.Url}.");
            _tasks.AddReceipt(taskId, $"Verified final page: {page.Title} ({page.Url}).");
            _tasks.Complete(taskId, result);
            return new BrowserAgentResult
            {
                Ok = true,
                TaskId = taskId,
                Message = result,
                Page = page,
                Task = _tasks.Get(taskId),
            };
        }
        catch (Exception ex)
        {
            _tasks.Fail(taskId, ex.Message);
            return Failure(ex.Message, taskId);
        }
    }

    public async Task<BrowserAgentResult> CancelAsync(string taskId)
    {
        var task = _tasks.Cancel(taskId, "Browser task cancelled.");
        await _backend.CloseTaskAsync(taskId);
        lock (_pendingLock)
        {
            _pendingConfirmations.Remove(taskId);
            _pendingClarifications.Remove(taskId);
            _pendingStarts.Remove(taskId);
            _pendingSmartStarts.Remove(taskId);
            _pendingSmartRoutes.Remove(taskId);
            _actionCounts.Remove(taskId);
        }
        return new BrowserAgentResult
        {
            Ok = task != null,
            TaskId = taskId,
            Message = task == null ? "Browser task was not found." : "Browser task cancelled.",
            Task = task,
        };
    }

    private BrowserAgentRouteDecision ChooseRoute(BrowserTaskRequest request)
    {
        var uriHost = BrowserAgentSafetyPolicy.IsSafeHttpUrl(request.StartUrl, out var uri)
            ? uri.Host
            : "";
        return _router.Choose(new BrowserAgentRouteInput
        {
            Task = request.OriginalUserCommand,
            StartUrl = request.StartUrl,
            Domain = uriHost,
            BrowserUseAvailable = request.UseSmartBackend && _smartBackend?.Status.Available == true,
            BrowserCompanionAvailable = request.UseSmartBackend && _companionBridge?.Status.Available == true,
            BrowserOSAvailable = false,
            AutomationProfileFallbackAllowed = request.AutomationProfileFallbackAllowed || _settings.Current.AllowAutomationProfileFallback,
            DirectDeterministicEnough = !request.UseSmartBackend || IsDirectStartEnough(request),
            KnownWorkflowMatch = _settings.Current.LearnBrowserWorkflows
                && (_workflowMemory?.FindBestMatch(uriHost, request.OriginalUserCommand).HighConfidence == true),
            RequiresLoggedInSession = MentionsLoggedInContext(request.OriginalUserCommand),
            RiskyActionConfirmed = request.RiskyActionConfirmed,
        });
    }

    private async Task<BrowserAgentResult> StartRoutedBrowserTaskAsync(
        string taskId,
        BrowserTaskRequest request,
        BrowserAgentRouteDecision route,
        CancellationToken token)
    {
        if (route.Safety.Risk is BrowserActionRisk.Blocked or BrowserActionRisk.ManualOnly)
        {
            var waiting = _tasks.WaitForClarification(
                taskId,
                _tasks.Get(taskId).Steps[3].Id,
                route.Safety.Reason + " Complete that part manually, then tell Jarvis what happened.",
                ["I completed it manually", "Cancel the task"]);
            return new BrowserAgentResult
            {
                Ok = false,
                TaskId = taskId,
                Message = route.Safety.Reason,
                ClarificationRequired = true,
                ManualActionRequired = true,
                Task = waiting,
            };
        }

        if (route.SelectedBackend == BrowserAgentBackendKind.BrowserCompanionExtension)
            return await ExecuteCompanionTaskAsync(taskId, request, route, token);

        if (ShouldBlockAutomationProfileFallback(request, route))
        {
            var message = "Browser Companion Extension is not connected. Connect the extension in your active browser, or explicitly enable automation profile fallback in Settings.";
            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Failed, message);
            _tasks.Fail(taskId, message);
            return Failure(message, taskId);
        }

        if (!request.UseSmartBackend || !route.UseSmartTaskRunner)
            return await OpenInitialPageAsync(taskId, request, token);

        if (route.RequiresBrowserExtension && MentionsLoggedInContext(request.OriginalUserCommand))
        {
            _tasks.AddReceipt(taskId, "Active logged-in browser control needs the future browser-extension bridge; this run uses the dedicated automation profile only.");
        }

        if (route.Safety.Risk == BrowserActionRisk.ConfirmationRequired && !request.RiskyActionConfirmed)
        {
            lock (_pendingLock)
            {
                _pendingSmartStarts[taskId] = request;
                _pendingSmartRoutes[taskId] = route;
            }
            var waiting = _tasks.WaitForConfirmation(
                taskId,
                _tasks.Get(taskId).Steps[3].Id,
                route.Safety.Reason + " Jarvis will still stop before checkout, payment, booking finalization, publishing, sending, deletion, or account changes.",
                "Run smart browser task with the confirmed safe boundary");
            return new BrowserAgentResult
            {
                Ok = false,
                TaskId = taskId,
                Message = route.Safety.Reason,
                ConfirmationRequired = true,
                Task = waiting,
            };
        }

        return await ExecuteSmartTaskAsync(taskId, request, route, token);
    }

    private async Task<BrowserAgentResult> ExecuteCompanionTaskAsync(
        string taskId,
        BrowserTaskRequest request,
        BrowserAgentRouteDecision route,
        CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var telemetry = new BrowserCompanionTelemetry();
        BrowserWorkflowMatch workflowMatch = null;
        try
        {
            if (_companionBridge?.Status.Available != true)
            {
                _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Failed, "Browser Companion Extension is not connected.");
                _tasks.Fail(taskId, "Browser Companion Extension is not connected. Load the extension and keep a browser tab active.");
                return Failure("Browser Companion Extension is not connected. Load the extension and keep a browser tab active.", taskId);
            }

            if (route.Safety.Risk == BrowserActionRisk.ConfirmationRequired && !request.RiskyActionConfirmed)
            {
                lock (_pendingLock)
                {
                    _pendingSmartStarts[taskId] = request;
                    _pendingSmartRoutes[taskId] = route;
                }
                var waiting = _tasks.WaitForConfirmation(
                    taskId,
                    _tasks.Get(taskId).Steps[3].Id,
                    route.Safety.Reason + " Jarvis will still stop before checkout, payment, booking finalization, publishing, sending, deletion, or account changes.",
                    "Run browser companion task with the confirmed safe boundary");
                return new BrowserAgentResult
                {
                    Ok = false,
                    TaskId = taskId,
                    Message = route.Safety.Reason,
                    ConfirmationRequired = true,
                    Task = waiting,
                };
            }

            _tasks.SetStepByIndex(taskId, 1, JarvisTaskStepStatus.Running, "Using the Browser Companion Extension in the active browser.");
            _tasks.SetStepByIndex(taskId, 2, JarvisTaskStepStatus.Running, "Building the Page Control Graph from visible DOM controls.");
            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Running, "Choosing deterministic controls from the graph.");

            var domain = BrowserAgentSafetyPolicy.IsSafeHttpUrl(request.StartUrl, out var uri)
                ? uri.Host
                : "";
            var siteSkillType = CanRunYouTubeOldestVideo(request.OriginalUserCommand)
                ? "youtube_play_oldest_video"
                : CanRunEcommerceCartTask(request.OriginalUserCommand)
                    ? "ecommerce_add_to_cart"
                    : "";
            workflowMatch = _settings.Current.LearnBrowserWorkflows
                ? _workflowMemory?.FindBestMatch(domain, request.OriginalUserCommand, siteSkillType)
                : null;
            telemetry.ReplayUsed = false;

            if (CanRunYouTubeOldestVideo(request.OriginalUserCommand))
            {
                var result = await RunYouTubeOldestVideoAsync(taskId, request, telemetry, workflowMatch, stopwatch, token);
                stopwatch.Stop();
                telemetry.RuntimeMs = (int)stopwatch.ElapsedMilliseconds;
                AddCompanionTelemetry(taskId, telemetry);
                return result;
            }

            if (CanRunEcommerceCartTask(request.OriginalUserCommand))
            {
                var result = await RunEcommerceCartTaskAsync(taskId, request, telemetry, workflowMatch, stopwatch, token);
                stopwatch.Stop();
                telemetry.RuntimeMs = (int)stopwatch.ElapsedMilliseconds;
                AddCompanionTelemetry(taskId, telemetry);
                return result;
            }

            var graphResult = await ExecuteCompanionCommandAsync(
                taskId,
                "getGraph",
                telemetry,
                token,
                timeout: TimeSpan.FromSeconds(12));
            if (graphResult.Graph != null)
                _tasks.SetStepByIndex(taskId, 2, JarvisTaskStepStatus.Completed, GraphSummary(graphResult.Graph));

            var message = "Browser Companion captured the page graph, but no reusable site skill exists yet for this task. It will not fall back to slow browser-use automatically.";
            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Failed, message);
            _tasks.Fail(taskId, message);
            stopwatch.Stop();
            telemetry.RuntimeMs = (int)stopwatch.ElapsedMilliseconds;
            AddCompanionTelemetry(taskId, telemetry);
            return Failure(message, taskId);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            telemetry.RuntimeMs = (int)stopwatch.ElapsedMilliseconds;
            AddCompanionTelemetry(taskId, telemetry);
            var task = _tasks.Timeout(taskId, "Browser Companion task timed out or was cancelled.");
            return new BrowserAgentResult
            {
                Ok = false,
                TaskId = taskId,
                Message = "Browser Companion task timed out or was cancelled.",
                Task = task,
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            telemetry.RuntimeMs = (int)stopwatch.ElapsedMilliseconds;
            AddCompanionTelemetry(taskId, telemetry);
            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Failed, ex.Message);
            _tasks.Fail(taskId, ex.Message);
            if (workflowMatch?.Workflow != null)
                _workflowMemory?.RecordFailure(workflowMatch.Workflow.WorkflowId);
            return Failure(ex.Message, taskId);
        }
    }

    private async Task<BrowserAgentResult> RunYouTubeOldestVideoAsync(
        string taskId,
        BrowserTaskRequest request,
        BrowserCompanionTelemetry telemetry,
        BrowserWorkflowMatch workflowMatch,
        Stopwatch stopwatch,
        CancellationToken token)
    {
        var channel = ExtractYouTubeChannelHandle(request.OriginalUserCommand);
        if (string.IsNullOrWhiteSpace(channel))
            return FailCompanionTask(taskId, "I could not identify the YouTube channel/entity from the request.");

        var youtubeDomain = BrowserAgentSafetyPolicy.IsSafeHttpUrl(request.StartUrl, out var startUri)
            ? startUri.Host
            : "www.youtube.com";
        var exactReplay = _settings.Current.LearnBrowserWorkflows
            ? _workflowMemory?.FindExactSuccessfulInstance(youtubeDomain, "youtube_play_oldest_video", channel)
            : null;
        telemetry.ReplayUsed = exactReplay?.Workflow != null;
        _tasks.AddReceipt(
            taskId,
            $"Browser memory lookup: parsed entity={channel}; exact replay matched={(telemetry.ReplayUsed ? "yes" : "no")}; template matched={(WorkflowMatchesType(workflowMatch, "youtube_play_oldest_video") ? "yes" : "no")}.");
        if (telemetry.ReplayUsed
            && await TryReplayYouTubeWorkflowAsync(taskId, exactReplay, channel, telemetry, token))
        {
            var replayMessage = $"Replayed the saved YouTube workflow for @{channel}.";
            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Completed, "Opened the saved verified watch URL.");
            _tasks.SetStepByIndex(taskId, 4, JarvisTaskStepStatus.Completed, "Verified YouTube watch/player page.");
            telemetry.RuntimeMs = (int)stopwatch.ElapsedMilliseconds;
            var replayWorkflowSaved = RememberSuccessfulYouTubeWorkflow(
                taskId,
                request,
                _companionBridge?.LastGraph,
                exactReplay,
                telemetry,
                channel,
                ["Replay saved watch URL", "Verify watch/player page"],
                ["youtube_replay_saved_watch_url"],
                exactReplay.Workflow.SelectorsOrLocatorHints);
            telemetry.WorkflowSaved = replayWorkflowSaved;
            _tasks.Complete(taskId, replayMessage);
            return new BrowserAgentResult
            {
                Ok = true,
                TaskId = taskId,
                Message = replayMessage,
                Task = _tasks.Get(taskId),
            };
        }

        var url = $"https://www.youtube.com/@{channel}/videos";
        var navigate = await ExecuteCompanionCommandAsync(
            taskId,
            "navigate",
            telemetry,
            token,
            url,
            timeout: TimeSpan.FromSeconds(20));
        if (!navigate.Ok)
            return FailCompanionTask(taskId, navigate.Message);

        var graph = navigate.Graph ?? _companionBridge?.LastGraph;
        if (graph == null)
            return FailCompanionTask(taskId, "Browser Companion did not return a Page Control Graph after navigation.");
        graph = await WaitForGraphConditionAsync(
            taskId,
            telemetry,
            token,
            graph,
            candidate => candidate.Controls.Count >= 20
                || candidate.Controls.Any(control => TextEquals(control, "oldest"))
                || FindFirstVideoCard(candidate) != null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(800));

        _tasks.SetStepByIndex(taskId, 1, JarvisTaskStepStatus.Completed, graph.Title);
        _tasks.SetStepByIndex(taskId, 2, JarvisTaskStepStatus.Completed, GraphSummary(graph));

        var videos = FindControl(graph, control =>
            TextContains(control, "videos")
            && IsClickableRole(control)
            && !TextContains(control, "shorts"));
        if (videos != null && !graph.Url.Contains("/videos", StringComparison.OrdinalIgnoreCase))
        {
            graph = await ClickAndGraph(taskId, videos, telemetry, token);
        }

        var oldest = FindControl(graph, control =>
            TextEquals(control, "oldest")
            && IsClickableRole(control));
        if (oldest == null)
        {
            var sort = FindControl(graph, control =>
                TextContains(control, "sort")
                && IsClickableRole(control));
            if (sort != null)
                graph = await ClickAndGraph(taskId, sort, telemetry, token);
            oldest = FindControl(graph, control =>
                TextEquals(control, "oldest")
                && IsClickableRole(control));
        }
        if (oldest == null)
        {
            _tasks.AddReceipt(taskId, "YouTube Oldest control was not exposed in the fresh graph; using YouTube videos sort URL fallback for the current channel.");
            var sortedUrl = $"https://www.youtube.com/@{channel}/videos?view=0&sort=da";
            var sorted = await ExecuteCompanionCommandAsync(
                taskId,
                "navigate",
                telemetry,
                token,
                sortedUrl,
                timeout: TimeSpan.FromSeconds(18));
            if (!sorted.Ok)
                return FailCompanionTask(taskId, sorted.Message);
            graph = sorted.Graph ?? _companionBridge?.LastGraph;
        }
        else
        {
            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Running, "Clicking YouTube sort control: Oldest.");
            graph = await ClickAndGraph(taskId, oldest, telemetry, token, waitMs: 2200);
        }
        graph = await WaitForGraphConditionAsync(
            taskId,
            telemetry,
            token,
            graph,
            candidate => FindFirstVideoCard(candidate) != null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(800));

        var firstVideo = FindFirstVideoCard(graph);
        for (var scrollAttempt = 0; firstVideo == null && scrollAttempt < 3; scrollAttempt++)
        {
            graph = await ScrollAndGraph(taskId, telemetry, token);
            graph = await WaitForGraphConditionAsync(
                taskId,
                telemetry,
                token,
                graph,
                candidate => FindFirstVideoCard(candidate) != null,
                TimeSpan.FromSeconds(4),
                TimeSpan.FromMilliseconds(800));
            firstVideo = FindFirstVideoCard(graph);
        }
        if (firstVideo == null)
        {
            _workflowMemory?.RecordFailureHint(
                youtubeDomain,
                "youtube_play_oldest_video",
                channel,
                request.OriginalUserCommand,
                graph.Url,
                "Oldest sort succeeded but refreshed graph did not expose a video card.",
                graph.PageSignals);
            return FailCompanionTask(taskId, "The YouTube page graph did not expose a playable first video card after sorting Oldest.");
        }

        _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Running, "Opening the first video card after Oldest sort.");
        graph = await ClickAndGraph(taskId, firstVideo, telemetry, token, waitMs: 2000);

        var verified = graph.Url.Contains("/watch", StringComparison.OrdinalIgnoreCase)
            || graph.Controls.Any(control => TextContains(control, "play") && control.Role.Contains("button", StringComparison.OrdinalIgnoreCase))
            || graph.PageType.Equals("youtube_watch", StringComparison.OrdinalIgnoreCase);
        if (!verified)
        {
            _workflowMemory?.RecordFailureHint(
                youtubeDomain,
                "youtube_play_oldest_video",
                channel,
                request.OriginalUserCommand,
                graph.Url,
                "Could not verify YouTube watch/player page after clicking first video.",
                graph.PageSignals);
            return FailCompanionTask(taskId, "The companion clicked a video card, but could not verify a YouTube watch/player page.");
        }

        telemetry.RuntimeMs = (int)stopwatch.ElapsedMilliseconds;
        var workflowSaved = RememberSuccessfulYouTubeWorkflow(
            taskId,
            request,
            graph,
            workflowMatch,
            telemetry,
            channel,
            [
                $"Navigate to @{channel}/videos",
                "Click Oldest sort control from Page Control Graph",
                "Click first video card from refreshed graph",
                "Verify watch page or player controls",
            ],
            [
                $"youtube_channel:{channel}",
                "youtube_pattern:channel_videos_sort_oldest_open_first_watch",
                "youtube_find_channel",
                "youtube_sort_videos_oldest",
                "youtube_play_video",
            ],
            [
                $"youtube_watch_url:{channel}|{graph.Url}",
                ControlSignature(oldest),
                ControlSignature(firstVideo),
            ]);
        telemetry.WorkflowSaved = workflowSaved;

        _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Completed, "Clicked Oldest and opened the first video card.");
        _tasks.SetStepByIndex(taskId, 4, JarvisTaskStepStatus.Completed, "Verified YouTube video/player page.");
        var message = $"Opened the oldest visible @{channel} video on YouTube.";
        _tasks.Complete(taskId, message);
        return new BrowserAgentResult
        {
            Ok = true,
            TaskId = taskId,
            Message = message,
            Task = _tasks.Get(taskId),
        };
    }

    private async Task<bool> TryReplayYouTubeWorkflowAsync(
        string taskId,
        BrowserWorkflowMatch workflowMatch,
        string channel,
        BrowserCompanionTelemetry telemetry,
        CancellationToken token)
    {
        if (workflowMatch?.Workflow == null)
            return false;
        var replayUrl = FindYouTubeReplayUrl(workflowMatch.Workflow, channel);
        if (string.IsNullOrWhiteSpace(replayUrl))
            return false;

        try
        {
            _tasks.SetStepByIndex(taskId, 1, JarvisTaskStepStatus.Running, "Replaying saved Browser Companion workflow.");
            var navigate = await ExecuteCompanionCommandAsync(
                taskId,
                "navigateFast",
                telemetry,
                token,
                replayUrl,
                timeout: TimeSpan.FromSeconds(8));
            var graph = navigate.Graph ?? _companionBridge?.LastGraph;
            var verified = graph != null
                && (graph.Url.Contains("/watch", StringComparison.OrdinalIgnoreCase)
                    || graph.PageType.Equals("youtube_watch", StringComparison.OrdinalIgnoreCase)
                    || graph.Controls.Any(control => TextContains(control, "play") && control.Role.Contains("button", StringComparison.OrdinalIgnoreCase)));
            if (!verified)
            {
                telemetry.RepairUsed = true;
                _tasks.AddReceipt(
                    taskId,
                    $"Saved workflow replay failed; repairing with fresh Page Control Graph. status={navigate.Status}; message={navigate.Message}; url={graph?.Url ?? ""}");
                _workflowMemory?.RecordFailure(workflowMatch.Workflow.WorkflowId);
                return false;
            }

            _tasks.SetStepByIndex(taskId, 1, JarvisTaskStepStatus.Completed, graph.Title);
            _tasks.SetStepByIndex(taskId, 2, JarvisTaskStepStatus.Completed, GraphSummary(graph));
            return true;
        }
        catch (Exception ex)
        {
            telemetry.RepairUsed = true;
            _tasks.AddReceipt(taskId, $"Saved workflow replay failed; repairing with fresh Page Control Graph. {ex.Message}");
            _workflowMemory?.RecordFailure(workflowMatch.Workflow.WorkflowId);
            return false;
        }
    }

    private static string ExtractReplayUrl(string value)
    {
        var text = value ?? "";
        const string NavigatePrefix = "navigate:";
        const string WatchPrefix = "watch_url:";
        if (text.StartsWith(NavigatePrefix, StringComparison.OrdinalIgnoreCase))
            return text[NavigatePrefix.Length..].Trim();
        if (text.StartsWith(WatchPrefix, StringComparison.OrdinalIgnoreCase))
            return text[WatchPrefix.Length..].Trim();
        return "";
    }

    private static string FindYouTubeReplayUrl(BrowserWorkflowMemory workflow, string channel)
    {
        if (workflow == null || string.IsNullOrWhiteSpace(channel))
            return "";
        if (workflow.MemoryKind == BrowserWorkflowMemoryKind.ExactSuccessfulInstance
            && string.Equals(
                FuzzyMatcher.Normalize(workflow.EntityKey).Replace(" ", "", StringComparison.Ordinal),
                FuzzyMatcher.Normalize(channel).Replace(" ", "", StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase)
            && workflow.FinalUrl.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase))
        {
            return workflow.FinalUrl;
        }

        var normalizedChannel = FuzzyMatcher.Normalize(channel);
        var tagged = workflow.DeterministicActions
            .Concat(workflow.SelectorsOrLocatorHints)
            .Where(value => value.StartsWith("youtube_watch_url:", StringComparison.OrdinalIgnoreCase))
            .Select(value =>
            {
                var remainder = value["youtube_watch_url:".Length..];
                var separator = remainder.IndexOf('|');
                if (separator <= 0) return new { Channel = "", Url = "" };
                return new
                {
                    Channel = FuzzyMatcher.Normalize(remainder[..separator]),
                    Url = remainder[(separator + 1)..].Trim(),
                };
            })
            .FirstOrDefault(item =>
                item.Channel.Equals(normalizedChannel, StringComparison.Ordinal)
                && item.Url.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(tagged?.Url))
            return tagged.Url;

        return "";
    }

    private static bool WorkflowContainsYouTubeChannel(BrowserWorkflowMemory workflow, string channel)
    {
        if (workflow == null || string.IsNullOrWhiteSpace(channel))
            return false;
        var normalizedChannel = FuzzyMatcher.Normalize(channel);
        var channelToken = $"youtube_channel:{channel}";
        return workflow.DeterministicActions
            .Concat(workflow.SelectorsOrLocatorHints)
            .Any(value => value.Contains(channelToken, StringComparison.OrdinalIgnoreCase))
            || workflow.EntityKey.Equals(normalizedChannel.Replace(" ", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    private static string FindSavedEcommerceProductUrl(
        BrowserWorkflowMemory workflow,
        string product)
    {
        if (workflow == null)
            return "";
        var tokens = ProductTokens(product).ToArray();
        var candidates = workflow.DeterministicActions
            .Concat(workflow.SelectorsOrLocatorHints)
            .Where(value => value.StartsWith("ecommerce_product_url:", StringComparison.OrdinalIgnoreCase))
            .Select(value =>
            {
                var remainder = value["ecommerce_product_url:".Length..];
                var separator = remainder.LastIndexOf('|');
                var label = separator >= 0 ? remainder[..separator] : remainder;
                var url = separator >= 0 ? remainder[(separator + 1)..] : "";
                var text = $"{label} {url}";
                var tokenHits = tokens.Count(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
                var coverage = tokens.Length == 0 ? 0 : tokenHits / (double)tokens.Length;
                return new
                {
                    Url = BrowserAgentSafetyPolicy.IsSafeHttpUrl(url, out _) ? url : "",
                    Score = Math.Max(coverage, FuzzyMatcher.Score(product, text)),
                };
            })
            .Where(item => item.Url.Length > 0 && item.Score >= 0.55)
            .OrderByDescending(item => item.Score)
            .ToArray();
        return candidates.FirstOrDefault()?.Url ?? "";
    }

    private async Task<BrowserAgentResult> RunEcommerceCartTaskAsync(
        string taskId,
        BrowserTaskRequest request,
        BrowserCompanionTelemetry telemetry,
        BrowserWorkflowMatch workflowMatch,
        Stopwatch stopwatch,
        CancellationToken token)
    {
        var products = ExtractEcommerceProducts(request.OriginalUserCommand);
        if (products.Length == 0)
            return FailCompanionTask(taskId, "I could not identify the products to add to cart.");

        telemetry.ReplayUsed = false;
        var baseUrl = EcommerceBaseUrl(request.StartUrl);
        _tasks.AddReceipt(
            taskId,
            $"Browser memory lookup: products={string.Join(", ", products)}; exact cart replay disabled for safety; template matched={(WorkflowMatchesType(workflowMatch, "ecommerce_add_to_cart") ? "yes" : "no")}; using fresh Page Control Graph.");

        var added = new List<string>();
        var locatorHints = new List<string>();
        var productUrlHints = new List<string>();
        var learnedSteps = new List<string>
        {
            "Use direct ecommerce search URL",
            "Open the best product result from visible product cards",
            "Click Add to Cart after one upfront cart confirmation",
            "Open cart page and stop before checkout/payment",
        };

        _tasks.SetStepByIndex(taskId, 1, JarvisTaskStepStatus.Completed, "Using Browser Companion Extension in the active browser.");

        foreach (var product in products)
        {
            EnsureCompanionTaskRunnable(taskId, token);
            _tasks.SetStepByIndex(taskId, 2, JarvisTaskStepStatus.Running, $"Searching ecommerce site for {product}.");
            var searchUrl = EcommerceSearchUrl(baseUrl, product);
            var search = await ExecuteCompanionCommandAsync(
                taskId,
                "navigate",
                telemetry,
                token,
                searchUrl,
                timeout: TimeSpan.FromSeconds(18));
            if (!search.Ok)
                return FailCompanionTask(taskId, search.Message);
            var graph = search.Graph ?? _companionBridge?.LastGraph;
            if (graph == null)
                return FailCompanionTask(taskId, $"Ecommerce search for {product} did not return a Page Control Graph.");
            graph = await TryDismissEcommerceInterruptionAsync(taskId, graph, telemetry, token);
            if (RequiresManualBrowserHandoff(graph))
                return FailCompanionTask(taskId, "Ecommerce site needs manual login/CAPTCHA/protection handling before Jarvis can continue.");

            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Running, $"Choosing the most likely product result for {product}.");
            var productLink = FindEcommerceProductLink(graph, product, baseUrl);
            var candidateOptions = TopEcommerceCandidateLabels(graph, product, baseUrl).ToList();
            for (var scrollAttempt = 0; productLink == null && scrollAttempt < 3; scrollAttempt++)
            {
                graph = await ScrollAndGraph(taskId, telemetry, token);
                graph = await TryDismissEcommerceInterruptionAsync(taskId, graph, telemetry, token);
                candidateOptions.AddRange(TopEcommerceCandidateLabels(graph, product, baseUrl));
                productLink = FindEcommerceProductLink(graph, product, baseUrl);
            }
            if (productLink == null)
            {
                var options = candidateOptions
                    .Where(option => !string.IsNullOrWhiteSpace(option))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToArray();
                return ClarifyCompanionTask(
                    taskId,
                    $"I could not confidently find '{product}' in the visible ecommerce results. Choose a match, change the search phrase, or cancel.",
                    options.Length > 0 ? options : ["Search with another phrase", "Cancel the task"]);
            }
            locatorHints.Add(ControlSignature(productLink));
            if (BrowserAgentSafetyPolicy.IsSafeHttpUrl(productLink.Href, out _))
                productUrlHints.Add($"ecommerce_product_url:{CleanUserText(product, 80)}|{productLink.Href}");

            graph = await NavigateOrClickControl(taskId, productLink, telemetry, token, waitMs: 900);
            graph = await TryDismissEcommerceInterruptionAsync(taskId, graph, telemetry, token);
            if (RequiresManualBrowserHandoff(graph))
                return FailCompanionTask(taskId, "Ecommerce site needs manual login/CAPTCHA/protection handling before Jarvis can continue.");
            graph = await WaitForGraphConditionAsync(
                taskId,
                telemetry,
                token,
                graph,
                current => FindAddToCartControl(current) != null
                    || LooksUnavailableForPurchase(current)
                    || RequiresManualBrowserHandoff(current),
                TimeSpan.FromSeconds(8),
                TimeSpan.FromMilliseconds(650));
            graph = await TryDismissEcommerceInterruptionAsync(taskId, graph, telemetry, token);
            if (RequiresManualBrowserHandoff(graph))
                return FailCompanionTask(taskId, "Ecommerce site needs manual login/CAPTCHA/protection handling before Jarvis can continue.");

            var addToCart = FindAddToCartControl(graph);
            for (var scrollAttempt = 0; addToCart == null && scrollAttempt < 3; scrollAttempt++)
            {
                graph = await ScrollAndGraph(taskId, telemetry, token);
                graph = await TryDismissEcommerceInterruptionAsync(taskId, graph, telemetry, token);
                if (RequiresManualBrowserHandoff(graph))
                    return FailCompanionTask(taskId, "Ecommerce site needs manual login/CAPTCHA/protection handling before Jarvis can continue.");
                addToCart = FindAddToCartControl(graph);
            }
            if (addToCart == null && LooksUnavailableForPurchase(graph))
                return FailCompanionTask(taskId, $"{product} appears unavailable, coming soon, or not currently purchasable on this page. No safe Add to Cart action is available.");
            if (addToCart == null)
                return FailCompanionTask(taskId, $"I opened {product}, but could not find an Add to Cart control.");
            locatorHints.Add(ControlSignature(addToCart));
            _tasks.AddReceipt(taskId, $"Ecommerce action control selected: {ControlSignature(addToCart)}; selectors={CleanUserText(string.Join(" | ", addToCart.SelectorCandidates), 220)}.");

            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Running, $"Adding {product} to cart.");
            graph = await ClickAndGraph(taskId, addToCart, telemetry, token, waitMs: 1800);
            graph = await TryDismissEcommerceInterruptionAsync(taskId, graph, telemetry, token);
            if (RequiresManualBrowserHandoff(graph))
                return FailCompanionTask(taskId, "Ecommerce site needs manual login/CAPTCHA/protection handling before Jarvis can continue.");
            graph = await WaitForGraphConditionAsync(
                taskId,
                telemetry,
                token,
                graph,
                LooksAddedToCart,
                TimeSpan.FromSeconds(8),
                TimeSpan.FromMilliseconds(700));
            if (!LooksAddedToCart(graph))
            {
                var refreshed = await ExecuteCompanionCommandAsync(
                    taskId,
                    "getGraph",
                    telemetry,
                    token,
                    timeout: TimeSpan.FromSeconds(8));
                graph = refreshed.Graph ?? graph;
                if (!LooksAddedToCart(graph))
                    return FailCompanionTask(taskId, $"Ecommerce site did not clearly confirm that {product} was added to cart.");
            }
            added.Add(product);
        }

        EnsureCompanionTaskRunnable(taskId, token);
        _tasks.SetStepByIndex(taskId, 4, JarvisTaskStepStatus.Running, "Opening cart page and stopping before checkout.");
        var cartUrl = EcommerceCartUrl(baseUrl);
        var cart = await ExecuteCompanionCommandAsync(
            taskId,
            "navigate",
            telemetry,
            token,
            cartUrl,
            timeout: TimeSpan.FromSeconds(15));
        var cartGraph = cart.Graph ?? _companionBridge?.LastGraph;
        if (!cart.Ok || cartGraph == null)
            return FailCompanionTask(taskId, cart.Message.Length > 0 ? cart.Message : "Could not open ecommerce cart page.");
        cartGraph = await TryDismissEcommerceInterruptionAsync(taskId, cartGraph, telemetry, token);
        if (RequiresManualBrowserHandoff(cartGraph))
            return FailCompanionTask(taskId, "Ecommerce site needs manual login/CAPTCHA/protection handling before cart verification.");

        var cartText = GraphText(cartGraph);
        var verifiedCount = added.Count(product => ProductTokens(product).Any(tokenText => cartText.Contains(tokenText, StringComparison.OrdinalIgnoreCase)));
        var summary = verifiedCount > 0
            ? $"Added {added.Count} item(s) to cart and stopped at the cart page."
            : $"Reached the cart page after adding {added.Count} item(s), but visible cart text did not expose item names for verification.";

        telemetry.RuntimeMs = (int)stopwatch.ElapsedMilliseconds;
        var deterministicActions = new List<string>
        {
            $"navigate:{EcommerceSearchUrl(baseUrl, "{product_query}")}",
            "ecommerce_choose_visible_product_card",
            "ecommerce_add_to_cart_after_upfront_confirmation",
            $"navigate:{cartUrl}",
        };
        deterministicActions.AddRange(productUrlHints);

        var workflowSaved = RememberSuccessfulEcommerceWorkflow(
            taskId,
            request,
            cartGraph,
            telemetry,
            products,
            learnedSteps,
            deterministicActions,
            productUrlHints.Concat(locatorHints));
        telemetry.WorkflowSaved = workflowSaved;

        _tasks.SetStepByIndex(taskId, 2, JarvisTaskStepStatus.Completed, $"Processed {added.Count} product search(es).");
        _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Completed, "Add-to-cart actions completed after one upfront confirmation.");
        _tasks.SetStepByIndex(taskId, 4, JarvisTaskStepStatus.Completed, "Cart page is open. Checkout/payment was not started.");
        _tasks.Complete(taskId, summary);
        return new BrowserAgentResult
        {
            Ok = true,
            TaskId = taskId,
            Message = summary,
            Task = _tasks.Get(taskId),
        };
    }

    private async Task<bool> TryReplayEcommerceCartWorkflowAsync(
        string taskId,
        BrowserTaskRequest request,
        BrowserWorkflowMatch workflowMatch,
        BrowserCompanionTelemetry telemetry,
        string baseUrl,
        IReadOnlyList<string> products,
        CancellationToken token)
    {
        if (workflowMatch?.Workflow == null || products == null || products.Count == 0)
            return false;

        var productUrls = products
            .Select(product => new
            {
                Product = product,
                Url = FindSavedEcommerceProductUrl(workflowMatch.Workflow, product),
            })
            .ToArray();
        if (productUrls.Any(item => string.IsNullOrWhiteSpace(item.Url)))
            return false;

        try
        {
            _tasks.SetStepByIndex(taskId, 1, JarvisTaskStepStatus.Completed, "Using Browser Companion Extension in the active browser.");
            _tasks.SetStepByIndex(taskId, 2, JarvisTaskStepStatus.Running, "Replaying saved ecommerce product pages.");
            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Running, "Adding products after the approved cart confirmation.");

            foreach (var item in productUrls)
            {
                EnsureCompanionTaskRunnable(taskId, token);
                var navigate = await ExecuteCompanionCommandAsync(
                    taskId,
                    "navigateFast",
                    telemetry,
                    token,
                    item.Url,
                    timeout: TimeSpan.FromSeconds(10));
                if (!navigate.Ok)
                    return ReplayEcommerceFailed(taskId, workflowMatch, telemetry, $"Saved product URL failed for {item.Product}: {navigate.Message}");

                await Task.Delay(550, token);
                var fresh = await ExecuteCompanionCommandAsync(
                    taskId,
                    "getGraph",
                    telemetry,
                    token,
                    timeout: TimeSpan.FromSeconds(8));
                var graph = fresh.Graph ?? navigate.Graph ?? _companionBridge?.LastGraph;
                if (graph == null)
                    return ReplayEcommerceFailed(taskId, workflowMatch, telemetry, $"Saved product page for {item.Product} did not return a graph.");
                if (RequiresManualBrowserHandoff(graph))
                    return ReplayEcommerceFailed(taskId, workflowMatch, telemetry, "Ecommerce site needs manual login/CAPTCHA/protection handling.");

                var addToCart = FindAddToCartControl(graph);
                for (var scrollAttempt = 0; addToCart == null && scrollAttempt < 3; scrollAttempt++)
                {
                    graph = await ScrollAndGraph(taskId, telemetry, token);
                    if (RequiresManualBrowserHandoff(graph))
                        return ReplayEcommerceFailed(taskId, workflowMatch, telemetry, "Ecommerce site needs manual login/CAPTCHA/protection handling.");
                    addToCart = FindAddToCartControl(graph);
                }
                if (addToCart == null)
                    return ReplayEcommerceFailed(taskId, workflowMatch, telemetry, $"Saved product page for {item.Product} no longer exposes Add to Cart.");
                _tasks.AddReceipt(taskId, $"Replay ecommerce action control selected: {ControlSignature(addToCart)}; selectors={CleanUserText(string.Join(" | ", addToCart.SelectorCandidates), 220)}.");

                graph = await ClickAndGraph(taskId, addToCart, telemetry, token, waitMs: 900);
                if (RequiresManualBrowserHandoff(graph))
                    return ReplayEcommerceFailed(taskId, workflowMatch, telemetry, "Ecommerce site needs manual login/CAPTCHA/protection handling after Add to Cart.");
                if (!LooksAddedToCart(graph))
                    return ReplayEcommerceFailed(taskId, workflowMatch, telemetry, $"Could not verify that {item.Product} was added to cart during replay.");
            }

            var cartUrl = $"{baseUrl}/gp/cart/view.html";
            var cart = await ExecuteCompanionCommandAsync(
                taskId,
                "navigateFast",
                telemetry,
                token,
                cartUrl,
                timeout: TimeSpan.FromSeconds(10));
            await Task.Delay(400, token);
            var cartGraphResult = await ExecuteCompanionCommandAsync(
                taskId,
                "getGraph",
                telemetry,
                token,
                timeout: TimeSpan.FromSeconds(8));
            var cartGraph = cartGraphResult.Graph ?? cart.Graph ?? _companionBridge?.LastGraph;
            if (!cart.Ok || cartGraph == null)
                return ReplayEcommerceFailed(taskId, workflowMatch, telemetry, cart.Message.Length > 0 ? cart.Message : "Saved ecommerce replay could not open the cart page.");
            if (RequiresManualBrowserHandoff(cartGraph))
                return ReplayEcommerceFailed(taskId, workflowMatch, telemetry, "Ecommerce site needs manual login/CAPTCHA/protection handling before cart verification.");
            if (!cartGraph.Url.Contains("/cart", StringComparison.OrdinalIgnoreCase)
                && !GraphText(cartGraph).Contains("shopping cart", StringComparison.OrdinalIgnoreCase))
            {
                return ReplayEcommerceFailed(taskId, workflowMatch, telemetry, "Saved ecommerce replay did not reach the cart page.");
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ReplayEcommerceFailed(taskId, workflowMatch, telemetry, ex.Message);
        }
    }

    private bool ReplayEcommerceFailed(
        string taskId,
        BrowserWorkflowMatch workflowMatch,
        BrowserCompanionTelemetry telemetry,
        string reason)
    {
        telemetry.RepairUsed = true;
        _tasks.AddReceipt(taskId, $"Saved ecommerce replay failed; repairing with fresh Page Control Graph. {CleanUserText(reason, 220)}");
        if (workflowMatch?.Workflow != null)
            _workflowMemory?.RecordFailure(workflowMatch.Workflow.WorkflowId);
        return false;
    }

    private async Task<BrowserCompanionControlGraph> NavigateOrClickControl(
        string taskId,
        BrowserCompanionControl control,
        BrowserCompanionTelemetry telemetry,
        CancellationToken token,
        int waitMs)
    {
        if (BrowserAgentSafetyPolicy.IsSafeHttpUrl(control.Href, out _))
        {
            var navigated = await ExecuteCompanionCommandAsync(
                taskId,
                "navigate",
                telemetry,
                token,
                control.Href,
                timeout: TimeSpan.FromSeconds(18));
            if (!navigated.Ok)
                throw new InvalidOperationException(navigated.Message);

            if (waitMs > 0)
            {
                await Task.Delay(waitMs, token);
                EnsureCompanionTaskRunnable(taskId, token);
                navigated = await ExecuteCompanionCommandAsync(
                    taskId,
                    "getGraph",
                    telemetry,
                    token,
                    timeout: TimeSpan.FromSeconds(12));
            }

            return navigated.Graph ?? _companionBridge?.LastGraph
                ?? throw new InvalidOperationException("Browser Companion did not return a product page graph.");
        }
        return await ClickAndGraph(taskId, control, telemetry, token, waitMs);
    }

    private async Task<BrowserCompanionControlGraph> ScrollAndGraph(
        string taskId,
        BrowserCompanionTelemetry telemetry,
        CancellationToken token)
    {
        var result = await ExecuteCompanionCommandAsync(
            taskId,
            "scroll",
            telemetry,
            token,
            text: "down",
            timeout: TimeSpan.FromSeconds(10));
        if (!result.Ok)
            throw new InvalidOperationException(result.Message);
        return result.Graph ?? _companionBridge.LastGraph
            ?? throw new InvalidOperationException("Browser Companion did not return a graph after scrolling.");
    }

    private async Task<BrowserCompanionControlGraph> WaitForGraphConditionAsync(
        string taskId,
        BrowserCompanionTelemetry telemetry,
        CancellationToken token,
        BrowserCompanionControlGraph current,
        Func<BrowserCompanionControlGraph, bool> condition,
        TimeSpan timeout,
        TimeSpan interval)
    {
        if (condition(current))
            return current;

        var stopwatch = Stopwatch.StartNew();
        var graph = current;
        while (stopwatch.Elapsed < timeout)
        {
            await Task.Delay(interval, token);
            EnsureCompanionTaskRunnable(taskId, token);
            var refreshed = await ExecuteCompanionCommandAsync(
                taskId,
                "getGraph",
                telemetry,
                token,
                timeout: TimeSpan.FromSeconds(8));
            graph = refreshed.Graph ?? graph;
            if (condition(graph))
                return graph;
        }
        return graph;
    }

    private async Task<BrowserCompanionControlGraph> ClickAndGraph(
        string taskId,
        BrowserCompanionControl control,
        BrowserCompanionTelemetry telemetry,
        CancellationToken token,
        int waitMs = 700)
    {
        EnsureCompanionTaskRunnable(taskId, token);
        var result = await ExecuteCompanionCommandAsync(
            taskId,
            "click",
            telemetry,
            token,
            controlId: control.ControlId,
            timeout: TimeSpan.FromSeconds(15));
        if (!result.Ok)
        {
            var fallbackGraph = await TryKeyboardNavigationFallbackAsync(
                taskId,
                control,
                telemetry,
                token,
                result.Message,
                waitMs);
            if (fallbackGraph != null)
                return fallbackGraph;
            throw new InvalidOperationException(result.Message);
        }
        if (waitMs > 0)
        {
            await Task.Delay(waitMs, token);
            EnsureCompanionTaskRunnable(taskId, token);
            result = await ExecuteCompanionCommandAsync(
                taskId,
                "getGraph",
                telemetry,
                token,
                timeout: TimeSpan.FromSeconds(12));
        }
        return result.Graph ?? _companionBridge.LastGraph
            ?? throw new InvalidOperationException("Browser Companion did not return a refreshed Page Control Graph.");
    }

    private async Task<BrowserCompanionControlGraph> TryKeyboardNavigationFallbackAsync(
        string taskId,
        BrowserCompanionControl control,
        BrowserCompanionTelemetry telemetry,
        CancellationToken token,
        string clickFailure,
        int waitMs)
    {
        if (_keyboardNavigation == null || control == null)
            return null;
        var target = CleanUserText(PrimaryControlText(control), 120);
        if (string.IsNullOrWhiteSpace(target))
            target = CleanUserText(ControlText(control), 120);
        if (string.IsNullOrWhiteSpace(target))
            return null;

        _tasks.AddReceipt(taskId, $"Direct page control failed; trying bounded keyboard navigation fallback. reason={CleanUserText(clickFailure, 160)} target={target}");
        var focus = await ExecuteCompanionCommandAsync(
            taskId,
            "focusActiveTab",
            telemetry,
            token,
            timeout: TimeSpan.FromSeconds(5));
        if (!focus.Ok)
        {
            _tasks.AddReceipt(taskId, $"Keyboard fallback skipped: browser tab could not be focused. {CleanUserText(focus.Message, 160)}");
            return null;
        }

        var cartAction = control.ControlType.Equals("cart-action", StringComparison.OrdinalIgnoreCase)
            || target.Contains("add to cart", StringComparison.OrdinalIgnoreCase)
            || target.Contains("add to basket", StringComparison.OrdinalIgnoreCase);
        var navigation = await _keyboardNavigation.ActivateTargetAsync(
            taskId,
            new KeyboardNavigationPlan
            {
                TargetDescription = target,
                MaxTabPresses = 40,
                MaxRuntime = TimeSpan.FromSeconds(12),
                SafetyContext = new InputSafetyContext
                {
                    Kind = cartAction ? InputSafetyContextKind.CartMutationApproved : InputSafetyContextKind.ReadOnlyNavigation,
                    UpfrontCartMutationApproved = cartAction,
                    UserExplicitlyConfirmed = cartAction,
                    TaskDescription = _tasks.Get(taskId)?.OriginalUserCommand ?? "",
                },
            },
            token);
        telemetry.Actions += Math.Max(1, navigation.TabPresses);
        if (!navigation.Ok)
        {
            _tasks.AddReceipt(taskId, $"Keyboard navigation fallback failed: {CleanUserText(navigation.Message, 180)}");
            return null;
        }

        _tasks.AddReceipt(taskId, $"Keyboard navigation fallback activated target after {navigation.TabPresses} Tab step(s).");
        await Task.Delay(Math.Max(waitMs, 650), token);
        var refreshed = await ExecuteCompanionCommandAsync(
            taskId,
            "getGraph",
            telemetry,
            token,
            timeout: TimeSpan.FromSeconds(10));
        return refreshed.Graph ?? _companionBridge?.LastGraph;
    }

    private async Task<BrowserCompanionCommandResult> ExecuteCompanionCommandAsync(
        string taskId,
        string type,
        BrowserCompanionTelemetry telemetry,
        CancellationToken token,
        string url = "",
        string controlId = "",
        string text = "",
        TimeSpan? timeout = null)
    {
        EnsureCompanionTaskRunnable(taskId, token);
        telemetry.Actions++;
        var command = new BrowserCompanionCommand
        {
            TaskId = taskId,
            Type = type,
            Url = url,
            ControlId = controlId,
            Text = text,
            TimeoutMs = (int)(timeout ?? TimeSpan.FromSeconds(15)).TotalMilliseconds,
        };
        var result = await _companionBridge.ExecuteAsync(
            command,
            timeout ?? TimeSpan.FromSeconds(15),
            token);
        if (result.Graph != null)
            telemetry.GraphRefreshes++;
        return result;
    }

    private void EnsureCompanionTaskRunnable(string taskId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_tasks.IsRunnable(taskId))
            throw new OperationCanceledException($"Browser task {taskId} is no longer running.");
    }

    private BrowserAgentResult FailCompanionTask(string taskId, string message)
    {
        _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Failed, message);
        _tasks.Fail(taskId, message);
        return Failure(message, taskId);
    }

    private BrowserAgentResult ClarifyCompanionTask(
        string taskId,
        string message,
        IEnumerable<string> options)
    {
        var task = _tasks.Get(taskId);
        var stepId = task?.Steps.Count > 3 ? task.Steps[3].Id : "";
        var waiting = _tasks.WaitForClarification(
            taskId,
            stepId,
            message,
            options);
        return new BrowserAgentResult
        {
            Ok = false,
            TaskId = taskId,
            Message = message,
            ClarificationRequired = true,
            Task = waiting,
        };
    }

    private bool RememberSuccessfulYouTubeWorkflow(
        string taskId,
        BrowserTaskRequest request,
        BrowserCompanionControlGraph graph,
        BrowserWorkflowMatch workflowMatch,
        BrowserCompanionTelemetry telemetry,
        string channel,
        IEnumerable<string> learnedSteps,
        IEnumerable<string> deterministicActions,
        IEnumerable<string> locatorHints)
    {
        if (_workflowMemory == null || !_settings.Current.LearnBrowserWorkflows)
            return false;
        if (graph == null || !graph.Url.Contains("youtube.com/watch", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            var domain = BrowserAgentSafetyPolicy.IsSafeHttpUrl(request.StartUrl, out var uri)
                ? uri.Host
                : "www.youtube.com";
            var templateSteps = new[]
            {
                "Identify requested YouTube channel/entity",
                "Open that channel's Videos page",
                "Use fresh Page Control Graph to find Videos tab/sort controls",
                "Select Oldest",
                "Use refreshed Page Control Graph to open first video card",
                "Verify youtube.com/watch or player controls",
            };
            var templateActions = new[]
            {
                "youtube_pattern:channel_videos_sort_oldest_open_first_watch",
                "youtube_find_channel_for_current_entity",
                "youtube_sort_videos_oldest",
                "youtube_open_first_video_card",
            };
            _workflowMemory.SaveSuccessfulWorkflow(
                domain,
                "youtube_play_oldest_video",
                "play oldest channel video",
                "play oldest channel video on YouTube",
                "Browser Companion Extension",
                templateSteps,
                templateActions,
                [],
                ["read-only media navigation", "never submit/pay/delete/post"],
                confirmationRequired: false,
                telemetry.RuntimeMs);

            _workflowMemory.SaveExactSuccessfulInstance(
                domain,
                "youtube_play_oldest_video",
                "play oldest channel video",
                request.OriginalUserCommand,
                channel,
                graph.Url,
                "Browser Companion Extension",
                learnedSteps,
                deterministicActions,
                locatorHints,
                ["read-only media navigation", "never submit/pay/delete/post"],
                confirmationRequired: false,
                telemetry.RuntimeMs);
            _tasks.AddReceipt(taskId, $"Learned browser workflow: yes. exact_entity={channel}; template=yes.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Failed to save YouTube Browser Companion workflow.");
            _tasks.AddReceipt(taskId, "Learned browser workflow: no.");
            return false;
        }
    }

    private bool RememberSuccessfulEcommerceWorkflow(
        string taskId,
        BrowserTaskRequest request,
        BrowserCompanionControlGraph graph,
        BrowserCompanionTelemetry telemetry,
        IReadOnlyList<string> products,
        IEnumerable<string> learnedSteps,
        IEnumerable<string> deterministicActions,
        IEnumerable<string> locatorHints)
    {
        if (_workflowMemory == null || !_settings.Current.LearnBrowserWorkflows)
            return false;
        try
        {
            var domain = BrowserAgentSafetyPolicy.IsSafeHttpUrl(request.StartUrl, out var uri)
                ? uri.Host
                : BrowserAgentSafetyPolicy.IsSafeHttpUrl(graph?.Url, out uri)
                    ? uri.Host
                    : "";
            if (string.IsNullOrWhiteSpace(domain))
                return false;

            var templateActions = (deterministicActions ?? Array.Empty<string>())
                .Where(action => !action.StartsWith("ecommerce_product_url:", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            _workflowMemory.SaveSuccessfulWorkflow(
                domain,
                "ecommerce_add_to_cart",
                "ecommerce search product, add to cart, and stop at cart",
                "ecommerce search and add selected products to cart after upfront confirmation",
                "Browser Companion Extension",
                [
                    "Navigate to requested ecommerce site",
                    "Use fresh Page Control Graph to find search box/results/product cards",
                    "Open best matching product result",
                    "Use fresh product-page graph to find Add to Cart",
                    "Stop at cart before checkout/payment",
                ],
                templateActions,
                [],
                ["one upfront add-to-cart confirmation", "never checkout/pay/place order", "manual handoff for login/CAPTCHA/payment/address"],
                confirmationRequired: true,
                telemetry.RuntimeMs);

            var entityKey = string.Join("|", products.Select(product => CleanUserText(product, 80)));
            _workflowMemory.SaveExactSuccessfulInstance(
                domain,
                "ecommerce_add_to_cart",
                "ecommerce add products to cart and stop at cart",
                request.OriginalUserCommand,
                entityKey,
                graph?.Url ?? request.StartUrl,
                "Browser Companion Extension",
                learnedSteps,
                deterministicActions,
                locatorHints,
                ["one upfront add-to-cart confirmation", "never checkout/pay/place order"],
                confirmationRequired: true,
                telemetry.RuntimeMs);
            _tasks.AddReceipt(taskId, $"Learned browser workflow: yes. exact_products={entityKey}; template=yes.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Failed to save ecommerce Browser Companion workflow.");
            _tasks.AddReceipt(taskId, "Learned browser workflow: no.");
            return false;
        }
    }

    private bool RememberSuccessfulCompanionWorkflow(
        string taskId,
        BrowserTaskRequest request,
        BrowserCompanionControlGraph graph,
        BrowserWorkflowMatch workflowMatch,
        BrowserCompanionTelemetry telemetry,
        IEnumerable<string> learnedSteps,
        IEnumerable<string> deterministicActions,
        IEnumerable<string> locatorHints)
    {
        if (_workflowMemory == null || !_settings.Current.LearnBrowserWorkflows)
            return false;
        try
        {
            var ecommerce = CanRunEcommerceCartTask(request.OriginalUserCommand);
            var youtube = CanRunYouTubeOldestVideo(request.OriginalUserCommand);
            var taskType = ecommerce
                ? "ecommerce_add_to_cart"
                : youtube
                    ? "youtube_play_oldest_video"
                    : "browser_task";
            var taskPattern = ecommerce
                ? "ecommerce add products to cart and stop at cart"
                : youtube
                    ? "play oldest channel video"
                    : "browser task";
            var safetyBoundaries = ecommerce
                ? new[]
                {
                    "one upfront add-to-cart confirmation",
                    "never checkout/pay/place order",
                    "manual handoff for login/CAPTCHA/payment/address",
                }
                : new[]
                {
                    "read-only media navigation",
                    "never submit/pay/delete/post",
                };
            if (WorkflowMatchesType(workflowMatch, taskType) && telemetry.ReplayUsed)
            {
                _workflowMemory.RecordRepairSuccess(
                    workflowMatch.Workflow.WorkflowId,
                    learnedSteps,
                    telemetry.RuntimeMs,
                    deterministicActions,
                    locatorHints);
            }
            else
            {
                _workflowMemory.SaveSuccessfulWorkflow(
                    graph?.Url ?? request.StartUrl,
                    taskType,
                    taskPattern,
                    request.OriginalUserCommand,
                    "Browser Companion Extension",
                    learnedSteps,
                    deterministicActions,
                    locatorHints,
                    safetyBoundaries,
                    confirmationRequired: ecommerce,
                    telemetry.RuntimeMs);
            }
            _tasks.AddReceipt(taskId, "Learned browser workflow: yes.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Failed to save Browser Companion workflow.");
            _tasks.AddReceipt(taskId, "Learned browser workflow: no.");
            return false;
        }
    }

    private static bool WorkflowMatchesType(BrowserWorkflowMatch workflowMatch, string taskType) =>
        workflowMatch?.HighConfidence == true
        && string.Equals(workflowMatch.Workflow?.TaskType, taskType, StringComparison.OrdinalIgnoreCase);

    private void AddCompanionTelemetry(string taskId, BrowserCompanionTelemetry telemetry)
    {
        _tasks.AddReceipt(
            taskId,
            $"Browser Companion telemetry: runtime {telemetry.RuntimeMs} ms; actions {telemetry.Actions}; graph refreshes {telemetry.GraphRefreshes}; LLM calls {telemetry.LlmCalls}; replay used {(telemetry.ReplayUsed ? "yes" : "no")}; repair used {(telemetry.RepairUsed ? "yes" : "no")}; workflow saved {(telemetry.WorkflowSaved ? "yes" : "no")}.");
    }

    private static bool CanRunYouTubeOldestVideo(string task)
    {
        var normalized = FuzzyMatcher.Normalize(task);
        return normalized.Contains("youtube", StringComparison.Ordinal)
            && normalized.Contains("oldest", StringComparison.Ordinal)
            && (normalized.Contains("video", StringComparison.Ordinal)
                || normalized.Contains("play", StringComparison.Ordinal));
    }

    private static bool CanRunEcommerceCartTask(string task)
    {
        var normalized = FuzzyMatcher.Normalize(task);
        return (normalized.Contains("amazon", StringComparison.Ordinal)
                || normalized.Contains("flipkart", StringComparison.Ordinal)
                || normalized.Contains("ecommerce", StringComparison.Ordinal)
                || normalized.Contains("cart", StringComparison.Ordinal))
            && normalized.Contains("cart", StringComparison.Ordinal)
            && (normalized.Contains("add", StringComparison.Ordinal)
                || normalized.Contains("open", StringComparison.Ordinal)
                || normalized.Contains("find", StringComparison.Ordinal)
                || normalized.Contains("search", StringComparison.Ordinal));
    }

    private static string EcommerceBaseUrl(string startUrl)
    {
        if (BrowserAgentSafetyPolicy.IsSafeHttpUrl(startUrl, out var uri))
            return $"{uri.Scheme}://{uri.Authority}";
        return "https://www.amazon.in";
    }

    private static string EcommerceSearchUrl(string baseUrl, string product)
    {
        var encoded = string.Equals(product, "{product_query}", StringComparison.Ordinal)
            ? "{product_query}"
            : Uri.EscapeDataString(product);
        if (BrowserAgentSafetyPolicy.IsSafeHttpUrl(baseUrl, out var uri)
            && uri.Host.Contains("flipkart.", StringComparison.OrdinalIgnoreCase))
            return $"{baseUrl.TrimEnd('/')}/search?q={encoded}";
        return $"{baseUrl.TrimEnd('/')}/s?k={encoded}";
    }

    private static string EcommerceCartUrl(string baseUrl)
    {
        if (BrowserAgentSafetyPolicy.IsSafeHttpUrl(baseUrl, out var uri)
            && uri.Host.Contains("flipkart.", StringComparison.OrdinalIgnoreCase))
            return $"{baseUrl.TrimEnd('/')}/viewcart?marketplace=FLIPKART";
        if (BrowserAgentSafetyPolicy.IsSafeHttpUrl(baseUrl, out uri)
            && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)))
            return $"{baseUrl.TrimEnd('/')}/gp/cart/view.html";
        return $"{baseUrl.TrimEnd('/')}/gp/cart/view.html";
    }

    private static string[] ExtractEcommerceProducts(string task)
    {
        var text = (task ?? "").ReplaceLineEndings(" ").Trim();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\bstop\s+(at|on|before)\s+.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var siteIndex = System.Text.RegularExpressions.Regex.Match(text, @"\s+(?:on|from)\s+(?:Amazon|Flipkart)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (siteIndex.Success && siteIndex.Index > 0)
            text = text[..siteIndex.Index];
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\bopen\s+(amazon|flipkart)\s+and\s+", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b(add|put)\s+(it|them|these|this)?\s*(to|in)\s+(my\s+)?cart\b", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b(open|find|search for|search|add|get|buy|please)\b", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b(on|from)?\s*(amazon|flipkart)\b", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\b(the|a|an|item|items|product|products|cart)\b", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex
            .Split(text, @"\s*(?:,|\+|\band\b)\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(value => CleanUserText(value, 120))
            .Where(value => value.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();
    }

    private static BrowserCompanionControl FindEcommerceProductLink(
        BrowserCompanionControlGraph graph,
        string product,
        string baseUrl)
    {
        var tokens = ProductTokens(product).ToArray();
        var candidates = graph?.Controls?
            .Where(control =>
                !control.Sensitive
                && control.Enabled
                && control.Visible
                && IsLikelyEcommerceProductLink(control, baseUrl))
            .Select(control => new
            {
                Control = control,
                Score = EcommerceProductScore(control, product, tokens),
            })
            .Where(item => item.Score >= 0.28)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Control.Bounds.Y)
            .ThenBy(item => item.Control.Bounds.X)
            .ToArray() ?? [];
        return candidates.FirstOrDefault()?.Control;
    }

    private static IEnumerable<string> TopEcommerceCandidateLabels(
        BrowserCompanionControlGraph graph,
        string product,
        string baseUrl)
    {
        var tokens = ProductTokens(product).ToArray();
        return graph?.Controls?
            .Where(control =>
                !control.Sensitive
                && control.Enabled
                && control.Visible
                && IsLikelyEcommerceProductLink(control, baseUrl))
            .Select(control => new
            {
                Label = CleanUserText(ControlText(control), 160),
                Score = EcommerceProductScore(control, product, tokens),
            })
            .Where(item => item.Label.Length > 0 && item.Score >= 0.12)
            .OrderByDescending(item => item.Score)
            .Take(6)
            .Select(item => item.Label) ?? [];
    }

    private static bool IsLikelyEcommerceProductLink(BrowserCompanionControl control, string baseUrl)
    {
        if (control == null || string.IsNullOrWhiteSpace(control.Href))
            return false;
        if (!BrowserAgentSafetyPolicy.IsSafeHttpUrl(control.Href, out var hrefUri))
            return false;
        if (!BrowserAgentSafetyPolicy.IsSafeHttpUrl(baseUrl, out var baseUri))
            return false;
        if (!hrefUri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
            return false;
        var href = control.Href;
        if (href.Contains("customer-reviews", StringComparison.OrdinalIgnoreCase)
            || href.Contains("offer-listing", StringComparison.OrdinalIgnoreCase)
            || href.Contains("/cart", StringComparison.OrdinalIgnoreCase)
            || href.Contains("/viewcart", StringComparison.OrdinalIgnoreCase)
            || href.Contains("/account", StringComparison.OrdinalIgnoreCase)
            || href.Contains("/login", StringComparison.OrdinalIgnoreCase)
            || href.Contains("/signin", StringComparison.OrdinalIgnoreCase)
            || href.Contains("checkout", StringComparison.OrdinalIgnoreCase)
            || href.Contains("payment", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = hrefUri.AbsolutePath;
        if (path.Contains("/dp/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/p/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/itm", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var context = $"{control.ControlType} {control.Role} {control.CardContext} {control.RepeatingGroup} {control.ParentSection}";
        return context.Contains("product", StringComparison.OrdinalIgnoreCase)
            || context.Contains("card", StringComparison.OrdinalIgnoreCase)
            || context.Contains("result", StringComparison.OrdinalIgnoreCase);
    }

    private static double EcommerceProductScore(
        BrowserCompanionControl control,
        string product,
        IReadOnlyList<string> tokens)
    {
        var text = ControlText(control);
        var tokenHits = tokens.Count(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
        var coverage = tokens.Count == 0 ? 0 : tokenHits / (double)tokens.Count;
        var fuzzy = FuzzyMatcher.Score(product, text);
        var digitTokens = tokens.Where(token => token.Any(char.IsDigit)).ToArray();
        var normalizedTextTokens = FuzzyMatcher.Normalize(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var missingDigitPenalty = digitTokens.Length > 0
            && digitTokens.Any(token => !DigitTokenHasQueryContext(normalizedTextTokens, tokens, token))
            ? 0.55
            : 0;
        var sponsoredPenalty = text.Contains("sponsored", StringComparison.OrdinalIgnoreCase) ? 0.08 : 0;
        var unavailablePenalty = LooksUnavailableText(text) ? 0.22 : 0;
        var priceBonus = text.Contains("₹", StringComparison.OrdinalIgnoreCase)
            || text.Contains("$", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rating", StringComparison.OrdinalIgnoreCase)
            ? 0.05
            : 0;
        return Math.Clamp((coverage * 0.62) + (fuzzy * 0.3) + priceBonus - sponsoredPenalty - unavailablePenalty - missingDigitPenalty, 0, 1);
    }

    private static BrowserCompanionControl FindAddToCartControl(BrowserCompanionControlGraph graph) =>
        graph?.Controls?
            .Where(control => control.Visible && control.Enabled && !control.Sensitive && IsClickableRole(control))
            .Select(control => new
            {
                Control = control,
                Score = AddToCartScore(control),
            })
            .Where(item => item.Score >= 0.72)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => Math.Max(0, item.Control.Bounds.Y))
            .FirstOrDefault()?.Control;

    private static double AddToCartScore(BrowserCompanionControl control)
    {
        if (control == null)
            return 0;
        var primary = PrimaryControlText(control);
        var selectorText = string.Join(" ", control.SelectorCandidates ?? Array.Empty<string>());
        var structural = $"{control.ControlType} {control.Role} {control.TagName} {control.InputType} {control.DomPathSignature} {selectorText}";
        if (primary.Contains("buy now", StringComparison.OrdinalIgnoreCase)
            || primary.Contains("checkout", StringComparison.OrdinalIgnoreCase)
            || primary.Contains("payment", StringComparison.OrdinalIgnoreCase)
            || primary.Contains("place order", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var score = 0.0;
        if (primary.Equals("add to cart", StringComparison.OrdinalIgnoreCase)
            || primary.Equals("add to basket", StringComparison.OrdinalIgnoreCase))
            score += 1.0;
        else if (primary.Contains("add to cart", StringComparison.OrdinalIgnoreCase)
            || primary.Contains("add to basket", StringComparison.OrdinalIgnoreCase))
            score += 0.88;
        if (selectorText.Contains("add-to-cart", StringComparison.OrdinalIgnoreCase)
            || selectorText.Contains("addtocart", StringComparison.OrdinalIgnoreCase)
            || structural.Contains("add-to-cart", StringComparison.OrdinalIgnoreCase)
            || structural.Contains("addtocart", StringComparison.OrdinalIgnoreCase))
            score += 0.82;
        if (control.ControlType.Equals("cart-action", StringComparison.OrdinalIgnoreCase))
            score += 0.26;
        if (control.Role.Contains("button", StringComparison.OrdinalIgnoreCase)
            || control.TagName.Equals("button", StringComparison.OrdinalIgnoreCase)
            || control.InputType.Equals("submit", StringComparison.OrdinalIgnoreCase)
            || control.InputType.Equals("button", StringComparison.OrdinalIgnoreCase))
            score += 0.18;
        if (string.IsNullOrWhiteSpace(primary)
            && score < 0.95)
            score *= 0.65;
        return Math.Clamp(score, 0, 1.4);
    }

    private static bool LooksAddedToCart(BrowserCompanionControlGraph graph)
    {
        var text = GraphText(graph);
        return graph?.PageSignals?.Any(signal => string.Equals(signal, "added_to_cart_visible", StringComparison.OrdinalIgnoreCase)) == true
            || text.Contains("added to cart", StringComparison.OrdinalIgnoreCase)
            || text.Contains("added to basket", StringComparison.OrdinalIgnoreCase)
            || text.Contains("go to cart", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cart subtotal", StringComparison.OrdinalIgnoreCase)
            || text.Contains("proceed to checkout", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksUnavailableForPurchase(BrowserCompanionControlGraph graph) =>
        LooksUnavailableText(GraphText(graph));

    private static bool LooksUnavailableText(string text) =>
        !string.IsNullOrWhiteSpace(text)
        && (text.Contains("coming soon", StringComparison.OrdinalIgnoreCase)
            || text.Contains("currently unavailable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("out of stock", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sold out", StringComparison.OrdinalIgnoreCase)
            || text.Contains("notify me", StringComparison.OrdinalIgnoreCase));

    private async Task<BrowserCompanionControlGraph> TryDismissEcommerceInterruptionAsync(
        string taskId,
        BrowserCompanionControlGraph graph,
        BrowserCompanionTelemetry telemetry,
        CancellationToken token)
    {
        if (graph == null)
            return graph;
        var text = GraphText(graph);
        var looksLikeLoginOverlay = text.Contains("login", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            || text.Contains("enter mobile", StringComparison.OrdinalIgnoreCase)
            || text.Contains("phone number", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeLoginOverlay)
            return graph;

        var close = FindControl(graph, control =>
        {
            var primary = PrimaryControlText(control);
            return IsClickableRole(control)
                && !primary.Contains("sign in", StringComparison.OrdinalIgnoreCase)
                && !primary.Contains("login", StringComparison.OrdinalIgnoreCase)
                && !primary.Contains("continue", StringComparison.OrdinalIgnoreCase)
                && (primary.Equals("close", StringComparison.OrdinalIgnoreCase)
                    || primary.Equals("×", StringComparison.OrdinalIgnoreCase)
                    || primary.Equals("x", StringComparison.OrdinalIgnoreCase)
                    || primary.Contains("dismiss", StringComparison.OrdinalIgnoreCase));
        });
        if (close == null)
            return graph;

        _tasks.AddReceipt(taskId, $"Dismissing visible ecommerce interruption: {ControlSignature(close)}.");
        try
        {
            return await ClickAndGraph(taskId, close, telemetry, token, waitMs: 900);
        }
        catch (Exception ex)
        {
            _tasks.AddReceipt(taskId, $"Dismiss interruption failed; continuing with current graph. {CleanUserText(ex.Message, 160)}");
            return graph;
        }
    }

    private static bool RequiresManualBrowserHandoff(BrowserCompanionControlGraph graph)
    {
        var text = GraphText(graph);
        return text.Contains("captcha", StringComparison.OrdinalIgnoreCase)
            || text.Contains("enter the characters", StringComparison.OrdinalIgnoreCase)
            || text.Contains("verify you are a human", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sign in", StringComparison.OrdinalIgnoreCase) && text.Contains("password", StringComparison.OrdinalIgnoreCase)
            || text.Contains("otp", StringComparison.OrdinalIgnoreCase)
            || text.Contains("one time password", StringComparison.OrdinalIgnoreCase);
    }

    private static string GraphText(BrowserCompanionControlGraph graph) =>
        string.Join(
            " ",
            graph?.Controls?.Select(ControlText).Where(value => !string.IsNullOrWhiteSpace(value)) ?? []);

    private static IEnumerable<string> ProductTokens(string product) =>
        FuzzyMatcher.Normalize(product)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3 || token.Any(char.IsDigit))
            .Distinct(StringComparer.Ordinal);

    private static bool DigitTokenHasQueryContext(
        IReadOnlyList<string> textTokens,
        IReadOnlyList<string> queryTokens,
        string digitToken)
    {
        if (textTokens == null || queryTokens == null || string.IsNullOrWhiteSpace(digitToken))
            return false;
        var nonDigitQueryTokens = queryTokens
            .Where(token => !token.Any(char.IsDigit) && token.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < textTokens.Count; index++)
        {
            if (!string.Equals(textTokens[index], digitToken, StringComparison.OrdinalIgnoreCase))
                continue;
            var start = Math.Max(0, index - 4);
            var end = Math.Min(textTokens.Count - 1, index + 4);
            var ratingStart = Math.Max(0, index - 1);
            var ratingEnd = Math.Min(textTokens.Count - 1, index + 3);
            var ratingContext = false;
            for (var nearby = ratingStart; nearby <= ratingEnd; nearby++)
            {
                var value = textTokens[nearby];
                if (value is "star" or "stars" or "rating" or "ratings" or "review" or "reviews")
                {
                    ratingContext = true;
                    break;
                }
            }
            if (ratingContext)
                continue;
            for (var nearby = start; nearby <= end; nearby++)
            {
                if (nearby == index)
                    continue;
                if (nonDigitQueryTokens.Contains(textTokens[nearby]))
                    return true;
            }
        }
        return false;
    }

    private static string ExtractYouTubeChannelHandle(string task)
    {
        var text = task ?? "";
        if (text.Contains("MrBeast", StringComparison.OrdinalIgnoreCase))
            return "MrBeast";
        var patterns = new[]
        {
            @"oldest\s+(?:visible\s+)?(?<channel>[A-Za-z0-9][A-Za-z0-9_.\-\s]{1,}?)(?:\s+video|\s+videos|\s+on\s+youtube|\s+youtube|\s+channel)",
            @"(?:play|open|find|show|search)?\s*(?:the\s+)?(?<channel>[A-Za-z0-9][A-Za-z0-9_.\-\s]{1,}?)(?:'s|’s|\s+channel)?\s+oldest\s+(?:video|videos)",
        };
        var raw = "";
        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                text,
                pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                raw = match.Groups["channel"].Value;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = System.Text.RegularExpressions.Regex.Replace(text, @"\bon\s+youtube\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\b(play|open|find|show|search|the|oldest|video|videos|channel|youtube)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\b(the|a|an|channel|youtube|video|videos|play|open|find|show|search)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return string.Concat(raw.Where(char.IsLetterOrDigit));
    }

    private static BrowserCompanionControl FindFirstVideoCard(BrowserCompanionControlGraph graph) =>
        graph?.Controls?
            .Where(control =>
                !control.Sensitive
                && control.Enabled
                && control.Visible
                && (control.Href.Contains("/watch", StringComparison.OrdinalIgnoreCase)
                    || control.Role.Contains("video", StringComparison.OrdinalIgnoreCase)
                    || TextContains(control, "views")))
            .OrderBy(control => control.Bounds.Y)
            .ThenBy(control => control.Bounds.X)
            .FirstOrDefault(control =>
                control.Href.Contains("/watch", StringComparison.OrdinalIgnoreCase)
                || TextContains(control, "views")) ?? null;

    private static BrowserCompanionControl FindControl(
        BrowserCompanionControlGraph graph,
        Func<BrowserCompanionControl, bool> predicate) =>
        graph?.Controls?
            .Where(control => control.Visible && control.Enabled && !control.Sensitive)
            .OrderByDescending(control => control.ConfidenceScore)
            .FirstOrDefault(predicate);

    private static bool IsClickableRole(BrowserCompanionControl control) =>
        control.Role.Contains("button", StringComparison.OrdinalIgnoreCase)
        || control.Role.Contains("link", StringComparison.OrdinalIgnoreCase)
        || control.Role.Contains("tab", StringComparison.OrdinalIgnoreCase)
        || control.Role.Contains("menu", StringComparison.OrdinalIgnoreCase)
        || control.Role.Contains("chip", StringComparison.OrdinalIgnoreCase)
        || control.ControlType.Equals("button", StringComparison.OrdinalIgnoreCase)
        || control.ControlType.Equals("cart-action", StringComparison.OrdinalIgnoreCase)
        || control.ControlType.Equals("filter-or-sort", StringComparison.OrdinalIgnoreCase)
        || control.ControlType.Equals("product-card", StringComparison.OrdinalIgnoreCase)
        || control.ControlType.Equals("video-card", StringComparison.OrdinalIgnoreCase)
        || control.TagName.Equals("button", StringComparison.OrdinalIgnoreCase)
        || control.TagName.Equals("a", StringComparison.OrdinalIgnoreCase)
        || (control.TagName.Equals("input", StringComparison.OrdinalIgnoreCase)
            && (control.InputType.Equals("submit", StringComparison.OrdinalIgnoreCase)
                || control.InputType.Equals("button", StringComparison.OrdinalIgnoreCase)
                || control.InputType.Equals("image", StringComparison.OrdinalIgnoreCase)));

    private static bool TextEquals(BrowserCompanionControl control, string expected) =>
        new[]
        {
            control?.Text,
            control?.AriaLabel,
            control?.Title,
            control?.Placeholder,
        }.Any(value => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
        || string.Equals(ControlText(control), expected, StringComparison.OrdinalIgnoreCase);

    private static bool TextContains(BrowserCompanionControl control, string expected) =>
        ControlText(control).Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static string ControlText(BrowserCompanionControl control) =>
        string.Join(
            " ",
            new[]
            {
                control?.Text,
                control?.AriaLabel,
                control?.Title,
                control?.Placeholder,
                control?.NearbyText,
                control?.ParentSection,
            }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    private static string PrimaryControlText(BrowserCompanionControl control) =>
        string.Join(
            " ",
            new[]
            {
                control?.Text,
                control?.AriaLabel,
                control?.Title,
                control?.Placeholder,
            }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    private static string ControlSignature(BrowserCompanionControl control)
    {
        if (control == null)
            return "";
        return $"{control.Role}:{control.TagName}:{CleanUserText(ControlText(control), 80)}:{CleanUserText(control.DomPathSignature, 80)}";
    }

    private static string GraphSummary(BrowserCompanionControlGraph graph) =>
        $"{CleanUserText(graph?.Title, 120)} · {graph?.Controls.Count ?? 0} graph controls · {CleanUserText(graph?.PageType, 80)}";

    private async Task<BrowserAgentResult> ExecuteSmartTaskAsync(
        string taskId,
        BrowserTaskRequest request,
        BrowserAgentRouteDecision route,
        CancellationToken token)
    {
        try
        {
            _tasks.SetStepByIndex(taskId, 1, JarvisTaskStepStatus.Running, "Launching browser-use + Gemini smart backend.");
            _tasks.SetStepByIndex(taskId, 2, JarvisTaskStepStatus.Running, "Reading visible page content and planning safe actions.");
            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Running, route.Safety.ChangesExternalState
                ? "Running the confirmed safe subset and stopping before protected final actions."
                : "Running safe read-only browser steps automatically.");

            var smartRequest = new BrowserSmartTaskRequest
            {
                TaskId = taskId,
                Title = request.Title,
                OriginalUserCommand = request.OriginalUserCommand,
                StartUrl = request.StartUrl,
                ContextSummary = _tasks.Get(taskId)?.ContextSummary ?? "",
                RiskyActionConfirmed = request.RiskyActionConfirmed || route.Safety.ChangesExternalState,
                Timeout = TimeSpan.FromSeconds(Math.Clamp(_settings.Current.MaxBrowserTaskSeconds, 30, 900)),
            };
            var result = await _smartBackend.RunAsync(smartRequest, token);
            ApplySmartProgress(taskId, result);

            if (result.Ok)
            {
                var summary = CleanSmartSummary(result);
                _tasks.SetStepByIndex(taskId, 1, JarvisTaskStepStatus.Completed, result.Backend);
                _tasks.SetStepByIndex(taskId, 2, JarvisTaskStepStatus.Completed, "Smart backend inspected the page and completed the safe task.");
                _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Completed, route.Safety.ChangesExternalState
                    ? "Confirmed safe actions completed; protected final actions were not performed."
                    : "Safe browser actions completed.");
                _tasks.SetStepByIndex(taskId, 4, JarvisTaskStepStatus.Completed, "Result summarized.");
                _tasks.AddReceipt(taskId, $"Smart backend: {CleanBackendName(result.Backend)}; {result.Steps} steps; {Math.Max(0, result.DurationMs)} ms.");
                RememberSuccessfulSmartWorkflow(taskId, request, result, route);
                _tasks.Complete(taskId, summary);
                return new BrowserAgentResult
                {
                    Ok = true,
                    TaskId = taskId,
                    Message = summary,
                    Task = _tasks.Get(taskId),
                };
            }

            if (result.Status is BrowserSmartTaskStatus.Blocked)
            {
                var waiting = _tasks.WaitForClarification(
                    taskId,
                    _tasks.Get(taskId).Steps[3].Id,
                    "The website blocked automation or needs manual login/CAPTCHA/OTP. Complete that manually in the browser, then tell Jarvis to continue or cancel.",
                    ["I completed the manual step", "Cancel the task"]);
                return new BrowserAgentResult
                {
                    Ok = false,
                    TaskId = taskId,
                    Message = "Manual browser action is required before Jarvis can continue.",
                    ClarificationRequired = true,
                    ManualActionRequired = true,
                    Task = waiting,
                };
            }

            if (result.Status is BrowserSmartTaskStatus.NeedsClarification)
            {
                var waiting = _tasks.WaitForClarification(
                    taskId,
                    _tasks.Get(taskId).Steps[3].Id,
                    CleanSmartSummary(result),
                    []);
                return new BrowserAgentResult
                {
                    Ok = false,
                    TaskId = taskId,
                    Message = CleanSmartSummary(result),
                    ClarificationRequired = true,
                    Task = waiting,
                };
            }

            if (result.CanFallbackToDirect
                && route.FallbackBackend == BrowserAgentBackendKind.DirectPlaywright
                && route.Safety.Risk == BrowserActionRisk.Safe)
            {
                _tasks.AddReceipt(taskId, "browser-use did not complete the task; using Direct Playwright fallback for safe browser inspection.");
                _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Running, "Smart backend unavailable; opening Direct Playwright fallback.");
                return await OpenInitialPageAsync(taskId, DisableSmartBackend(request), token);
            }

            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Failed, CleanSmartSummary(result));
            _tasks.Fail(taskId, CleanSmartSummary(result));
            return Failure(CleanSmartSummary(result), taskId);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(taskId);
        }
        catch (Exception ex)
        {
            _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Failed, ex.Message);
            return Failure(ex.Message, taskId);
        }
    }

    private async Task<BrowserAgentResult> ExecuteApprovedAsync(
        BrowserAgentActionRequest request,
        BrowserSafetyDecision safety,
        CancellationToken token)
    {
        try
        {
            var result = await _backend.ExecuteAsync(request, token);
            if (!result.Ok && (result.Ambiguous || result.MatchCount == 0))
            {
                var evidence = TaskConfidenceEvaluator.Evaluate(
                    new TaskConfidenceEvidence
                    {
                        CandidateScores = result.Candidates.Select(candidate =>
                            FuzzyMatcher.Score(request.Target, candidate)).ToArray(),
                        TargetExplicitlyNamed = !string.IsNullOrWhiteSpace(request.Target),
                        ChangesExternalState = safety?.ChangesExternalState == true,
                        AmbiguousField = result.Ambiguous,
                    },
                    _settings.Current,
                    actionIsRiskyOrImportant: safety?.ChangesExternalState == true);
                _tasks.SetConfidence(request.TaskId, evidence.Confidence);
                lock (_pendingLock)
                    _pendingClarifications[request.TaskId] = request;
                var waiting = _tasks.WaitForClarification(
                    request.TaskId,
                    _tasks.Get(request.TaskId).Steps[3].Id,
                    result.Message,
                    result.Candidates);
                return new BrowserAgentResult
                {
                    Ok = false,
                    TaskId = request.TaskId,
                    Message = result.Message,
                    ClarificationRequired = true,
                    Page = result.State,
                    Task = waiting,
                };
            }
            if (!result.Ok)
            {
                _tasks.SetStepByIndex(request.TaskId, 3, JarvisTaskStepStatus.Failed, result.Message);
                return Failure(result.Message, request.TaskId);
            }
            _tasks.SetConfidence(request.TaskId, TaskConfidence.High);
            _tasks.SetStepByIndex(
                request.TaskId,
                3,
                JarvisTaskStepStatus.Running,
                result.Message);
            _tasks.AddReceipt(request.TaskId, Describe(request) + $" on {result.State?.Url}.");
            return new BrowserAgentResult
            {
                Ok = true,
                TaskId = request.TaskId,
                Message = result.Message,
                Page = result.State,
                Extraction = result.Extraction,
                Task = _tasks.Get(request.TaskId),
            };
        }
        catch (OperationCanceledException)
        {
            return Cancelled(request.TaskId);
        }
        catch (Exception ex)
        {
            _tasks.SetStepByIndex(request.TaskId, 3, JarvisTaskStepStatus.Failed, ex.Message);
            return Failure(ex.Message, request.TaskId);
        }
    }

    private void OnConfirmationResponded(string taskId, bool confirmed)
    {
        BrowserTaskRequest smartStart = null;
        BrowserAgentRouteDecision smartRoute = null;
        BrowserAgentActionRequest request = null;
        lock (_pendingLock)
        {
            if (_pendingSmartStarts.Remove(taskId, out var pendingSmart))
            {
                smartStart = pendingSmart;
                _pendingSmartRoutes.Remove(taskId, out smartRoute);
            }
            if (_pendingConfirmations.Remove(taskId, out var pending))
                request = pending;
        }
        if (smartStart != null)
        {
            if (!confirmed)
            {
                _tasks.Cancel(taskId, "The user declined the smart browser action.");
                return;
            }

            var confirmedStart = CopyRequest(smartStart, riskyActionConfirmed: true, useSmartBackend: true);
            var confirmedRoute = ChooseRoute(confirmedStart);
            _ = Task.Run(async () =>
            {
                var result = await StartRoutedBrowserTaskAsync(
                    taskId,
                    confirmedStart,
                    confirmedRoute,
                    _tasks.TokenFor(taskId));
                if (!result.Ok)
                    AppLog.Warning($"Confirmed smart browser task failed: {result.Message}");
            });
            return;
        }

        if (!confirmed || request == null)
        {
            if (!confirmed)
                _ = _backend.CloseTaskAsync(taskId);
            return;
        }
        _ = Task.Run(async () =>
        {
            var result = await ExecuteApprovedAsync(
                request,
                BrowserAgentSafetyPolicy.Evaluate(request),
                _tasks.TokenFor(taskId));
            if (!result.Ok)
                AppLog.Warning($"Confirmed browser action failed: {result.Message}");
        });
    }

    private void OnClarificationResponded(string taskId, string clarification)
    {
        BrowserTaskRequest pendingStart = null;
        BrowserAgentActionRequest request = null;
        lock (_pendingLock)
        {
            _pendingStarts.Remove(taskId, out pendingStart);
            if (_pendingClarifications.Remove(taskId, out var pending))
                request = pending;
        }
        if (pendingStart != null)
        {
            _ = Task.Run(() => ContinueStartAfterClarificationAsync(
                taskId,
                pendingStart,
                clarification,
                _tasks.TokenFor(taskId)));
            return;
        }
        if (request == null)
        {
            _tasks.AddReceipt(taskId, $"User clarification: {clarification}");
            var task = _tasks.Get(taskId);
            if (task != null && task.Steps.Count > 3)
                _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Running, "Clarification received.");
            return;
        }
        var retry = new BrowserAgentActionRequest
        {
            TaskId = request.TaskId,
            Action = request.Action,
            Role = request.Role,
            Target = clarification,
            Value = request.Value,
            FilePath = request.FilePath,
            ExpectedText = request.ExpectedText,
            ExplicitlyRequested = request.ExplicitlyRequested,
        };
        _ = Task.Run(async () =>
        {
            var result = await ExecuteApprovedAsync(
                retry,
                BrowserAgentSafetyPolicy.Evaluate(retry),
                _tasks.TokenFor(taskId));
            if (!result.Ok)
                AppLog.Warning($"Clarified browser action failed: {result.Message}");
        });
    }

    private static string BuildContextSummary(TaskContextResult context)
    {
        var parts = new List<string>();
        if (context.Source != TaskContextSource.None)
            parts.Add(context.Source.ToString());
        if (!string.IsNullOrWhiteSpace(context.ActiveWindowTitle))
            parts.Add(context.ActiveWindowTitle);
        if (!string.IsNullOrWhiteSpace(context.Summary))
            parts.Add(context.Summary);
        return string.Join(" · ", parts);
    }

    private static string ContextReceipt(TaskContextResult context) => context.Source switch
    {
        TaskContextSource.SelectedText => "Captured selected text as task context.",
        TaskContextSource.Clipboard => "Captured explicitly requested clipboard text as task context.",
        TaskContextSource.ActiveWindow => "Captured the active window title as task context.",
        _ => "No private context was captured.",
    };

    private static string PageSummary(BrowserPageState page) =>
        $"{page.Title} · {page.InteractiveElements.Count} interactive elements";

    private void ApplySmartProgress(string taskId, BrowserSmartTaskResult result)
    {
        if (result?.Progress == null)
            return;
        var clean = result.Progress
            .Where(step => !string.IsNullOrWhiteSpace(step.Title))
            .Take(8)
            .Select(step =>
            {
                var title = CleanUserText(step.Title, 120);
                var detail = CleanUserText(step.Detail, 220);
                return string.IsNullOrWhiteSpace(detail) ? title : $"{title}: {detail}";
            });
        foreach (var line in clean)
            _tasks.AddReceipt(taskId, line);
    }

    private static string CleanSmartSummary(BrowserSmartTaskResult result)
    {
        var text = string.IsNullOrWhiteSpace(result?.Summary)
            ? result?.Message
            : result.Summary;
        text = CleanUserText(text, 2000);
        if (string.IsNullOrWhiteSpace(text))
            return result?.Ok == true
                ? "Task completed."
                : "Browser-use could not complete the task.";
        return text;
    }

    private void RememberSuccessfulSmartWorkflow(
        string taskId,
        BrowserTaskRequest request,
        BrowserSmartTaskResult result,
        BrowserAgentRouteDecision route)
    {
        if (_workflowMemory == null || !_settings.Current.LearnBrowserWorkflows)
            return;
        if (!BrowserAgentSafetyPolicy.IsSafeHttpUrl(request.StartUrl, out var uri))
            return;

        try
        {
            var learnedSteps = result.Progress?
                .Select(step => step.Title)
                .Where(step => !string.IsNullOrWhiteSpace(step))
                .ToArray() ?? Array.Empty<string>();
            var workflow = _workflowMemory.SaveSuccessfulWorkflow(
                uri.Host,
                GuessTaskType(request.OriginalUserCommand),
                request.OriginalUserCommand,
                request.OriginalUserCommand,
                result.Backend,
                learnedSteps.Length > 0 ? learnedSteps : ["Open page", "Read visible page", "Return verified summary"],
                ["Open start URL", "Read visible text/DOM before using screenshots"],
                [],
                route.Safety.ChangesExternalState
                    ? ["one upfront confirmation before state-changing action", "stop before checkout/payment/final submission"]
                    : ["read-only or preview-only task"],
                route.Safety.ChangesExternalState,
                result.DurationMs);
            _tasks.AddReceipt(taskId, $"Learned browser workflow for {workflow.Domain} ({workflow.TaskType}).");
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Failed to save browser workflow memory.");
        }
    }

    private static string CleanBackendName(string backend) =>
        CleanUserText(backend, 120);

    private static string CleanUserText(string value, int maxLength)
    {
        var clean = string.Join(
            " ",
            (value ?? "").ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        clean = System.Text.RegularExpressions.Regex.Replace(
            clean,
            @"https?://(?:localhost|127\.0\.0\.1)(?::\d+)?\S*",
            "[local page]");
        clean = System.Text.RegularExpressions.Regex.Replace(
            clean,
            @"AIza[0-9A-Za-z_\-]{20,}",
            "[redacted-gemini-key]");
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }

    private static BrowserTaskRequest DisableSmartBackend(BrowserTaskRequest request) =>
        CopyRequest(request, riskyActionConfirmed: request.RiskyActionConfirmed, useSmartBackend: false);

    private static BrowserTaskRequest CopyRequest(
        BrowserTaskRequest request,
        bool riskyActionConfirmed,
        bool useSmartBackend) => new()
    {
        Title = request.Title,
        OriginalUserCommand = request.OriginalUserCommand,
        StartUrl = request.StartUrl,
        ExpectedOutcome = request.ExpectedOutcome,
        UseSelectedText = request.UseSelectedText,
        UseClipboard = request.UseClipboard,
        ContextExplicitlyRequested = request.ContextExplicitlyRequested,
        UseSmartBackend = useSmartBackend,
        RiskyActionConfirmed = riskyActionConfirmed,
        AutomationProfileFallbackAllowed = request.AutomationProfileFallbackAllowed,
    };

    private static BrowserTaskRequest NormalizeRequest(BrowserTaskRequest request)
    {
        var startUrl = request.StartUrl;
        if (!BrowserAgentSafetyPolicy.IsSafeHttpUrl(startUrl, out _))
            startUrl = InferStartUrl(request.OriginalUserCommand);
        return new BrowserTaskRequest
        {
            Title = request.Title,
            OriginalUserCommand = request.OriginalUserCommand,
            StartUrl = startUrl,
            ExpectedOutcome = request.ExpectedOutcome,
            UseSelectedText = request.UseSelectedText,
            UseClipboard = request.UseClipboard,
            ContextExplicitlyRequested = request.ContextExplicitlyRequested,
            UseSmartBackend = request.UseSmartBackend,
            RiskyActionConfirmed = request.RiskyActionConfirmed,
            AutomationProfileFallbackAllowed = request.AutomationProfileFallbackAllowed,
        };
    }

    private static string InferStartUrl(string task)
    {
        var normalized = FuzzyMatcher.Normalize(task);
        if (normalized.Contains("youtube", StringComparison.Ordinal))
            return "https://www.youtube.com";
        if (normalized.Contains("amazon", StringComparison.Ordinal))
            return normalized.Contains("amazon com", StringComparison.Ordinal)
                ? "https://www.amazon.com"
                : "https://www.amazon.in";
        if (normalized.Contains("flipkart", StringComparison.Ordinal))
            return "https://www.flipkart.com";
        if (normalized.Contains("devpost", StringComparison.Ordinal))
            return "https://devpost.com";
        if (normalized.Contains("bookmyshow", StringComparison.Ordinal) || normalized.Contains("book my show", StringComparison.Ordinal))
            return "https://in.bookmyshow.com";
        if (normalized.Contains("gmail", StringComparison.Ordinal))
            return "https://mail.google.com";
        if (normalized.Contains("google flights", StringComparison.Ordinal) || normalized.Contains("flight", StringComparison.Ordinal))
            return "https://www.google.com/travel/flights";
        return "";
    }

    private static bool IsDirectStartEnough(BrowserTaskRequest request)
    {
        var normalized = FuzzyMatcher.Normalize(request.OriginalUserCommand);
        return normalized.StartsWith("open ", StringComparison.Ordinal)
            || normalized.StartsWith("go to ", StringComparison.Ordinal)
            || normalized.StartsWith("navigate to ", StringComparison.Ordinal);
    }

    private bool ShouldBlockAutomationProfileFallback(
        BrowserTaskRequest request,
        BrowserAgentRouteDecision route)
    {
        if (_settings.Current.AllowAutomationProfileFallback || request.AutomationProfileFallbackAllowed)
            return false;
        if (route.SelectedBackend == BrowserAgentBackendKind.BrowserCompanionExtension)
            return false;
        var normalized = FuzzyMatcher.Normalize(request.OriginalUserCommand);
        var complex = normalized.Contains("amazon", StringComparison.Ordinal)
            || normalized.Contains("cart", StringComparison.Ordinal)
            || normalized.Contains("youtube", StringComparison.Ordinal)
            || normalized.Contains("oldest", StringComparison.Ordinal)
            || normalized.Contains("find", StringComparison.Ordinal)
            || normalized.Contains("search", StringComparison.Ordinal)
            || normalized.Contains("compare", StringComparison.Ordinal)
            || normalized.Contains("flight", StringComparison.Ordinal)
            || normalized.Contains("hotel", StringComparison.Ordinal)
            || normalized.Contains("bookmyshow", StringComparison.Ordinal)
            || normalized.Contains("devpost", StringComparison.Ordinal);
        return request.UseSmartBackend && complex;
    }

    private static bool MentionsLoggedInContext(string task)
    {
        var normalized = FuzzyMatcher.Normalize(task);
        return normalized.Contains("gmail", StringComparison.Ordinal)
            || normalized.Contains("inbox", StringComparison.Ordinal)
            || normalized.Contains("trash", StringComparison.Ordinal)
            || normalized.Contains("youtube studio", StringComparison.Ordinal)
            || normalized.Contains("logged in", StringComparison.Ordinal)
            || normalized.Contains("my account", StringComparison.Ordinal);
    }

    private static string GuessTaskType(string task)
    {
        var normalized = FuzzyMatcher.Normalize(task);
        if (normalized.Contains("form", StringComparison.Ordinal) || normalized.Contains("fill", StringComparison.Ordinal))
            return "form_fill";
        if (normalized.Contains("cart", StringComparison.Ordinal))
            return "cart_mutation";
        if (normalized.Contains("compare", StringComparison.Ordinal) || normalized.Contains("best", StringComparison.Ordinal) || normalized.Contains("cheapest", StringComparison.Ordinal))
            return "comparison";
        if (normalized.Contains("highest", StringComparison.Ordinal) || normalized.Contains("rank", StringComparison.Ordinal) || normalized.Contains("prize", StringComparison.Ordinal))
            return "rank_visible_options";
        return "browser_task";
    }

    private static string Describe(BrowserAgentActionRequest request) => request.Action switch
    {
        BrowserAgentActionKind.Navigate => $"Navigate to {request.Value}",
        BrowserAgentActionKind.Click => $"Click {request.Target}",
        BrowserAgentActionKind.Fill => $"Fill {request.Target}",
        BrowserAgentActionKind.Select => $"Select {request.Value} in {request.Target}",
        BrowserAgentActionKind.Upload => $"Upload {Path.GetFileName(request.FilePath)} to {request.Target}",
        BrowserAgentActionKind.Press => $"Press {request.Value}",
        BrowserAgentActionKind.Wait => "Wait for the page",
        BrowserAgentActionKind.Verify => $"Verify {request.ExpectedText}",
        BrowserAgentActionKind.Extract => $"Extract {request.Target}",
        _ => request.Action.ToString(),
    };

    private BrowserAgentResult Cancelled(string taskId)
    {
        var task = _tasks.Cancel(taskId, "Browser task cancelled.");
        return new BrowserAgentResult
        {
            Ok = false,
            TaskId = taskId,
            Message = "Browser task cancelled.",
            Task = task,
        };
    }

    private BrowserAgentResult Failure(string message, string taskId = "") => new()
    {
        Ok = false,
        TaskId = taskId,
        Message = message,
        Task = string.IsNullOrWhiteSpace(taskId) ? null : _tasks.Get(taskId),
    };

    private async Task<BrowserAgentResult> OpenInitialPageAsync(
        string taskId,
        BrowserTaskRequest request,
        CancellationToken token)
    {
        _tasks.SetStepByIndex(taskId, 1, JarvisTaskStepStatus.Running);
        var page = await _backend.OpenAsync(taskId, request.StartUrl, token);
        _tasks.AddReceipt(taskId, $"Opened {page.Url} in the controlled {_backend.Status.BrowserChannel} session.");
        _tasks.SetStepByIndex(taskId, 1, JarvisTaskStepStatus.Completed, page.Title);
        _tasks.SetStepByIndex(taskId, 2, JarvisTaskStepStatus.Completed, PageSummary(page));
        _tasks.SetStepByIndex(taskId, 3, JarvisTaskStepStatus.Running, "Waiting for the next safe browser action.");
        return new BrowserAgentResult
        {
            Ok = true,
            TaskId = taskId,
            Message = "Browser task started and the first page was inspected.",
            Page = page,
            Task = _tasks.Get(taskId),
        };
    }

    private async Task ContinueStartAfterClarificationAsync(
        string taskId,
        BrowserTaskRequest request,
        string clarification,
        CancellationToken token)
    {
        try
        {
            var normalized = FuzzyMatcher.Normalize(clarification);
            if (normalized.Contains("cancel", StringComparison.Ordinal))
            {
                await CancelAsync(taskId);
                return;
            }
            if (!normalized.Contains("without", StringComparison.Ordinal))
            {
                _tasks.SetStepByIndex(taskId, 0, JarvisTaskStepStatus.Running, "Retrying context capture.");
                var context = await _contextCapture.CaptureAsync(new TaskContextRequest
                {
                    UserExplicitlyRequested = true,
                    UseSelectedText = request.UseSelectedText,
                    UseClipboard = request.UseClipboard,
                    IncludeActiveWindow = true,
                }, token);
                if (!context.Captured)
                {
                    lock (_pendingLock)
                        _pendingStarts[taskId] = request;
                    _tasks.WaitForClarification(
                        taskId,
                        _tasks.Get(taskId).Steps[0].Id,
                        context.Error,
                        ["Select the text and try again", "Continue without private context", "Cancel"]);
                    return;
                }
                _tasks.SetContext(taskId, BuildContextSummary(context));
                _tasks.AddReceipt(taskId, ContextReceipt(context));
                _tasks.SetStepByIndex(taskId, 0, JarvisTaskStepStatus.Completed, ContextReceipt(context));
            }
            else
            {
                _tasks.SetStepByIndex(
                    taskId,
                    0,
                    JarvisTaskStepStatus.Completed,
                    "Continued without private context after clarification.");
            }
            await StartRoutedBrowserTaskAsync(taskId, request, ChooseRoute(request), token);
        }
        catch (OperationCanceledException)
        {
            _tasks.Cancel(taskId, "Browser task cancelled.");
        }
        catch (Exception ex)
        {
            _tasks.Fail(taskId, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _tasks.ConfirmationResponded -= OnConfirmationResponded;
        _tasks.ClarificationResponded -= OnClarificationResponded;
        await _backend.DisposeAsync();
        if (_smartBackend != null)
            await _smartBackend.DisposeAsync();
    }
}
