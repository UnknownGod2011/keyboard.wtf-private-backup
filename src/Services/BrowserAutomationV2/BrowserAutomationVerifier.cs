namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KeyboardWtf.Helpers;

public sealed class BrowserVerificationResult
{
    public bool Verified { get; init; }
    public bool Partial { get; init; }
    public string Evidence { get; init; } = "";
    public string Error { get; init; } = "";
}

public sealed class BrowserAutomationVerifier
{
    public BrowserVerificationResult VerifyAction(
        BrowserPlannerAction action,
        BrowserActionReceipt receipt)
    {
        if (receipt == null || !receipt.Ok)
            return Failed(receipt?.Error ?? "No action receipt was returned.");

        return action.Type switch
        {
            "navigate" or "open_tab" =>
                IsLikelyNavigationRegression(receipt.BeforeUrl, receipt.AfterUrl)
                    ? Failed("The action navigated backward to a less specific state.")
                    : receipt.Navigation || !string.Equals(receipt.BeforeUrl, receipt.AfterUrl, StringComparison.Ordinal)
                    ? Passed($"Navigation reached {SafeDomain(receipt.AfterUrl)}.")
                    : Failed("The page URL did not change."),
            "type" =>
                receipt.ValueMatches
                    ? Passed("The live field value matches the requested text.")
                    : !receipt.NoVisibleChange
                        ? Progressed("The page reacted to text entry, but the value is not committed yet.")
                        : Failed("The live field value did not match after text entry."),
            "paste" =>
                receipt.ValueMatches
                    ? Passed("The destination contains the requested pasted text.")
                    : Failed("The live field value did not match after text entry."),
            "select_option" =>
                receipt.SelectedChanged || !receipt.NoVisibleChange
                    ? Passed("The live option state changed.")
                    : Failed("The option state did not change."),
            "check" or "uncheck" =>
                receipt.CheckedChanged
                    ? Passed("The live checked state changed.")
                    : Failed("The checked state did not change."),
            "click" or "press_key" =>
                receipt.Navigation
                || receipt.SelectedChanged
                || receipt.CheckedChanged
                || !receipt.NoVisibleChange
                    ? Passed("The page produced a live state change.")
                    : Failed("The action produced no visible page change."),
            "focus" or "scroll" => Passed("The helper acknowledged the live page action."),
            _ => Passed("The helper returned a successful action receipt."),
        };
    }

    public BrowserVerificationResult VerifyTerminal(
        string request,
        BrowserPlannerDecision decision,
        BrowserPageSnapshot page,
        IReadOnlyList<BrowserActionReceipt> receipts)
    {
        var text = IntentText(request);

        if (RequestsClipboardOutput(text))
        {
            var clipboard = ClipboardHelper.GetText();
            var expected = decision.CompletionValue;
            if (string.IsNullOrWhiteSpace(expected))
                expected = decision.ResultSummary;
            return !string.IsNullOrWhiteSpace(expected)
                && string.Equals(clipboard, expected, StringComparison.Ordinal)
                    ? Passed($"Clipboard content verified ({HashLength(clipboard)}).")
                    : Failed("Clipboard read-back did not match the requested result.");
        }

        if (Regex.IsMatch(text, @"\b(oldest|latest|newest|most recent).*\b(video|youtube)\b|\bplay\b.*\bvideo\b"))
        {
            var watchPage = Uri.TryCreate(page.Url, UriKind.Absolute, out var uri)
                && uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase)
                && page.Url.Contains("v=", StringComparison.OrdinalIgnoreCase);
            if (!watchPage)
                return Failed("The controlled tab is not on a verified YouTube watch page.");

            if (text.Contains("oldest", StringComparison.Ordinal)
                && !receipts.Any(receipt =>
                    receipt.Ok
                    && receipt.Action == "click"
                    && receipt.TargetName.Contains("oldest", StringComparison.OrdinalIgnoreCase)
                    && !receipt.NoVisibleChange))
            {
                return Failed("No current-task receipt proves that the visible Oldest control was activated.");
            }

            var expectedChannel = ExtractBetween(
                request,
                @"\b(?:oldest|latest|newest|most recent)\s+(.+?)\s+(?:video|youtube)\b");
            if (!string.IsNullOrWhiteSpace(expectedChannel)
                && !ContainsMeaningfulTokens(
                    $"{page.Title} {page.PageText}",
                    expectedChannel,
                    minimumMatches: 1))
            {
                return Failed("The watch page does not contain evidence for the requested channel/entity.");
            }

            return Passed("The requested YouTube ordering was used and a matching watch page is open.");
        }

