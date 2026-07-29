using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserAutomationVerifierTests
{
    private readonly BrowserAutomationVerifier _verifier = new();

    [Fact]
    public void Typing_requires_live_value_match()
    {
        var action = new BrowserPlannerAction { Type = "type", Text = "hello" };
        var failed = _verifier.VerifyAction(
            action,
            new BrowserActionReceipt
            {
                Ok = true,
                Action = "type",
                ValueMatches = false,
                NoVisibleChange = true,
            });
        var passed = _verifier.VerifyAction(
            action,
            new BrowserActionReceipt { Ok = true, Action = "type", ValueMatches = true });

        Assert.False(failed.Verified);
        Assert.False(failed.Partial);
        Assert.True(passed.Verified);
    }

    [Fact]
    public void Autocomplete_reaction_is_intermediate_progress_not_verified_success_or_a_hard_noop()
    {
        var result = _verifier.VerifyAction(
            new BrowserPlannerAction { Type = "type", Text = "Switzerland" },
            new BrowserActionReceipt
            {
                Ok = true,
                Action = "type",
                ValueMatches = false,
                NoVisibleChange = false,
            });

        Assert.False(result.Verified);
        Assert.True(result.Partial);
        Assert.Contains("not committed", result.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Click_noop_is_not_success()
    {
        var result = _verifier.VerifyAction(
            new BrowserPlannerAction { Type = "click" },
            new BrowserActionReceipt
            {
                Ok = true,
                Action = "click",
                NoVisibleChange = true,
            });

        Assert.False(result.Verified);
    }

    [Fact]
    public void Navigation_to_a_less_specific_same_site_state_is_not_progress()
    {
        var result = _verifier.VerifyAction(
            new BrowserPlannerAction { Type = "navigate" },
            new BrowserActionReceipt
            {
                Ok = true,
                Action = "navigate",
                Navigation = true,
                BeforeUrl = "https://example.test/search?origin=A&destination=B",
                AfterUrl = "https://example.test/search",
            });

        Assert.False(result.Verified);
        Assert.Contains("less specific", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void YouTube_completion_requires_real_watch_url()
    {
        var decision = new BrowserPlannerDecision
        {
            Done = true,
            ResultSummary = "Opened the oldest video.",
        };
        var failed = _verifier.VerifyTerminal(
            "Play the oldest NASA video on YouTube",
            decision,
            new BrowserPageSnapshot { Url = "https://www.youtube.com/@NASA/videos" },
            Array.Empty<BrowserActionReceipt>());
        var passed = _verifier.VerifyTerminal(
            "Play the oldest NASA video on YouTube",
            decision,
            new BrowserPageSnapshot
            {
                Url = "https://www.youtube.com/watch?v=abc",
                Title = "NASA — First upload",
                PageText = "NASA",
            },
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "click",
                    TargetName = "Oldest",
                    SelectedChanged = true,
                },
            });

        Assert.False(failed.Verified);
        Assert.True(passed.Verified);
    }

    [Fact]
    public void YouTube_oldest_rejects_a_watch_page_without_oldest_evidence()
    {
        var result = _verifier.VerifyTerminal(
            "Play the oldest NASA video on YouTube",
            new BrowserPlannerDecision { Done = true, ResultSummary = "Done" },
            new BrowserPageSnapshot
            {
                Url = "https://www.youtube.com/watch?v=abc",
                Title = "NASA",
                PageText = "NASA",
            },
            Array.Empty<BrowserActionReceipt>());

        Assert.False(result.Verified);
        Assert.Contains("Oldest", result.Error);
    }

    [Fact]
    public void Planner_done_without_evidence_is_rejected()
    {
        var result = _verifier.VerifyTerminal(
            "Click the requested control",
            new BrowserPlannerDecision { Done = true, ResultSummary = "Done" },
            new BrowserPageSnapshot(),
            Array.Empty<BrowserActionReceipt>());

        Assert.False(result.Verified);
    }

    [Fact]
    public void Form_completion_rejects_dropdown_opening_without_a_selected_option()
    {
        var result = _verifier.VerifyTerminal(
            "Fill this quiz and do not submit",
            new BrowserPlannerDecision { Done = true, ResultSummary = "Done" },
            new BrowserPageSnapshot { PageText = "Practice quiz" },
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "click",
                    TargetRole = "option",
                    TargetName = "Choose",
                    NoVisibleChange = false,
                },
            });

        Assert.False(result.Verified);
    }

    [Fact]
    public void Form_completion_accepts_concrete_checked_and_selected_state_changes()
    {
        var result = _verifier.VerifyTerminal(
            "Fill this quiz and do not submit",
            new BrowserPlannerDecision { Done = true, ResultSummary = "Done" },
            new BrowserPageSnapshot { PageText = "Practice quiz" },
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "click",
                    TargetRole = "radio",
                    TargetName = "Answer A",
                    CheckedChanged = true,
                },
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "click",
                    TargetRole = "option",
                    TargetName = "Answer B",
                    SelectedChanged = true,
                },
            });

        Assert.True(result.Verified);
    }

    [Fact]
    public void Report_request_requires_a_grounded_live_result()
    {
        var decision = new BrowserPlannerDecision
        {
            Done = true,
            ResultSummary = "The repository shows visible project metrics.",
        };
        var passed = _verifier.VerifyTerminal(
            "Open GitHub and report visible stars and forks",
            decision,
            new BrowserPageSnapshot
            {
                Url = "https://github.com/example/project",
                PageText = "Project page with repository metrics",
            },
            Array.Empty<BrowserActionReceipt>());
        var failed = _verifier.VerifyTerminal(
            "Open GitHub and report visible stars and forks",
            new BrowserPlannerDecision { Done = true },
            new BrowserPageSnapshot
            {
                Url = "https://github.com/example/project",
                PageText = "Project page",
            },
            Array.Empty<BrowserActionReceipt>());

        Assert.True(passed.Verified);
        Assert.False(failed.Verified);
    }

    [Fact]
    public void Summary_to_editor_requires_verified_destination_write()
    {
        const string request = "Summarize this page and paste the result into the Draft editor.";
        var decision = new BrowserPlannerDecision
        {
            Done = true,
            ResultSummary = "A grounded fixture summary.",
        };
        var page = new BrowserPageSnapshot
        {
            TabId = 12,
            Url = "https://example.test/editor",
            PageText = "Safe fixture article",
            ActiveEditable = true,
            ActiveEditorId = "draft",
        };

        var missingPaste = _verifier.VerifyTerminal(
            request,
            decision,
            page,
            Array.Empty<BrowserActionReceipt>());
        var pasted = _verifier.VerifyTerminal(
            request,
            decision,
            page,
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "paste",
                    TabId = 12,
                    TargetId = "draft",
                    TargetRole = "textbox",
                    TargetName = "Draft editor",
                    AfterUrl = "https://example.test/editor",
                    ValueMatches = true,
                },
            });

        Assert.False(missingPaste.Verified);
        Assert.Contains("destination editor", missingPaste.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(pasted.Verified);
    }

    [Fact]
    public void Natural_place_wording_still_requires_verified_destination_write()
    {
        const string request = "In this document, place the exact text at the current cursor.";
        var decision = new BrowserPlannerDecision
        {
            Done = true,
            ResultSummary = "Done",
        };
        var page = new BrowserPageSnapshot
        {
            TabId = 8,
            PageText = "Some page text",
            ActiveEditable = true,
            ActiveEditorId = "document-content",
        };

        var missing = _verifier.VerifyTerminal(
            request,
            decision,
            page,
            Array.Empty<BrowserActionReceipt>());
        var verified = _verifier.VerifyTerminal(
            request,
            decision,
            page,
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "paste",
                    TabId = 8,
                    TargetId = "document-content",
                    TargetRole = "textbox",
                    TargetName = "Document content",
                    ValueMatches = true,
                },
            });

        Assert.False(missing.Verified);
        Assert.True(verified.Verified);
    }

    [Fact]
    public void Source_search_field_paste_cannot_prove_google_docs_transfer()
    {
        const string request =
            "Open Wikipedia, summarize Alan Turing, and paste the summary into Google Docs.";
        var result = _verifier.VerifyTerminal(
            request,
            new BrowserPlannerDecision
            {
                Done = true,
                ResultSummary = "Initiating search for Alan Turing on Wikipedia.",
            },
            new BrowserPageSnapshot
            {
                TabId = 21,
                Url = "https://www.wikipedia.org/",
                PageText = "Wikipedia search",
                ActiveEditable = true,
                ActiveEditorId = "search",
            },
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "paste",
                    TabId = 21,
                    TargetId = "search",
                    TargetRole = "textbox",
                    TargetName = "Search Wikipedia",
                    AfterUrl = "https://www.wikipedia.org/",
                    ValueMatches = true,
                },
            });

        Assert.False(result.Verified);
        Assert.Contains("destination editor", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
