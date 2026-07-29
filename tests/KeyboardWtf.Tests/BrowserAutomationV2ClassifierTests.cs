using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserAutomationV2ClassifierTests
{
    private readonly BrowserAutomationTaskClassifier _classifier = new();

    [Theory]
    [InlineData("Open Amazon")]
    [InlineData("Search YouTube for MrBeast")]
    [InlineData("Search Amazon for MX Master 4")]
    [InlineData("Open https://example.com")]
    [InlineData("Open YouTube")]
    [InlineData("Switch to Chrome's fourth tab using Ctrl+4")]
    [InlineData("Go to the third browser tab")]
    [InlineData("Switch to the last tab")]
    [InlineData("Go to the second last tab")]
    [InlineData("Switch to the second-to-last tab")]
    public void Simple_open_and_search_commands_stay_on_fast_tools(string request)
    {
        var result = _classifier.Classify(request);
        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
    }

    [Theory]
    [InlineData("Summarize this page and copy it to clipboard")]
    [InlineData("Fill this Google Form")]
    [InlineData("Click Oldest on this page")]
    [InlineData("Add MX Master 4 to my Amazon cart")]
    [InlineData("Play the oldest NASA video on YouTube")]
    [InlineData("Open Wikipedia, summarize Albert Einstein, and paste it into Google Docs")]
    [InlineData("Send Yaksh a message on Instagram")]
    [InlineData("Reload this page, then close the harmless popup")]
    [InlineData("Answer only the first unanswered image-based question on this page")]
    [InlineData("Solve the quiz but do not submit it")]
    [InlineData("Open GitHub, find browser-use/browser-use, and report visible stars and forks")]
    [InlineData("Find the cheapest round-trip flights from Mumbai to Switzerland in September 2026 for one week")]
    [InlineData("Compare the best three options and open the lowest-priced result")]
    public void Live_page_interactions_use_browser_automation(string request)
    {
        var result = _classifier.Classify(request);
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Theory]
    [InlineData("Click Oldest.")]
    [InlineData("Fill the fields.")]
    [InlineData("Continue filling the answerable fields from the live page state.")]
    [InlineData("Resume answering this quiz without submitting it.")]
    [InlineData("Send Yaksh Dharod a message saying hi on Instagram web.")]
    public void Unambiguous_live_interactions_do_not_require_a_site_qualifier(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Fact]
    public void Current_page_language_is_preserved()
    {
        var result = _classifier.Classify("What is on this page?");
        Assert.True(result.RefersToCurrentPage);
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Fact]
    public void This_document_is_grounded_to_the_active_tab()
    {
        var result = _classifier.Classify("In this blank Google Doc, paste a draft at the active cursor.");

        Assert.True(result.RefersToCurrentPage);
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Theory]
    [InlineData("In this blank Google Doc, place the exact harmless text at the current cursor.")]
    [InlineData("Write this sentence into the active document.")]
    [InlineData("Insert the summary at the current cursor.")]
    [InlineData("Put this draft into the editor.")]
    public void Natural_document_mutation_phrasing_uses_the_active_tab(string request)
    {
        var result = _classifier.Classify(request);

        Assert.True(result.RefersToCurrentPage);
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Fact]
    public void Ordinary_writing_request_does_not_start_browser_automation()
    {
        var result = _classifier.Classify("Write me a short poem about the moon.");

        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
    }

    [Theory]
    [InlineData("browser_automation")]
    [InlineData("browser_automation_status")]
    [InlineData("browser_automation_control")]
    public void Pre_routed_turn_allows_only_browser_lifecycle_tools(string toolName)
    {
        Assert.True(CommandRegistry.IsBrowserTaskLifecycleTool(toolName));
    }

    [Theory]
    [InlineData("type_text")]
    [InlineData("replace_selected_text")]
    [InlineData("press_key")]
    [InlineData("open_url")]
    [InlineData("browser_action")]
    public void Pre_routed_turn_rejects_competing_active_window_tools(string toolName)
    {
        Assert.False(CommandRegistry.IsBrowserTaskLifecycleTool(toolName));
    }

    [Fact]
    public void Direct_control_command_is_grounded_to_the_active_page()
    {
        var result = _classifier.Classify("Click Oldest.");

        Assert.True(result.RefersToCurrentPage);
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Fact]
    public void Browser_tool_uses_the_complete_raw_user_turn_instead_of_a_lossy_model_summary()
    {
        const string user =
            "Find the cheapest round-trip flights from Mumbai to Switzerland in September 2026 for one week.";

        Assert.Equal(
            user,
            CommandRegistry.ResolveBrowserAutomationTask("Browser automation", user));
    }

    [Theory]
    [InlineData("Do that.")]
    [InlineData("Yes, please proceed.")]
    [InlineData("Try again.")]
    public void Browser_tool_keeps_model_resolved_context_for_reference_only_follow_ups(
        string userTurn)
    {
        const string resolved =
            "Fill the currently open form with the details already provided, but do not submit it.";

        Assert.Equal(
            resolved,
            CommandRegistry.ResolveBrowserAutomationTask(resolved, userTurn));
    }

    [Fact]
    public void Vague_web_work_asks_once()
    {
        var result = _classifier.Classify("Do something with this website");
        Assert.Equal(BrowserAutomationDecision.AskUser, result.Decision);
    }
}