        if (Regex.IsMatch(text, @"\badd\b.+\bto (?:my |the )?cart\b"))
        {
            var addReceipt = receipts.LastOrDefault(receipt =>
                receipt.Ok
                && receipt.Action == "click"
                && receipt.TargetName.Contains("cart", StringComparison.OrdinalIgnoreCase));
            var cartState = page.PageText.Contains("added to cart", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(page.PageText, @"\bcart\s*\(\s*[1-9]\d*\s*\)", RegexOptions.IgnoreCase)
                || Regex.IsMatch(page.PageText, @"\bcart\s+[1-9]\d*\b", RegexOptions.IgnoreCase);
            var requestedProduct = ExtractBetween(
                request,
                @"\badd\s+(.+?)\s+to\s+(?:my\s+|the\s+)?cart\b");
            var entityPresent = string.IsNullOrWhiteSpace(requestedProduct)
                || ContainsMeaningfulTokens(
                    $"{page.Title} {page.PageText}",
                    requestedProduct,
                    minimumMatches: 2);
            return addReceipt != null && entityPresent && (cartState || !addReceipt.NoVisibleChange)
                ? Passed("The intended Add to Cart action changed the live page/cart state.")
                : Failed("The intended product and changed cart state could not both be verified.");
        }

        if (Regex.IsMatch(text, @"\b(fill|answer)\b.*\b(form|fields|quiz)\b"))
        {
            var typed = receipts.Where(receipt => receipt.Action is "type" or "paste").ToArray();
            var selected = receipts.Where(receipt =>
                (receipt.Action is "click" or "select_option" or "check")
                && receipt.Ok
                && (receipt.SelectedChanged || receipt.CheckedChanged)).ToArray();
            return typed.Length + selected.Length > 0
                && typed.All(receipt => receipt.ValueMatches)
                ? Passed($"Verified {typed.Length} filled fields and {selected.Length} selections; Submit was not used.")
                : Failed("No verified form field changes were recorded.");
        }

        // A source result is not the requested outcome when the user also asked
        // to place it in an editor. Require the destination write to be verified
        // before the generic read/summary branch can declare success.
        if (Regex.IsMatch(text, @"\b(paste|insert|type|write|place|put)\b")
            && Regex.IsMatch(text, @"\b(google docs|document|docs|doc|editor|textbox|text area)\b"))
        {
            var paste = receipts.LastOrDefault(receipt =>
                receipt.Ok
                && (receipt.Action is "paste" or "type")
                && receipt.ValueMatches
                && IsDestinationEditorReceipt(text, page, receipt));
            return paste != null
                ? Passed("The destination editor contains the requested text.")
                : Failed("The requested text was not verified in the destination editor.");
        }

        if (Regex.IsMatch(text, @"\b(summari[sz]e|what is on|read|extract|inspect|report)\b"))
        {
            return !string.IsNullOrWhiteSpace(decision.ResultSummary)
                && !string.IsNullOrWhiteSpace(page.PageText)
                    ? Passed($"Result is grounded in a live page snapshot ({page.PageText.Length} characters observed).")
                    : Failed("A grounded page summary was not produced.");
        }

        var lastChange = receipts.LastOrDefault(receipt => receipt.Ok && !receipt.NoVisibleChange);
        if (lastChange != null)
        {
            var requestedControl = ExtractBetween(
                request,
                @"\b(?:click|press|choose|select|check|uncheck|open)\s+(?:the\s+)?(.+?)(?:\s+(?:button|tab|link|control|option))?(?:\s+on\s+this\s+page)?[.!?]?$");
            if (string.IsNullOrWhiteSpace(requestedControl)
                || ContainsMeaningfulTokens(
                    lastChange.TargetName,
                    requestedControl,
                    minimumMatches: 1))
            {
                return Passed("The requested live page state change was independently observed.");
            }
        }

        return Failed("No current-task evidence proves the requested outcome.");
    }

