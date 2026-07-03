namespace KeyboardWtf.Automation.Input;

using System.Diagnostics;
using KeyboardWtf.Models;

public sealed class KeyboardNavigationResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public int TabPresses { get; init; }
    public BrowserCompanionFocusedElement FocusedElement { get; init; }
    public IReadOnlyList<KeyboardNavigationProgress> Progress { get; init; } = Array.Empty<KeyboardNavigationProgress>();
}

public sealed class KeyboardNavigationExecutor
{
    private readonly IInputController _input;
    private readonly IFocusInspector _focus;
    private readonly InputSafetyPolicy _safety;

    public KeyboardNavigationExecutor(
        IInputController input,
        IFocusInspector focus,
        InputSafetyPolicy safety = null)
    {
        _input = input;
        _focus = focus;
        _safety = safety ?? new InputSafetyPolicy();
    }

    public async Task<KeyboardNavigationResult> ActivateTargetAsync(
        string taskId,
        KeyboardNavigationPlan plan,
        CancellationToken token)
    {
        if (_input == null || _focus == null)
            return Fail("Keyboard navigation fallback is not configured.");
        plan ??= new KeyboardNavigationPlan();
        var started = Stopwatch.StartNew();
        var progress = new List<KeyboardNavigationProgress>();
        var maxTabs = Math.Clamp(plan.MaxTabPresses, 1, 80);
        var lastSignature = "";
        var repeatedFocusCount = 0;

        for (var i = 0; i <= maxTabs; i++)
        {
            token.ThrowIfCancellationRequested();
            if (started.Elapsed > plan.MaxRuntime)
                return Fail("Keyboard navigation fallback timed out.", i, progress);

            var focus = await _focus.InspectBrowserFocusAsync(taskId, token);
            var score = KeyboardNavigationPlanner.ScoreFocus(focus, plan.TargetDescription);
            progress.Add(new KeyboardNavigationProgress { TabPresses = i, FocusedElement = focus, Score = score });

            var signature = FocusSignature(focus);
            if (signature == lastSignature && !string.IsNullOrWhiteSpace(signature))
                repeatedFocusCount++;
            else
                repeatedFocusCount = 0;
            lastSignature = signature;
            if (repeatedFocusCount >= 4)
                return Fail("Keyboard navigation repeated the same focused element too many times.", i, progress);

            if (focus?.Sensitive == true)
                return Fail("Keyboard navigation stopped because a sensitive focused field was reached.", i, progress, focus);

            if (score >= 0.82)
            {
                var activationKey = focus.SpaceLikelySafe && !focus.EnterLikelySafe ? InputKey.Space : InputKey.Enter;
                var context = new InputSafetyContext
                {
                    Kind = plan.SafetyContext.Kind,
                    FocusedElement = focus,
                    UpfrontCartMutationApproved = plan.SafetyContext.UpfrontCartMutationApproved,
                    UserExplicitlyConfirmed = plan.SafetyContext.UserExplicitlyConfirmed,
                    TaskDescription = plan.SafetyContext.TaskDescription,
                };
                var decision = _safety.Evaluate(InputAction.Activate(activationKey, plan.TargetDescription), context);
                if (decision.Risk == InputRisk.Blocked)
                    return Fail(decision.Reason, i, progress, focus);
                if (decision.Risk == InputRisk.NeedsConfirmation)
                    return Fail("Keyboard activation needs confirmation: " + decision.Reason, i, progress, focus);
                var press = await _input.PressKeyAsync(activationKey, context, token);
                return press.Ok
                    ? new KeyboardNavigationResult
                    {
                        Ok = true,
                        Message = $"Focused and activated {plan.TargetDescription} with {activationKey}.",
                        TabPresses = i,
                        FocusedElement = focus,
                        Progress = progress,
                    }
                    : Fail(press.Message, i, progress, focus);
            }

            if (i == maxTabs)
                break;
            var tabContext = new InputSafetyContext
            {
                Kind = InputSafetyContextKind.ReadOnlyNavigation,
                TaskDescription = plan.TargetDescription,
            };
            var tabResult = await _input.PressKeyAsync(InputKey.Tab, tabContext, token);
            if (!tabResult.Ok)
                return Fail(tabResult.Message, i, progress, focus);
            await Task.Delay(80, token);
        }

        return Fail("Keyboard navigation could not focus the requested target within the bounded Tab limit.", maxTabs, progress);
    }

    private static string FocusSignature(BrowserCompanionFocusedElement focus) =>
        focus == null || !focus.Exists
            ? ""
            : string.Join("|", focus.Role, focus.ControlType, focus.Text, focus.AriaLabel, focus.Bounds.X, focus.Bounds.Y);

    private static KeyboardNavigationResult Fail(
        string message,
        int tabPresses = 0,
        IReadOnlyList<KeyboardNavigationProgress> progress = null,
        BrowserCompanionFocusedElement focus = null) =>
        new()
        {
            Ok = false,
            Message = message,
            TabPresses = tabPresses,
            FocusedElement = focus,
            Progress = progress ?? Array.Empty<KeyboardNavigationProgress>(),
        };
}
