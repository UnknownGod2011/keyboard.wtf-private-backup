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
    [InlineData("Set a ten second timer called cancellation check.")]
    [InlineData("Cancel the active timer.")]
    [InlineData("Show my active timers.")]
    [InlineData("Open Gmail, Discord, Devpost, Instagram, and Excel.")]
    [InlineData("Whenever I say potato patato, open Gmail, Discord, Devpost, Instagram, and Excel. Remember this.")]
    [InlineData("Open Instagram and Discord.")]
    [InlineData("Open Gmail.")]
    [InlineData("Open Devpost.")]
    [InlineData("Always open Discord on the web instead.")]
    [InlineData("Add Instagram to my duck duck setup.")]
    [InlineData("Remove Devpost from that workflow.")]
    [InlineData("Change Gmail to Google Sheets in that workflow.")]
    public void Simple_open_and_search_commands_stay_on_fast_tools(string request)
    {
        var result = _classifier.Classify(request);
        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
    }

    [Theory]
    [InlineData("Open https://example.com normally in Chrome. Do not use Browser Automation.")]
    [InlineData("Open example.com without browser automation.")]
    [InlineData("Open https://example.com normally in Chrome, without Browser Automation. Do not enter or submit anything.")]
    [InlineData("Open https://example.org. Do not use browser automation or click any controls.")]
    [InlineData("Fill this page without browser automation.")]
    [InlineData("Pause browser automation before entering the next value.")]
    [InlineData("Cancel browser automation.")]
    public void Negated_or_controlled_automation_mentions_do_not_start_a_browser_task(
        string request)
    {
        var result = _classifier.Classify(request);

        Assert.False(BrowserAutomationTaskClassifier.HasExplicitAutomationRequest(request));
        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
    }

    [Theory]
    [InlineData("Use Browser Automation to fill this form.")]
    [InlineData("Automate the browser and click the application button.")]
    public void Affirmative_automation_mentions_remain_authoritative(string request)
    {
        Assert.True(BrowserAutomationTaskClassifier.HasExplicitAutomationRequest(request));
        Assert.Equal(
            BrowserAutomationDecision.BrowserAutomation,
            _classifier.Classify(request).Decision);
    }

    [Theory]
    [InlineData("Summarize this page and copy it to clipboard")]
    [InlineData("Fill this Google Form")]
    [InlineData("Click Oldest on this page")]
    [InlineData("Add MX Master 4 to my Amazon cart")]
    [InlineData("Play the oldest NASA video on YouTube")]
    [InlineData("Open Wikipedia, summarize Albert Einstein, and paste it into Google Docs")]
    [InlineData("Fill this quiz")]
    [InlineData("Could you please fill this quiz?")]
    [InlineData("Solve this quiz on the active tab")]
    [InlineData("Solve the quiz but do not submit it")]
    [InlineData("Answer only the first unanswered image-based question on this page")]
    [InlineData("Send Yaksh a message on Instagram")]
    [InlineData("Reload this page, then close the harmless popup")]
    [InlineData("Open GitHub, find browser-use/browser-use, and report visible stars and forks")]
    [InlineData("Find the cheapest round-trip flights from Mumbai to Switzerland in September 2026 for one week")]
    [InlineData("Compare the best three options and open the lowest-priced result")]
    [InlineData("Open YouTube and play latest video by NASA")]
    [InlineData("Can you play NASA's latest video on YouTube?")]
    [InlineData("Play latest YouTube video by MrBeast")]
    public void Live_page_interactions_use_browser_automation(string request)
    {
        var result = _classifier.Classify(request);
        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Theory]
    [InlineData("Maybe we could add a community area where users can post recipes.")]
    [InlineData("Let's brainstorm which of these product ideas would be best.")]
    [InlineData("What do you think about a discussion feature for this idea?")]
    [InlineData("The selected text suggests people could publish their own examples.")]
    public void Brainstorming_and_hypothetical_page_topics_stay_with_normal_jarvis(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
    }

    [Theory]
    [InlineData("Analyze this selected context; when I navigate to the form, use it to fill the fields.")]
    [InlineData("Keep this highlighted information. Once I tell you the page is ready, use browser automation to enter it.")]
    [InlineData("Wait for me to open the intended page, then fill it from the selected details.")]
    [InlineData("Analyze the selected feedback and keep it for this conversation. Wait for my separate instruction before filling the survey; I have not moved to the form yet.")]
    [InlineData("Review this highlighted application. Hold for our confirmation before entering anything on the page.")]
    [InlineData("Read my selection; wait for us to confirm the destination before using browser automation.")]
    public void Future_user_controlled_browser_work_waits_instead_of_starting_now(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
        Assert.True(BrowserAutomationTaskClassifier.IsDeferredInteractionRequest(request));
        Assert.True(result.PrefersSelectedContent);
    }

    [Theory]
    [InlineData("Use the selected details to fill this form.")]
    [InlineData("Fill this page from the information I copied to the clipboard.")]
    [InlineData("Use the selected feedback to fill this survey.")]
    [InlineData("Fill the current form from my selection.")]
    public void Current_selected_context_mutation_waits_for_fresh_capture_before_pre_routing(
        string request)
    {
        Assert.True(BrowserAutomationTaskClassifier.RequiresFreshUserContextCapture(request));
        Assert.True(CommandRegistry.ShouldDeferIncrementalBrowserPreRoute(
            request,
            fromLocalTextSubmission: false));
    }

    [Fact]
    public void Immediate_page_mutation_remains_actionable()
    {
        const string request = "Use the selected details to fill this form now.";

        Assert.False(BrowserAutomationTaskClassifier.IsDeferredInteractionRequest(request));
        Assert.Equal(
            BrowserAutomationDecision.BrowserAutomation,
            _classifier.Classify(request).Decision);
    }

    [Fact]
    public void Captured_context_is_preserved_verbatim_in_the_one_ephemeral_browser_task_contract()
    {
        const string context =
            "Project: Clear Finch\nTeam size: 3\nWebsite: https://clearfinch.example";

        var task = CommandRegistry.BuildBrowserTaskWithCapturedContext(
            "Fill the current form and skip unknown optional fields.",
            "selected text",
            context);

        Assert.Contains("AUTHORITATIVE USER-PROVIDED SELECTED TEXT", task, StringComparison.Ordinal);
        Assert.Contains(context, task, StringComparison.Ordinal);
        Assert.Contains("genuinely missing required information", task, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, true, false, true, true)]
    [InlineData(true, true, false, false, true, false)]
    [InlineData(true, false, false, true, true, true)]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(false, true, true, true, true, false)]
    public void Captured_context_requires_current_turn_ownership_or_explicit_future_authorization(
        bool fresh,
        bool referencesContext,
        bool sameTurn,
        bool authorizedForLater,
        bool pageMutation,
        bool expected)
    {
        Assert.Equal(
            expected,
            CommandRegistry.CanAttachCapturedBrowserContext(
                fresh,
                referencesContext,
                sameTurn,
                authorizedForLater,
                pageMutation));
    }

    [Theory]
    [InlineData("Open the discussion tab.")]
    [InlineData("Go to the community section.")]
    [InlineData("Search this website for the eligibility criteria.")]
    [InlineData("Go to this page and find the application link.")]
    public void Clear_page_navigation_requests_still_use_browser_automation(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Theory]
    [InlineData("Answer this")]
    [InlineData("Solve these questions")]
    [InlineData("Explain this")]
    [InlineData("I selected this text, answer that question")]
    [InlineData("Please solve the questions I have highlighted")]
    [InlineData("Answer the question from the selected text")]
    [InlineData("Can you explain the text I've selected?")]
    [InlineData("I selected something. Can you answer it?")]
    [InlineData("I selected a word. What does it mean?")]
    [InlineData("I highlighted a paragraph; give me a summary")]
    [InlineData("What is the meaning of the highlighted content?")]
    [InlineData("Read the currently selected text and tell me what it describes")]
    [InlineData("Review this highlighted passage and discuss its main idea")]
    [InlineData("Inspect the selected text and interpret it for me")]
    public void Selection_style_questions_do_not_preempt_normal_jarvis(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.AskUser, result.Decision);
        Assert.True(result.PrefersSelectedContent);
        Assert.Contains("selected content", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Fill this form")]
    [InlineData("Fill this page with the supplied answers")]
    [InlineData("Use browser automation to answer and fill this")]
    [InlineData("I selected these questions; solve and fill the quiz")]
    [InlineData("I selected a paragraph; paste it into this form")]
    public void Explicit_page_mutation_starts_browser_automation(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
    }

    [Theory]
    [InlineData("Answer the question")]
    [InlineData("Solve this Coursera quiz question")]
    [InlineData("Explain this question on the current page")]
    public void Incremental_answer_fragments_wait_for_possible_selected_context(string request) =>
        Assert.True(BrowserAutomationTaskClassifier.ShouldDeferIncrementalContentRouting(request));

    [Theory]
    [InlineData("Fill this form")]
    [InlineData("I selected these questions; solve and fill the quiz")]
    [InlineData("Use browser automation to answer this question")]
    [InlineData("Click the answer on this page")]
    public void Explicit_page_actions_are_not_deferred_as_selected_content(string request) =>
        Assert.False(BrowserAutomationTaskClassifier.ShouldDeferIncrementalContentRouting(request));

    [Fact]
    public void Only_incremental_voice_pre_route_defers_an_ambiguous_answer_request()
    {
        Assert.True(CommandRegistry.ShouldDeferIncrementalBrowserPreRoute(
            "Answer the question on this page",
            fromLocalTextSubmission: false));
        Assert.False(CommandRegistry.ShouldDeferIncrementalBrowserPreRoute(
            "Answer the question on this page",
            fromLocalTextSubmission: true));
        Assert.False(CommandRegistry.ShouldDeferIncrementalBrowserPreRoute(
            "Fill the answer into this page",
            fromLocalTextSubmission: false));
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
    [InlineData("Draft a Gmail to Yaksh saying I am sick.")]
    [InlineData("Write an email to Pat about tomorrow's report.")]
    [InlineData("Prepare a WhatsApp message for Alex saying I will be late on WhatsApp.")]
    public void Safe_direct_communication_drafts_do_not_start_browser_automation(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.SimpleTool, result.Decision);
        Assert.Contains("draft", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Delete all promotional emails from Gmail using browser automation.")]
    [InlineData("Read the newest email on this Gmail page.")]
    public void Live_inbox_work_still_uses_browser_automation(string request)
    {
        var result = _classifier.Classify(request);

        Assert.Equal(BrowserAutomationDecision.BrowserAutomation, result.Decision);
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

    [Theory]
    [InlineData("remember_routine")]
    [InlineData("edit_workflow")]
    [InlineData("remember_app_alias")]
    [InlineData("remember_link_alias")]
    [InlineData("forget_learned_mapping")]
    [InlineData("set_browser_preference")]
    public void Durable_local_mutations_are_idempotent_pre_route_operations(string toolName)
    {
        Assert.True(CommandRegistry.IsDurableMutationTool(toolName));
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

    [Theory]
    [InlineData("I'm on the correct page now; continue here.")]
    [InlineData("Use this current form and resume here.")]
    public void Browser_tool_keeps_resolved_deferred_task_for_an_explicit_current_page_handoff(
        string userTurn)
    {
        const string resolved =
            "Fill the current form from the selected context and skip unavailable optional fields, without submitting.";

        Assert.True(BrowserAutomationCoordinator.IsExplicitPageHandoffGuidance(userTurn));
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
