namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Text.RegularExpressions;

public sealed class BrowserAutomationTaskClassifier
{
    private static readonly Regex CurrentContext = new(
        @"\b(this|current|active)(?:\s+\w+){0,2}\s+(page|tab|website|site|form|document|doc|docs|editor|composer|conversation)|\bon this page\b|\bfrom here\b|\bcontinue from here\b|\bselected text\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitAutomation = new(
        @"\b(browser automation|automate (?:the )?browser|use (?:the )?(?:current|logged[- ]in) (?:browser|chrome)|use this tab)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitAutomationSuppressionBefore = new(
        @"(?:\b(?:do\s+not|don't|dont|never|without|avoid|disable|cancel|stop|pause|turn\s+off)\b(?:\s+(?:using|use|starting|start|launching|launch))?\s*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitAutomationSuppressionAfter = new(
        @"^\s*(?:must|should|is|stays?|remains?)?\s*(?:not|off|disabled|cancelled|canceled|stopped|paused)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OutcomeDiagnosticQuestion = new(
        @"^\s*(?:please\s+)?(?:"
        + @"what\s+(?:happened|went\s+wrong|failed|was\s+(?:the\s+)?(?:issue|problem|error|failure))"
        + @"|what(?:'s|\s+is|\s+was)\s+(?:the\s+)?(?:issue|problem|error|failure|reason)"
        + @"|why\b.{0,180}\b(?:fail(?:ed|ure)?|stop(?:ped)?|error|problem|work(?:ed|ing)?|complete(?:d)?)\b"
        + @"|(?:can|could|would|will)\s+you\s+(?:please\s+)?(?:explain|tell\s+me)\s+(?:what\s+happened|what\s+went\s+wrong|why\b))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OutcomeDiagnosticFollowUpAction = new(
        @"(?:\b(?:and|then|also)\b|[?!.])\s*(?:please\s+)?"
        + @"(?:use\s+browser\s+automation\s+to\s+)?"
        + @"(?:retry|try\s+again|rerun|run\s+again|resume|continue|restart|start|fix|repeat|do\s+it\s+again)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A browser action described as dependent on a future user-controlled
    // transition is not authorized yet. This models temporal intent rather
    // than any destination or example phrase.
    private static readonly Regex DeferredUserControlledInteraction = new(
        @"\b(?:when|once|after)\s+(?:i|we)\b.{0,180}\b(?:navigate|open|reach|switch|move|tell|say|confirm|let\s+you\s+know)\b"
        + @"|\b(?:wait|hold)\b.{0,100}\b(?:until|for\s+(?:(?:me|us)\s+to|my|our))\b"
        + @"|\b(?:not\s+yet|later)\b.{0,120}\b(?:fill|click|enter|paste|submit|continue|automate|use)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocalTimerAction = new(
        @"^\s*(?:please\s+)?(?:(?:set|start|create|make|cancel|stop|pause|resume|list|show|check)\s+)?(?:an?\s+|the\s+|my\s+|active\s+|all\s+)?(?:\w+\s+){0,8}\b(?:timer|timers|alarm|alarms|reminder|reminders)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DirectCommunicationDraft = new(
        @"\b(?:draft|compose|write|prepare|send)\b.{0,120}\b(?:e-?mail|gmail)\b|\b(?:e-?mail|gmail)\b.{0,120}\b(?:draft|compose|write|prepare|send)\b|\b(?:prepare|send)\b.{0,120}\b(?:whatsapp\s+)?message\b.{0,80}\bon\s+whatsapp\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DirectCurrentControl = new(
        @"^\s*(?:please\s+)?(?:(?:continue|resume)\s+)?(?:click|press|choose|select|unselect|check|uncheck|fill(?:ing)?|type|enter|paste|write|insert|place|put|scroll|dismiss|close\s+(?:the\s+)?(?:popup|banner|modal))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DirectInteractionRequest = new(
        @"^\s*(?:please\s+)?(?:(?:(?:can|could|would|will)\s+you|i\s+(?:need|want|would\s+like)\s+you\s+to|let(?:'s|\s+us))\s+(?:please\s+)?)?(?:open|go\s+to|navigate\s+to|visit|search|find|look\s+for|locate|click|press|choose|select|unselect|check|uncheck|fill|answer|solve|type|enter|paste|copy|read|inspect|summari[sz]e|scroll|dismiss|reload|refresh|send|post|publish|delete|add|play|upload|download|compare|show)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A page-related noun or an interactive-sounding verb inside an idea is
    // not itself an instruction to operate the browser. This is intentionally
    // narrow: clear imperative/polite action requests still win below.
    private static readonly Regex ConversationalProposal = new(
        @"\b(?:brainstorm(?:ing)?|idea|concept|feature|proposal|what\s+do\s+you\s+think)\b|\b(?:maybe|perhaps)\b|\bwe\s+(?:could|might|should|would)\b|\b(?:users?|people)\s+(?:can|could|might|would)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PageInteraction = new(
        @"\b(click|press|choose|select|unselect|check|uncheck|fill(?:ing)?|answer(?:ing)?|solv(?:e|ing)|type|enter|paste|copy|summari[sz]e|extract|report|read|inspect|scroll|dismiss|send|post|publish|delete|what is on|close .{0,40}\b(?:popup|banner|modal)|add .{0,80}\bto (?:my |the )?cart|open the (?:first|second|third|oldest|latest|newest|highest|lowest|best)|play the (?:oldest|latest|newest|most recent)|upload|download)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PageSectionNavigation = new(
        @"\b(?:open|go\s+to|navigate\s+to|visit|show)\s+(?:the\s+)?[^.!?]{1,80}\b(?:tab|section|panel|menu|area)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CurrentPageSearch = new(
        @"\b(?:search|find|look\s+for|locate)\s+(?:(?:on|in|within)\s+)?(?:this|the\s+current|the\s+active)\s+(?:page|site|website|tab)\b|\b(?:search|find|look\s+for|locate)\b.{0,100}\b(?:on|in|within)\s+(?:this|the\s+current|the\s+active)\s+(?:page|site|website|tab)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DestinationThenInteraction = new(
        @"\b(?:open|go\s+to|navigate\s+to|visit)\b.{1,140}\b(?:and|then)\b.{0,80}\b(?:search|find|look\s+for|locate|click|press|choose|select|fill|read|inspect|open)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DocumentMutation = new(
        @"\b(?:write|insert|place|put)\b.{0,100}\b(?:text|content|sentence|draft|summary|cursor|editor|document|doc|field|box)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SimpleOpenOrSearch = new(
        @"^\s*(?:please\s+)?(?:open|launch|go to|visit)\s+(?:https?://\S+|[\w.-]+(?:\.[a-z]{2,})?)(?:\s+in\s+\w+)?\s*[.!?]?\s*$|^\s*(?:please\s+)?search\s+(?:google|youtube|github|amazon|the web)?\s*(?:for\s+)?\S.+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WebContext = new(
        @"\b(chrome|browser|website|webpage|page|tab|form|quiz|questionnaire|assessment|survey|youtube|amazon|google|github|wikipedia|instagram|gmail|docs|devpost|https?://)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FormOrAssessmentTarget = new(
        @"\b(?:form|quiz|questionnaire|assessment|survey)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CartInteraction = new(
        @"\badd\b.+\bcart\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ComparativeResearch = new(
        @"\b(?:find|compare|identify|choose|show)\b.{0,180}\b(?:cheapest|lowest|highest|best|earliest|latest|shortest|fastest|largest|smallest|top|second[- ]largest)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LiveResearchContext = new(
        @"\b(?:flight|fare|hotel|ticket|product|price|priced|deal|listing|restaurant|store|shop|result)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RankedBrowserInteraction = new(
        @"\b(?:play|open|find|choose|select)\b.{0,160}\b(?:oldest|latest|newest|most recent|first|last|highest[- ]rated|lowest[- ]priced)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnambiguouslyInteractive = new(
        @"\b(click|press|choose|select|unselect|check|uncheck|fill(?:ing)?|answer(?:ing)?|solv(?:e|ing)|type|enter|paste|scroll|dismiss|send|post|publish|delete|close .{0,40}\b(?:popup|banner|modal)|add .{0,80}\bto (?:my |the )?cart|play the (?:oldest|latest|newest|most recent)|upload)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // These requests commonly refer to text the user has selected, including
    // text selected inside a webpage or a form. They are deliberately handled
    // by normal Jarvis first so it can capture and answer the selection. A
    // page-changing verb or an explicit automation request still wins.
    private static readonly Regex SelectedContentQuestion = new(
        @"\b(?:answer|solve|explain|read|review|inspect|interpret|summar(?:y|i[sz]e)|translate|analyse|analyze|define|meaning|mean)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitSelectedContext = new(
        @"\b(?:selected|highlighted)\b|\bselection\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitPageMutation = new(
        @"\b(click|press|choose|select|unselect|check|uncheck|fill(?:ing)?|type|enter(?:ing)?|paste|copy|write|insert|place|put|upload|download|send|post|publish|delete|dismiss|close\s+(?:the\s+)?(?:popup|banner|modal))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitSubmit = new(
        @"\bsubmit\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SubmitBlockedByUser = new(
        @"\b(?:do\s+not|don't|dont|without|before)\s+submit\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public BrowserClassification Classify(string request)
    {
        var text = (request ?? "").Trim();
        if (text.Length == 0)
            return Result(BrowserAutomationDecision.SimpleTool, "No browser request was provided.");

        if (LocalTimerAction.IsMatch(text))
        {
            return Result(
                BrowserAutomationDecision.SimpleTool,
                "Timers, alarms, and reminders are handled by normal Jarvis rather than webpage automation.");
        }

        if (IsOutcomeDiagnosticQuestion(text))
        {
            return Result(
                BrowserAutomationDecision.SimpleTool,
                "A retrospective task question is read-only diagnostic context, not permission to start browser automation.");
        }

        var explicitAutomationRequested = HasExplicitAutomationRequest(text);
        if (DirectCommunicationDraft.IsMatch(text)
            && !explicitAutomationRequested
            && !CurrentContext.IsMatch(text))
        {
            return Result(
                BrowserAutomationDecision.SimpleTool,
                "A deterministic draft/message capability should prepare this for review without starting browser automation.");
        }

        if (JarvisAutomationService.IsDirectBrowserShortcutRequest(text))
        {
            return Result(
                BrowserAutomationDecision.SimpleTool,
                "An exact browser-tab shortcut is sufficient.");
        }

        var current = CurrentContext.IsMatch(text)
            || DirectCurrentControl.IsMatch(text);
        var interaction = PageInteraction.IsMatch(text)
            || CartInteraction.IsMatch(text)
            || DocumentMutation.IsMatch(text)
            || ComparativeResearch.IsMatch(text)
            || RankedBrowserInteraction.IsMatch(text)
            || PageSectionNavigation.IsMatch(text)
            || CurrentPageSearch.IsMatch(text)
            || DestinationThenInteraction.IsMatch(text);
        if (IsDeferredInteractionRequest(text))
        {
            return Result(
                BrowserAutomationDecision.SimpleTool,
                "The user described a future browser action that depends on a later user-controlled transition; retain current context and wait rather than acting now.",
                current,
                prefersSelectedContent: ReferencesCapturedUserContext(text));
        }
        if (explicitAutomationRequested)
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The user explicitly requested browser automation.",
                current);

        // A suppressed automation mention is an explicit user constraint.
        // Interaction words elsewhere (including prohibited actions) cannot
        // turn that opt-out back into permission to start browser control.
        if (ExplicitAutomation.IsMatch(text))
        {
            return Result(
                BrowserAutomationDecision.SimpleTool,
                "The browser-automation mention is negated or is a control request; use a direct capability or explain its limits rather than starting browser automation.",
                current);
        }

        var contentQuestion = SelectedContentQuestion.IsMatch(text);
        var selectedContext = ExplicitSelectedContext.IsMatch(text);
        var explicitMutation = HasExplicitPageMutation(text);
        var formOrAssessment = FormOrAssessmentTarget.IsMatch(text);
        var directActionRequest = DirectCurrentControl.IsMatch(text)
            || DirectInteractionRequest.IsMatch(text);

        if (contentQuestion
            && !explicitMutation
            && selectedContext)
        {
            return Result(
                BrowserAutomationDecision.AskUser,
                "The user explicitly referred to selected content. Answer that selection normally and do not start page automation.",
                current,
                prefersSelectedContent: true);
        }

        if (contentQuestion
            && !explicitMutation
            && !current
            && !formOrAssessment)
        {
            return Result(
                BrowserAutomationDecision.AskUser,
                "The request could mean answering selected content or acting on a live page, so ask one concise clarification.",
                current,
                prefersSelectedContent: true);
        }

        if (ConversationalProposal.IsMatch(text)
            && !directActionRequest)
        {
            return Result(
                BrowserAutomationDecision.SimpleTool,
                "The turn is discussion or brainstorming, not a request to interact with the webpage.",
                current,
                prefersSelectedContent: selectedContext);
        }

        if (current
            && interaction
            && (directActionRequest
                || explicitMutation
                || Regex.IsMatch(text, @"\bwhat\s+is\s+on\b", RegexOptions.IgnoreCase)))
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The outcome depends on the live state of the current page.",
                true);

        if (formOrAssessment && interaction)
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The request asks Jarvis to interact with a form or assessment rather than only answer selected content.",
                current);

        if (ComparativeResearch.IsMatch(text)
            && (current
                || WebContext.IsMatch(text)
                || LiveResearchContext.IsMatch(text)))
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The request requires comparing live results and selecting an outcome rather than opening a direct search URL.",
                current);

        if (RankedBrowserInteraction.IsMatch(text) && WebContext.IsMatch(text))
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The request requires navigating live webpage state to choose an ordered result.",
                current);

