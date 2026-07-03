namespace KeyboardWtf.Services;

using System.Text.RegularExpressions;
using KeyboardWtf.Models;

public static class BrowserAgentSafetyPolicy
{
    private static readonly string[] ManualOnlyTerms =
    [
        "pay", "payment", "checkout", "place order", "card number", "credit card", "debit card",
        "cvv", "bank", "money transfer", "wire transfer", "otp", "one time password",
        "verification code", "password", "captcha", "government submit", "medical submit",
        "legal submit",
    ];

    private static readonly string[] ConfirmationTerms =
    [
        "add to cart", "buy now", "submit", "publish", "post", "send", "delete", "remove",
        "unsubscribe", "subscribe", "save changes", "confirm booking", "book now", "create account",
        "change email", "change password",
    ];

    public static BrowserSafetyDecision Evaluate(
        BrowserAgentActionRequest request,
        string originalUserCommand = "")
    {
        if (request == null)
            return Block("No browser action was provided.");
        var combined = Normalize(
            $"{request.Action} {request.Role} {request.Target} {request.Value} {request.ExpectedText}");
        var original = Normalize(originalUserCommand);

        if (request.Action == BrowserAgentActionKind.Navigate)
        {
            if (!IsSafeHttpUrl(request.Value, out _))
                return Block("Browser navigation is limited to http and https URLs.");
            return Safe();
        }
        if (request.Action == BrowserAgentActionKind.Upload)
        {
            if (!request.ExplicitlyRequested || !ContainsAny(original, "upload", "attach", "choose file", "select file"))
            {
                return new BrowserSafetyDecision
                {
                    Risk = BrowserActionRisk.ConfirmationRequired,
                    ChangesExternalState = true,
                    Reason = "File upload must be explicitly requested by the user.",
                };
            }
            if (string.IsNullOrWhiteSpace(request.FilePath)
                || !Path.IsPathFullyQualified(request.FilePath)
                || !File.Exists(request.FilePath))
            {
                return Block("The upload path must be an existing absolute local file.");
            }
            if (SmartFileSearchService.IsRiskyFile(request.FilePath))
                return Block("Executable, script, installer, shortcut, and registry files cannot be uploaded.");
        }

        if (ContainsAny(combined, ManualOnlyTerms))
        {
            return new BrowserSafetyDecision
            {
                Risk = BrowserActionRisk.ManualOnly,
                ChangesExternalState = true,
                Reason = "This step involves payment, authentication, CAPTCHA, or a protected final submission and must be completed manually.",
            };
        }
        if (request.Action == BrowserAgentActionKind.Press
            && !AllowedKey(request.Value))
        {
            return Block("Only non-submitting navigation keys are allowed.");
        }
        if (ContainsAny(combined, ConfirmationTerms))
        {
            return new BrowserSafetyDecision
            {
                Risk = BrowserActionRisk.ConfirmationRequired,
                ChangesExternalState = true,
                Reason = "This action can change external state and needs explicit confirmation.",
            };
        }

        var changesState = request.Action is BrowserAgentActionKind.Click
            or BrowserAgentActionKind.Fill
            or BrowserAgentActionKind.Select
            or BrowserAgentActionKind.Upload;
        return new BrowserSafetyDecision
        {
            Risk = BrowserActionRisk.Safe,
            ChangesExternalState = changesState,
            Reason = changesState
                ? "The action is reversible and does not match a protected final action."
                : "The action is read-only or navigational.",
        };
    }

