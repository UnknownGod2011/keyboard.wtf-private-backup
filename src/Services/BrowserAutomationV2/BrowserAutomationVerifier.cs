namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Globalization;
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
                && IsLikelyNavigationRegression(receipt.BeforeUrl, receipt.AfterUrl)
                    ? Failed("The action navigated backward to a less specific state.")
                    : receipt.Navigation
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
        IReadOnlyList<BrowserActionReceipt> receipts,
        bool orderedSourceCollectionVerified = false)
    {
        var text = IntentText(request);
        var groundedInformationResult = RequestsGroundedInformation(text)
            && !string.IsNullOrWhiteSpace(decision.ResultSummary)
            && HasGroundedResultEvidence(page, decision.ResultSummary);

        if (!groundedInformationResult
            && !RequestAllowsNavigationRegression(text)
            && receipts.Any(receipt =>
                receipt?.Ok == true
                && !receipt.Stale
                && receipt.Navigation
                && IsLikelyNavigationRegression(receipt.AfterUrl, page?.Url)))
        {
            return Failed(
                "The final page discarded a more specific verified state reached earlier in this task.");
        }

        var orderedSource = ExtractOrderedSourceIdentity(request);
        if (!string.IsNullOrWhiteSpace(orderedSource))
        {
            if (!orderedSourceCollectionVerified)
            {
                return Failed(
                    "No current-task evidence proves that the requested publisher/source collection was established before choosing the ordered item.");
            }
            if (!PageHasExactSourceIdentity(page, orderedSource))
            {
                return Failed(
                    "The final page does not identify the requested publisher/source as the creator of the opened item.");
            }
        }

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

        var requestedMutationSteps = CountRequestedMutationSteps(text);
        var destinationEditorTransfer = Regex.IsMatch(
                text,
                @"\b(paste|insert|type|write|place|put)\b")
            && Regex.IsMatch(
                text,
                @"\b(google docs|document|docs|doc|editor|textbox|text area)\b");
        var formLikeMutation = !destinationEditorTransfer
            && (Regex.IsMatch(
                text,
                @"\b(fill|answer)\b.*\b(form|fields|quiz|survey|questionnaire)\b")
                || (requestedMutationSteps > 1
                && Regex.IsMatch(
                    text,
                    @"\b(fill|type|paste|set|choose|select|check|uncheck)\b")
                    && receipts.Any(IsFormControlReceipt)));
        if (formLikeMutation)
        {
            var missing = (page?.Controls ?? Array.Empty<BrowserControl>())
                .Where(control => control.Visible && control.Enabled
                    && control.FieldRequired == true && !control.FieldAnswered
                    && !string.IsNullOrWhiteSpace(control.FieldId))
                .GroupBy(control => control.FieldId, StringComparer.Ordinal)
                .Select(group => group.First().FieldLabel)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (missing.Length > 0)
                return Failed("Required form questions remain unanswered: "
                    + string.Join("; ", missing)
                    + ". Use the provided context, or ask for genuinely missing required information before completion.");

            var expectedStates = receipts
                .Where(HasExpectedFormState)
                .GroupBy(
                    receipt => string.Join(
                        "|",
                        string.IsNullOrWhiteSpace(receipt.TargetFieldId) ? receipt.TargetId : receipt.TargetFieldId,
                        receipt.TargetRole,
                        receipt.TargetName),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToArray();
            var satisfied = expectedStates.Count(receipt =>
                FinalPageStillSatisfies(page, receipt));
            var required = Math.Max(1, requestedMutationSteps);
            if (expectedStates.Length < required)
            {
                return Failed(
                    $"Only {expectedStates.Length} of {required} requested form states have grounded action evidence.");
            }

            return satisfied >= required
                ? Passed(
                    $"The final live page still contains {satisfied} verified form states; Submit was not used.")
                : Failed(
                    $"Only {satisfied} of {required} requested form states remain present in the final live page.");
        }

        // A source result is not the requested outcome when the user also asked
        // to place it in an editor. Require the destination write to be verified
        // before the generic read/summary branch can declare success.
        if (destinationEditorTransfer)
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

        if (RequestsGroundedInformation(text))
        {
            return groundedInformationResult
                    ? Passed($"Result is grounded in the final live page ({page.PageText.Length} characters observed).")
                    : Failed("The reported result is not supported by the final live page.");
        }

        if (requestedMutationSteps > 1)
        {
            var verifiedMutations = receipts
                .Where(IsVerifiedMutationReceipt)
                .Select(receipt => string.Join(
                    "|",
                    receipt.Action,
                    receipt.TargetId,
                    receipt.TargetRole,
                    receipt.TargetName))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (verifiedMutations < requestedMutationSteps)
            {
                return Failed(
                    $"Only {verifiedMutations} of {requestedMutationSteps} explicitly requested browser action steps have distinct verified evidence.");
            }
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

    internal static int CountRequestedMutationSteps(string request)
    {
        var text = IntentText(request);
        return Regex.Matches(
            text,
            @"\b(?:click|press|choose|select|check|uncheck|fill|type|paste|set|close|dismiss|replace|search|navigate|open)\b",
            RegexOptions.IgnoreCase).Count;
    }

    internal static bool RequestsGroundedInformation(string request)
    {
        var text = IntentText(request);
        var information = Regex.Matches(
            text,
            @"\b(?:summari[sz]e|read|extract|inspect|report|find|identify|answer)\b"
            + @"|\btell\s+me\b"
            + @"|^\s*(?:what|who|when|where|which|how)\b",
            RegexOptions.IgnoreCase);
        if (information.Count == 0)
            return false;

        // The last requested outcome owns the terminal contract. "Find X and
        // click it" ends in an action; "click X and tell me what it says"
        // ends in a grounded information result.
        var mutations = Regex.Matches(
            text,
            @"\b(?:click|press|fill|type|paste|set|choose|select|check|uncheck|submit|send|delete|remove|add|open|navigate|search)\b"
            + @"|\bgo\s+to\b",
            RegexOptions.IgnoreCase);
        return mutations.Count == 0
            || information[^1].Index > mutations[^1].Index;
    }

    private static bool HasGroundedResultEvidence(
        BrowserPageSnapshot page,
        string resultSummary)
    {
        if (page == null || string.IsNullOrWhiteSpace(page.PageText))
            return false;

        var observed = string.Join(
            " ",
            page.Title,
            page.PageText,
            string.Join(
                " ",
                (page.Controls ?? Array.Empty<BrowserControl>()).Select(control =>
                    string.Join(" ", ControlIdentity(control), control.Value))));
        var tokens = MeaningfulTokens(resultSummary);
        if (tokens.Length == 0)
            return false;

        // Exact numbers are strong evidence for dates, counts, prices, and
        // other lookup results even when the surrounding prose is paraphrased.
        if (tokens.Any(token => token.Any(char.IsDigit)
                && observed.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var required = tokens.Length == 1 ? 1 : 2;
        return tokens.Count(token =>
            observed.Contains(token, StringComparison.OrdinalIgnoreCase)) >= required;
    }

    private static bool IsVerifiedMutationReceipt(BrowserActionReceipt receipt) =>
        receipt?.Ok == true
        && receipt.Action is not ("focus" or "scroll")
        && (receipt.Navigation
            || receipt.ValueMatches
            || receipt.SelectedChanged
            || receipt.CheckedChanged
            || !receipt.NoVisibleChange);

    private static bool IsFormControlReceipt(BrowserActionReceipt receipt) =>
        receipt?.Ok == true
        && receipt.TargetRole is "textbox" or "searchbox" or "combobox"
            or "listbox" or "option" or "checkbox" or "radio";

    private static bool HasExpectedFormState(BrowserActionReceipt receipt)
    {
        if (!IsFormControlReceipt(receipt))
            return false;
        if (receipt.Action is "type" or "paste" or "select_option")
        {
            return receipt.ValueMatches
                && !string.IsNullOrWhiteSpace(receipt.ExpectedValue);
        }
        if (receipt.Action is "check" or "uncheck")
            return receipt.CheckedChanged;
        return (receipt.SelectedChanged || receipt.CheckedChanged)
            && !string.IsNullOrWhiteSpace(
                string.IsNullOrWhiteSpace(receipt.ExpectedValue)
                    ? receipt.TargetName
                    : receipt.ExpectedValue);
    }

    private static bool FinalPageStillSatisfies(
        BrowserPageSnapshot page,
        BrowserActionReceipt receipt)
    {
        var controls = (page?.Controls ?? Array.Empty<BrowserControl>())
            .Where(control => string.IsNullOrWhiteSpace(receipt.TargetFieldId)
                || string.Equals(control.FieldId, receipt.TargetFieldId, StringComparison.Ordinal))
            .ToArray();
        var live = controls.FirstOrDefault(control =>
                !string.IsNullOrWhiteSpace(receipt.TargetId)
                && string.Equals(
                    control.Id,
                    receipt.TargetId,
                    StringComparison.Ordinal))
            ?? controls.FirstOrDefault(control =>
                string.Equals(
                    control.Role,
                    receipt.TargetRole,
                    StringComparison.OrdinalIgnoreCase)
                && ControlIdentity(control).Contains(
                    Normalize(receipt.TargetName),
                    StringComparison.Ordinal));

        if (receipt.Action is "check" or "uncheck")
        {
            return live != null
                && live.Checked == receipt.Action.Equals(
                    "check",
                    StringComparison.OrdinalIgnoreCase);
        }

        var expected = string.IsNullOrWhiteSpace(receipt.ExpectedValue)
            ? receipt.TargetName
            : receipt.ExpectedValue;
        if (string.IsNullOrWhiteSpace(expected))
            return false;

        if (receipt.Action is "type" or "paste")
        {
            return live != null
                && EquivalentInputValue(live.Value, expected);
        }

        if (live != null
            && (EquivalentInputValue(live.Value, expected)
                || ((live.Selected || live.Checked)
                    && ControlIdentity(live).Contains(
                        Normalize(expected),
                        StringComparison.Ordinal))))
        {
            return true;
        }

        // A custom option may disappear from the graph after the popup closes.
        // Verify it from the live selected option or the owning combobox value.
        return controls.Any(control =>
            (control.Selected
                || control.Checked
                || control.Role.Equals(
                    "combobox",
                    StringComparison.OrdinalIgnoreCase))
            && (EquivalentInputValue(control.Value, expected)
                || ControlIdentity(control).Contains(
                    Normalize(expected),
                    StringComparison.Ordinal)));
    }

    private static string ControlIdentity(BrowserControl control) =>
        Normalize(string.Join(
            " ",
            control?.Text,
            control?.AccessibleName,
            control?.AriaLabel,
            control?.NearbyLabel));

    private static string Normalize(string value) =>
        Regex.Replace(
            (value ?? "").Trim().ToLowerInvariant(),
            @"\s+",
            " ");

    private static bool EquivalentInputValue(
        string observed,
        string expected)
    {
        var actual = Normalize(observed);
        var wanted = Normalize(expected);
        if (wanted.Length == 0)
            return true;
        if (actual.Equals(wanted, StringComparison.Ordinal))
            return true;

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

    internal static bool RequestAllowsNavigationRegression(string request) =>
        Regex.IsMatch(
            request ?? "",
            @"\b(?:go back|return home|start over|restart|reset)\b"
            + @"|\b(?:open|go to|navigate to|visit)\b.{0,80}\b(?:home|homepage|landing page|main page)\b",
            RegexOptions.IgnoreCase);

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

    internal static bool ContainsMeaningfulTokens(
        string observed,
        string expected,
        int minimumMatches)
    {
        var tokens = MeaningfulTokens(expected);
        if (tokens.Length == 0)
            return true;
        var required = Math.Min(Math.Max(1, minimumMatches), tokens.Length);
        return tokens.Count(token =>
            (observed ?? "").Contains(token, StringComparison.OrdinalIgnoreCase)) >= required;
    }

    internal static string ExtractOrderedSourceIdentity(string request)
    {
        var text = IntentText(request);
        if (!Regex.IsMatch(
                text,
                @"\b(?:oldest|latest|newest|most\s+recent)\b",
                RegexOptions.IgnoreCase))
        {
            return "";
        }

        var contentKind = @"(?:[\p{L}\p{N}-]+\s+){0,2}(?:video|upload|post|article|episode|release|content|item)";
        var patterns = new[]
        {
            @"\b(?<source>[\p{L}\p{N}][\p{L}\p{N} .&_-]{0,80}?)['’]s\s+(?:oldest|latest|newest|most\s+recent)\s+" + contentKind,
            @"\b(?:oldest|latest|newest|most\s+recent)\s+" + contentKind + @"\s+(?:by|from)\s+(?<source>[^,.;!?]{1,80}?)(?=\s+(?:on|at|in|via)\b|[,.;!?]|$)",
            @"\b(?:oldest|latest|newest|most\s+recent)\s+(?<source>[\p{L}\p{N}][\p{L}\p{N} .&_-]{0,80}?)\s+(?:video|upload|post|article|episode|release|content|item)\b",
        };
        var matches = patterns
            .SelectMany(pattern => Regex.Matches(
                    text,
                    pattern,
                    RegexOptions.IgnoreCase)
                .Cast<Match>())
            .OrderByDescending(match => match.Index);
        foreach (var match in matches)
        {
            var source = Regex.Replace(
                match.Groups["source"].Value.Trim(),
                @"^(?:(?:please\s+)|(?:(?:can|could|would|will)\s+you\s+)|(?:open|play|find|show|watch|visit)\s+|the\s+)+"
                + @"|\s+(?:official\s+)?(?:channel|profile|account|page)$"
                + @"|\s+(?:instead|please|now)$",
                "",
                RegexOptions.IgnoreCase).Trim();
            if (MeaningfulTokens(source).Length > 0)
                return source;
        }
        return "";
    }

    internal static bool IsVerifiedOrderedSourceCollectionPage(
        BrowserPageSnapshot page,
        string source)
    {
        if (!Uri.TryCreate(page?.Url, UriKind.Absolute, out var uri)
            || IsSearchOrDetailUrl(uri))
        {
            return false;
        }

        var title = Normalize(page.Title);
        var sourceName = Normalize(source);
        var titleIdentifiesSource = title.Equals(sourceName, StringComparison.Ordinal)
            || title.StartsWith(sourceName + " ", StringComparison.Ordinal)
            || title.Contains(" " + sourceName + " ", StringComparison.Ordinal);
        var exactIdentityControl = PageHasExactSourceIdentity(page, source);
        var sourceTokens = MeaningfulTokens(source);
        var path = Uri.UnescapeDataString(uri.AbsolutePath)
            .Replace('-', ' ')
            .Replace('_', ' ');
        if (sourceTokens.Length > 0
            && sourceTokens.All(token =>
                path.Contains(token, StringComparison.OrdinalIgnoreCase))
            && (titleIdentifiesSource || exactIdentityControl))
        {
            return true;
        }

        var hasCollectionSurface = (page.Controls ?? Array.Empty<BrowserControl>()).Any(control =>
            control.Visible
            && control.Role is "link" or "tab" or "button"
            && Regex.IsMatch(
                ControlIdentity(control),
                @"\b(?:videos?|uploads?|posts?|articles?|episodes?|content|releases?|latest|newest|oldest|recent)\b",
                RegexOptions.IgnoreCase));
        return (titleIdentifiesSource || exactIdentityControl) && hasCollectionSurface;
    }

    internal static bool PageHasExactSourceIdentity(
        BrowserPageSnapshot page,
        string source)
    {
        var expected = Normalize(source);
        if (expected.Length == 0)
            return false;
        return (page?.Controls ?? Array.Empty<BrowserControl>()).Any(control =>
        {
            if (!control.Visible || control.Role is not ("link" or "button"))
                return false;
            var candidates = new[]
            {
                control.AccessibleName,
                control.Text,
                control.AriaLabel,
                control.Title,
            };
            return candidates.Any(candidate =>
            {
                var normalized = Normalize(candidate);
                return normalized.Equals(expected, StringComparison.Ordinal)
                    || Regex.IsMatch(
                        normalized,
                        $@"^{Regex.Escape(expected)}\s+(?:official|verified|channel|profile|account)$",
                        RegexOptions.IgnoreCase);
            });
        });
    }

    private static bool IsSearchOrDetailUrl(Uri uri)
    {
        var path = Normalize(uri.AbsolutePath);
        if (Regex.IsMatch(path, @"\b(?:search|results?|watch|item|article|post|video)\b"))
            return true;
        return Regex.IsMatch(
            uri.Query ?? "",
            @"(?:^|[?&])(?:q|query|search|search_query)=",
            RegexOptions.IgnoreCase);
    }

    private static string[] MeaningfulTokens(string value)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "my", "on", "in", "from", "for", "of", "and",
            "is", "are", "was", "were", "it", "this", "that", "shows", "showed",
            "requested", "result", "page", "visible", "video", "youtube", "channel",
            "product", "mouse", "bottle",
        };
        return Regex.Matches(value ?? "", @"[\p{L}\p{N}]{2,}")
            .Select(match => match.Value)
            .Where(token => !ignored.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string HashLength(string value)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")));
        return $"length={value?.Length ?? 0}, sha256={hash[..12]}…";
    }
}
