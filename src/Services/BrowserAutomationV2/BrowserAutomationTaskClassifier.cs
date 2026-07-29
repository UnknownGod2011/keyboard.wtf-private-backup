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

    private static readonly Regex DirectCurrentControl = new(
        @"^\s*(?:please\s+)?(?:(?:continue|resume)\s+)?(?:click|press|choose|select|unselect|check|uncheck|fill(?:ing)?|answer(?:ing)?|solv(?:e|ing)|type|enter|paste|write|insert|place|put|scroll|dismiss|close\s+(?:the\s+)?(?:popup|banner|modal))\b",
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
        @"\b(chrome|browser|website|webpage|page|tab|form|youtube|amazon|google|github|wikipedia|instagram|gmail|docs|devpost|https?://)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CartInteraction = new(
        @"\badd\b.+\bcart\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ComparativeResearch = new(
        @"\b(?:find|compare|identify|choose|show)\b.{0,180}\b(?:cheapest|lowest|highest|best|earliest|latest|shortest|fastest|largest|smallest|top|second[- ]largest)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UnambiguouslyInteractive = new(
        @"\b(click|press|choose|select|unselect|check|uncheck|fill(?:ing)?|answer(?:ing)?|solv(?:e|ing)|type|enter|paste|scroll|dismiss|send|post|publish|delete|close .{0,40}\b(?:popup|banner|modal)|add .{0,80}\bto (?:my |the )?cart|play the (?:oldest|latest|newest|most recent)|upload)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public BrowserClassification Classify(string request)
    {
        var text = (request ?? "").Trim();
        if (text.Length == 0)
            return Result(BrowserAutomationDecision.SimpleTool, "No browser request was provided.");

        var current = CurrentContext.IsMatch(text)
            || DirectCurrentControl.IsMatch(text);
        var interaction = PageInteraction.IsMatch(text)
            || CartInteraction.IsMatch(text)
            || DocumentMutation.IsMatch(text)
            || ComparativeResearch.IsMatch(text);
        if (ExplicitAutomation.IsMatch(text))
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The user explicitly requested browser automation.",
                current);

        if (current && interaction)
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The outcome depends on the live state of the current page.",
                true);

        if (ComparativeResearch.IsMatch(text))
            return Result(
                BrowserAutomationDecision.BrowserAutomation,
                "The request requires comparing live results and selecting an outcome rather than opening a direct search URL.",
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
        bool current = false) => new()
    {
        Decision = decision,
        Reason = reason,
        RefersToCurrentPage = current,
    };
}