        if ((PageSectionNavigation.IsMatch(text)
                || DestinationThenInteraction.IsMatch(text))
            && directActionRequest)
        {
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The request explicitly asks for multi-step page navigation or a live page section.",
                current);
        }

        if (interaction
            && UnambiguouslyInteractive.IsMatch(text)
            && directActionRequest)
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The request requires a grounded interaction with a live webpage control.",
                current);

        if (interaction
            && WebContext.IsMatch(text)
            && directActionRequest)
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The request requires reading or changing live webpage state.",
                current);

        if (SimpleOpenOrSearch.IsMatch(text) && !interaction)
            return Result(
                BrowserAutomationDecision.SimpleTool,
                "A direct URL, search URL, or existing lightweight browser command is sufficient.",
                current);

        if (WebContext.IsMatch(text) && Regex.IsMatch(
            text,
            @"\b(do|complete|handle|work on|continue|use)\b",
            RegexOptions.IgnoreCase))
        {
            return Result(
                BrowserAutomationDecision.AskUser,
                "The request mentions a website but does not make the intended page interaction clear.",
                current);
        }

        return Result(
            BrowserAutomationDecision.SimpleTool,
            "No live webpage interaction is required.",
            current);
    }

    private static BrowserClassification Result(
        BrowserAutomationDecision decision,
        string reason,
        bool current = false,
        bool prefersSelectedContent = false) => new()
    {
        Decision = decision,
        Reason = reason,
        RefersToCurrentPage = current,
        PrefersSelectedContent = prefersSelectedContent,
    };

    private static bool HasExplicitPageMutation(string text) =>
        ExplicitPageMutation.IsMatch(text)
        || (ExplicitSubmit.IsMatch(text) && !SubmitBlockedByUser.IsMatch(text));

    internal static bool RequiresPageMutation(string text) =>
        HasExplicitPageMutation(text)
        || (FormOrAssessmentTarget.IsMatch(text)
            && SelectedContentQuestion.IsMatch(text));

    internal static bool IsDeferredInteractionRequest(string text) =>
        DeferredUserControlledInteraction.IsMatch(text ?? "")
        && (HasExplicitAutomationRequest(text)
            || HasExplicitPageMutation(text ?? "")
            || FormOrAssessmentTarget.IsMatch(text ?? ""));

    internal static bool ReferencesCapturedUserContext(string text) =>
        ExplicitSelectedContext.IsMatch(text ?? "")
        ||
        Regex.IsMatch(
            text ?? "",
            @"\b(?:copied|clipboard)\b.{0,80}\b(?:text|content|context|details|information|data|answers?)\b"
            + @"|\b(?:use|from|with|based\s+on)\b.{0,60}\b(?:the|that|my|previous|provided|supplied|captured)\s+(?:selection|selected\s+text|clipboard|context|details|information|data)\b",
            RegexOptions.IgnoreCase);

    internal static bool ReferencesSelectedUserContext(string text) =>
        ExplicitSelectedContext.IsMatch(text ?? "");

    internal static bool RequiresFreshUserContextCapture(string text) =>
        ReferencesCapturedUserContext(text)
        && (RequiresPageMutation(text)
            || HasExplicitAutomationRequest(text)
            || IsDeferredInteractionRequest(text));

    internal static string FreshContextCaptureTool(string text)
    {
        text ??= "";
        if (!RequiresFreshUserContextCapture(text))
            return "";
        if (ExplicitSelectedContext.IsMatch(text))
            return "get_selected_text";
        if (Regex.IsMatch(text, @"\b(?:copied|clipboard)\b", RegexOptions.IgnoreCase))
            return "get_clipboard_text";
        return "";
    }

    // Live input transcription is incremental. Until an answer/explain-style
    // utterance is complete, it may still resolve to selected-text context.
    // This affects only eager voice pre-routing; the completed turn can still
    // choose Browser Automation through the normal classifier/tool path.
    internal static bool ShouldDeferIncrementalContentRouting(string text) =>
        SelectedContentQuestion.IsMatch(text ?? "")
        && !HasExplicitAutomationRequest(text)
        && !HasExplicitPageMutation(text ?? "");

    internal static bool HasExplicitAutomationRequest(string text)
    {
        text ??= "";
        if (IsOutcomeDiagnosticQuestion(text))
            return false;
        foreach (Match match in ExplicitAutomation.Matches(text))
        {
            var beforeStart = Math.Max(0, match.Index - 48);
            var before = text[beforeStart..match.Index];
            var afterEnd = Math.Min(text.Length, match.Index + match.Length + 32);
            var after = text[(match.Index + match.Length)..afterEnd];
            if (ExplicitAutomationSuppressionBefore.IsMatch(before)
                || ExplicitAutomationSuppressionAfter.IsMatch(after))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    internal static bool IsOutcomeDiagnosticQuestion(string text)
    {
        var value = (text ?? "").Trim();
        return OutcomeDiagnosticQuestion.IsMatch(value)
            && !OutcomeDiagnosticFollowUpAction.IsMatch(value);
    }
}
