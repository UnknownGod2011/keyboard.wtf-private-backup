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

    private static readonly Regex LocalTimerAction = new(
        @"^\s*(?:please\s+)?(?:(?:set|start|create|make|cancel|stop|pause|resume|list|show|check)\s+)?(?:an?\s+|the\s+|my\s+|active\s+|all\s+)?(?:\w+\s+){0,8}\b(?:timer|timers|alarm|alarms|reminder|reminders)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DirectCurrentControl = new(
        @"^\s*(?:please\s+)?(?:(?:continue|resume)\s+)?(?:click|press|choose|select|unselect|check|uncheck|fill(?:ing)?|type|enter|paste|write|insert|place|put|scroll|dismiss|close\s+(?:the\s+)?(?:popup|banner|modal))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PageInteraction = new(
        @"\b(click|press|choose|select|unselect|check|uncheck|fill(?:ing)?|answer(?:ing)?|solv(?:e|ing)|type|enter|paste|copy|summari[sz]e|extract|report|read|inspect|scroll|dismiss|send|post|publish|delete|what is on|close .{0,40}\b(?:popup|banner|modal)|add .{0,80}\bto (?:my |the )?cart|open the (?:first|second|third|oldest|latest|newest|highest|lowest|best)|play the (?:oldest|latest|newest|most recent)|upload|download)\b",
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
        @"\b(?:answer|solve|explain|summar(?:y|i[sz]e)|translate|analyse|analyze|define|meaning|mean)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitSelectedContext = new(
        @"\b(?:selected|highlighted)\b|\bselection\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitPageMutation = new(
        @"\b(click|press|choose|select|unselect|check|uncheck|fill(?:ing)?|type|enter|paste|copy|write|insert|place|put|upload|download|send|post|publish|delete|dismiss|close\s+(?:the\s+)?(?:popup|banner|modal))\b",
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

        var current = CurrentContext.IsMatch(text)
            || DirectCurrentControl.IsMatch(text);
        var interaction = PageInteraction.IsMatch(text)
            || CartInteraction.IsMatch(text)
            || DocumentMutation.IsMatch(text)
            || ComparativeResearch.IsMatch(text)
            || RankedBrowserInteraction.IsMatch(text);
        if (ExplicitAutomation.IsMatch(text))
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The user explicitly requested browser automation.",
                current);

        var contentQuestion = SelectedContentQuestion.IsMatch(text);
        var selectedContext = ExplicitSelectedContext.IsMatch(text);
        var explicitMutation = HasExplicitPageMutation(text);
        var formOrAssessment = FormOrAssessmentTarget.IsMatch(text);

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

        if (current && interaction)
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The outcome depends on the live state of the current page.",
                true);

        if (formOrAssessment && interaction)
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The request asks Jarvis to interact with a form or assessment rather than only answer selected content.",
                current);

        if (ComparativeResearch.IsMatch(text))
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The request requires comparing live results and selecting an outcome rather than opening a direct search URL.",
                current);

        if (RankedBrowserInteraction.IsMatch(text) && WebContext.IsMatch(text))
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The request requires navigating live webpage state to choose an ordered result.",
                current);

        if (interaction && UnambiguouslyInteractive.IsMatch(text))
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The request requires a grounded interaction with a live webpage control.",
                current);

        if (interaction && WebContext.IsMatch(text))
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
}