    public static BrowserSafetyDecision EvaluateTaskIntent(
        string task,
        bool riskyActionConfirmed = false)
    {
        var normalized = Normalize(task);
        if (string.IsNullOrWhiteSpace(normalized))
            return Block("A browser task description is required.");

        var protectedFinalAction = ContainsUnnegatedAny(
            normalized,
            "pay",
            "payment",
            "place order",
            "complete checkout",
            "finish checkout",
            "checkout now",
            "book ticket",
            "confirm booking",
            "submit application",
            "submit form",
            "publish",
            "post publicly",
            "send email",
            "delete",
            "archive",
            "change password",
            "banking",
            "otp",
            "one time password",
            "captcha");
        if (protectedFinalAction)
        {
            return new BrowserSafetyDecision
            {
                Risk = BrowserActionRisk.ManualOnly,
                ChangesExternalState = true,
                Reason = "This browser task includes payment, checkout, publishing, sending, deleting, OTP/CAPTCHA, or another protected final action and must stop for manual review.",
            };
        }

        var needsConfirmation = ContainsUnnegatedAny(
            normalized,
            "add to cart",
            "add these to cart",
            "add it to cart",
            "add it to my cart",
            "add them to my cart",
            "add to my cart",
            "submit",
            "upload",
            "publish",
            "post",
            "send",
            "delete",
            "archive",
            "save changes",
            "create account",
            "change account")
            || (ContainsAny(normalized, "cart") && ContainsAny(normalized, "add", "buy"));
        if (needsConfirmation && !riskyActionConfirmed)
        {
            return new BrowserSafetyDecision
            {
                Risk = BrowserActionRisk.ConfirmationRequired,
                ChangesExternalState = true,
                Reason = "This browser task can change external state and needs one explicit confirmation before Jarvis proceeds.",
            };
        }

        return new BrowserSafetyDecision
        {
            Risk = BrowserActionRisk.Safe,
            ChangesExternalState = needsConfirmation,
            Reason = needsConfirmation
                ? "The risky boundary was explicitly confirmed; Jarvis must still stop before checkout, payment, or final submission."
                : "This task appears read-only, navigational, or preview-only.",
        };
    }

    public static bool IsSafeHttpUrl(string value, out Uri uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool AllowedKey(string value)
    {
        var key = Normalize(value);
        return key is "tab" or "shift tab" or "escape" or "arrowup" or "arrowdown"
            or "arrowleft" or "arrowright" or "pageup" or "pagedown" or "home" or "end";
    }

    private static BrowserSafetyDecision Safe() => new()
    {
        Risk = BrowserActionRisk.Safe,
        Reason = "Safe browser navigation.",
    };

    private static BrowserSafetyDecision Block(string reason) => new()
    {
        Risk = BrowserActionRisk.Blocked,
        Reason = reason,
    };

    private static bool ContainsAny(string value, params string[] terms)
    {
        foreach (var term in terms.Select(Normalize).Where(term => term.Length > 0))
        {
            if (term.Contains(' ', StringComparison.Ordinal))
            {
                if (value.Contains(term, StringComparison.Ordinal))
                    return true;
                continue;
            }

            if (Regex.IsMatch(value, $@"(?:^|\s){Regex.Escape(term)}(?:$|\s)", RegexOptions.IgnoreCase))
                return true;
        }
        return false;
    }

    private static bool ContainsUnnegatedAny(string normalizedValue, params string[] terms)
    {
        foreach (var term in terms.Select(Normalize).Where(term => term.Length > 0))
        {
            foreach (var index in FindTermIndexes(normalizedValue, term))
            {
                var prefix = normalizedValue[..index].TrimEnd();
                var tail = prefix.Length <= 32 ? prefix : prefix[^32..];
                if (!tail.EndsWith("do not", StringComparison.Ordinal)
                    && !tail.EndsWith("dont", StringComparison.Ordinal)
                    && !tail.EndsWith("never", StringComparison.Ordinal)
                    && !tail.EndsWith("stop before", StringComparison.Ordinal)
                    && !tail.EndsWith("without", StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static IEnumerable<int> FindTermIndexes(string normalizedValue, string term)
    {
        var pattern = $@"(?:^|\s){Regex.Escape(term)}(?:$|\s)";
        foreach (Match match in Regex.Matches(normalizedValue, pattern, RegexOptions.IgnoreCase))
        {
            var value = match.Value;
            yield return match.Index + (value.Length > 0 && char.IsWhiteSpace(value[0]) ? 1 : 0);
        }
    }

    private static string Normalize(string value) =>
        FuzzyMatcher.Normalize(value);
}