    internal static bool IsLikelyNavigationRegression(string beforeUrl, string afterUrl)
    {
        if (!Uri.TryCreate(beforeUrl, UriKind.Absolute, out var before)
            || !Uri.TryCreate(afterUrl, UriKind.Absolute, out var after)
            || !before.Host.Equals(after.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var beforePath = NormalizePath(before.AbsolutePath);
        var afterPath = NormalizePath(after.AbsolutePath);
        var afterIsSameOrAncestor = beforePath.Equals(
                afterPath,
                StringComparison.OrdinalIgnoreCase)
            || (afterPath.Length > 0
                && beforePath.StartsWith(
                    $"{afterPath}/",
                    StringComparison.OrdinalIgnoreCase))
            || afterPath.Length == 0;
        if (!afterIsSameOrAncestor)
            return false;

        var beforeQueryParts = QueryPartCount(before.Query);
        var afterQueryParts = QueryPartCount(after.Query);
        return (beforeQueryParts > 0 && afterQueryParts == 0)
            || (beforePath.Length > afterPath.Length
                && afterQueryParts <= beforeQueryParts);
    }

    private static bool IsDestinationEditorReceipt(
        string requestIntent,
        BrowserPageSnapshot page,
        BrowserActionReceipt receipt)
    {
        if (page.TabId > 0
            && receipt.TabId.HasValue
            && receipt.TabId.Value != page.TabId)
        {
            return false;
        }

        var targetUrl = string.IsNullOrWhiteSpace(receipt.AfterUrl)
            ? page.Url
            : receipt.AfterUrl;
        if (Regex.IsMatch(requestIntent, @"\bgoogle docs\b|\bdocs\b")
            && (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var docsUri)
                || !docsUri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase)
                || !docsUri.AbsolutePath.StartsWith("/document/", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var role = (receipt.TargetRole ?? "").Trim();
        var name = (receipt.TargetName ?? "").Trim();
        var semanticTarget = $"{role} {name}";
        if (Regex.IsMatch(
            semanticTarget,
            @"\b(search|find|filter|address|location|destination|origin)\b",
            RegexOptions.IgnoreCase))
        {
            return false;
        }

        if (page.ActiveEditable
            && (!string.IsNullOrWhiteSpace(page.ActiveEditorId)
                && string.Equals(
                    receipt.TargetId,
                    page.ActiveEditorId,
                    StringComparison.Ordinal)))
        {
            return true;
        }

        if (Regex.IsMatch(
            name,
            @"\b(document|editor|content|body|draft|compose|text\s*area|textarea)\b",
            RegexOptions.IgnoreCase))
        {
            return true;
        }

        // Generic editors do not always expose a useful accessible name.
        // A live textbox is sufficient only on the final active editable tab;
        // search/location controls were already rejected above.
        return page.ActiveEditable
            && role.Equals("textbox", StringComparison.OrdinalIgnoreCase)
            && (!receipt.TabId.HasValue || receipt.TabId.Value == page.TabId);
    }

    internal static bool RequestsClipboardOutput(string request)
    {
        var text = IntentText(request);
        return Regex.IsMatch(
            text,
            @"\b(?:copy|save|send|place|put)\b.{0,120}\b(?:to|into|on)\s+(?:the\s+)?clipboard\b"
            + @"|\bcopy\b.{0,120}\bclipboard\b",
            RegexOptions.IgnoreCase);
    }

    private static string IntentText(string request)
    {
        // Quoted strings are data to type/paste, not orchestration intent.
        // Without this separation, content such as "original clipboard" can
        // incorrectly change a document-write request into a clipboard task.
        var withoutQuotedPayloads = Regex.Replace(
            request ?? "",
            "[\"“](?:[^\"”]|\\\\.){1,8000}[\"”]",
            "\"<quoted payload>\"");
        return withoutQuotedPayloads.ToLowerInvariant();
    }

    private static BrowserVerificationResult Passed(string evidence) => new()
    {
        Verified = true,
        Evidence = evidence,
    };

    private static BrowserVerificationResult Failed(string error) => new()
    {
        Verified = false,
        Error = error,
    };

    private static BrowserVerificationResult Progressed(string evidence) => new()
    {
        Verified = false,
        Partial = true,
        Evidence = evidence,
        Error = "The interaction changed the page but still requires a grounded follow-up.",
    };

    private static string SafeDomain(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Host : "the requested page";

    private static string NormalizePath(string value) =>
        (value ?? "").Trim().Trim('/');

    private static int QueryPartCount(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? 0
            : value.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Length;

    private static string ExtractBetween(string value, string pattern)
    {
        var match = Regex.Match(value ?? "", pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static bool ContainsMeaningfulTokens(
        string observed,
        string expected,
        int minimumMatches)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "my", "on", "in", "from", "for", "of", "and",
            "video", "youtube", "channel", "product", "mouse", "bottle",
        };
        var tokens = Regex.Matches(expected ?? "", @"[\p{L}\p{N}]{2,}")
            .Select(match => match.Value)
            .Where(token => !ignored.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tokens.Length == 0)
            return true;
        var required = Math.Min(Math.Max(1, minimumMatches), tokens.Length);
        return tokens.Count(token =>
            (observed ?? "").Contains(token, StringComparison.OrdinalIgnoreCase)) >= required;
    }

    private static string HashLength(string value)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")));
        return $"length={value?.Length ?? 0}, sha256={hash[..12]}…";
    }
}
