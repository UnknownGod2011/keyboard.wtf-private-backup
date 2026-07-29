namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Text.RegularExpressions;

public sealed class BrowserAutomationSafetyDecision
{
    public bool MayStart { get; init; }
    public bool PauseBeforeSensitiveStep { get; init; }
    public bool FinalSubmitExplicitlyRequested { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class BrowserAutomationSafetyPolicy
{
    private static readonly Regex ManualOnly = new(
        @"\b(password|passcode|otp|one[- ]time password|two[- ]factor|2fa|captcha|security challenge|banking|bank account|credit card|debit card|cvv|payment|checkout|place (?:the )?order|buy now)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FinalSubmit = new(
        @"\b(submit(?:ting)?|send(?:ing)?|post(?:ing)?|publish(?:ing)?|delet(?:e|ing)|register(?:ing)?|join(?:ing)?|appl(?:y|ying))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FinalSubmitNegated = new(
        @"\b(?:do\s+not|don't|dont|never|without|avoid|must\s+not|should\s+not|shouldn't|stop\s+before|leave\s+(?:it\s+)?(?:unsubmitted|unsent|unposted))\b[^.!?;\r\n]{0,48}\b(?:submit(?:ting)?|send(?:ing)?|post(?:ing)?|publish(?:ing)?|delet(?:e|ing)|register(?:ing)?|join(?:ing)?|appl(?:y|ying))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SearchOrNavigationTarget = new(
        @"\b(search|find|lookup|look up|query|compare|report|repository|address|url|go|continue|done|date|calendar|next)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EnterMayCommitUserContent = new(
        @"\b(message|chat|composer|comment|reply|form|submit|send|post|publish|delete|register|join|apply|checkout|order|payment)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PersonalField = new(
        @"\b(full\s+name|your\s+name|email(?:\s+address)?|phone|mobile|street\s+address|home\s+address|date\s+of\s+birth|dob|age)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public BrowserAutomationSafetyDecision EvaluateRequest(string request)
    {
        var text = (request ?? "").Trim();
        if (ManualOnly.IsMatch(text))
        {
            return new BrowserAutomationSafetyDecision
            {
                MayStart = true,
                PauseBeforeSensitiveStep = true,
                Reason = "The workflow may proceed until the password, security, payment, or order boundary, then pause for the user.",
            };
        }

        return new BrowserAutomationSafetyDecision
        {
            MayStart = true,
            PauseBeforeSensitiveStep = false,
            FinalSubmitExplicitlyRequested = IsFinalSubmitExplicitlyRequested(text),
            Reason = "The requested browser interaction is allowed; sensitive gates remain manual.",
        };
    }

    public BrowserAutomationSafetyDecision EvaluateAction(
        string request,
        BrowserPlannerAction action)
    {
        var target = string.Join(
            " ",
            action?.TargetName ?? "",
            action?.TargetRole ?? "",
            action?.Text ?? "");
        if (ManualOnly.IsMatch(target))
        {
            return new BrowserAutomationSafetyDecision
            {
                MayStart = false,
                PauseBeforeSensitiveStep = true,
                Reason = "This password, security, payment, checkout, or order step requires the user.",
            };
        }

        if (action?.Type is "type" or "paste"
            && PersonalField.IsMatch(target)
            && (string.IsNullOrWhiteSpace(action.Text)
                || !(request ?? "").Contains(
                    action.Text,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return new BrowserAutomationSafetyDecision
            {
                MayStart = false,
                PauseBeforeSensitiveStep = true,
                Reason = "This personal-information field needs a value supplied by the user.",
            };
        }

        if (action?.Type == "click"
            && FinalSubmit.IsMatch(target)
            && !IsFinalSubmitExplicitlyRequested(request))
        {
            return new BrowserAutomationSafetyDecision
            {
                MayStart = false,
                PauseBeforeSensitiveStep = true,
                Reason = "The final submit/send/post/delete/register action was not explicitly requested.",
            };
        }

        if (action?.Type == "press_key"
            && string.Equals(action.Key, "Enter", StringComparison.OrdinalIgnoreCase)
            && (EnterMayCommitUserContent.IsMatch(target)
                || (!string.Equals(
                        action.TargetRole,
                        "searchbox",
                        StringComparison.OrdinalIgnoreCase)
                    && !SearchOrNavigationTarget.IsMatch(target)
                    && !SearchOrNavigationTarget.IsMatch(request ?? "")))
            && !IsFinalSubmitExplicitlyRequested(request))
        {
            return new BrowserAutomationSafetyDecision
            {
                MayStart = false,
                PauseBeforeSensitiveStep = true,
                Reason = "Pressing Enter here could submit or send. The user did not explicitly request the final action.",
            };
        }

        return new BrowserAutomationSafetyDecision
        {
            MayStart = true,
            FinalSubmitExplicitlyRequested = IsFinalSubmitExplicitlyRequested(request),
            Reason = "The grounded page action is allowed.",
        };
    }

    private static bool IsFinalSubmitExplicitlyRequested(string request)
    {
        var text = request ?? "";
        return FinalSubmit.IsMatch(text) && !FinalSubmitNegated.IsMatch(text);
    }
}
