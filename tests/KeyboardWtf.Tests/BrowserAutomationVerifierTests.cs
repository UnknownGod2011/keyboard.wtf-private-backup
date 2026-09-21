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
    public void Native_input_receipt_does_not_override_missing_fresh_field_value()
    {
        var action = new BrowserPlannerAction
        {
            Type = "type",
            TargetId = "name",
            TargetRole = "textbox",
            TargetName = "Display name",
            Text = "Regression User",
            Replace = true,
        };
        var receipt = new BrowserActionReceipt
        {
            Ok = true,
            Action = "type",
            TargetId = "name",
            TargetRole = "textbox",
            TargetName = "Display name",
            ValueMatches = true,
            NativeInputUsed = true,
        };
        BrowserAutomationCoordinator.AttachExpectedState(receipt, action);

        var persisted = BrowserAutomationCoordinator.FreshStateConfirmsPersistedValue(
            new BrowserPageSnapshot
            {
                Controls =
                [
                    Field("name", "textbox", "Display name", ""),
                ],
            },
            action,
            receipt);

        Assert.Equal("Regression User", receipt.ExpectedValue);
        Assert.False(persisted);
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
                Controls =
                [
                    PublisherLink("NASA"),
                ],
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
            },
            orderedSourceCollectionVerified: true);

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
                Controls =
                [
                    PublisherLink("NASA"),
                ],
            },
            Array.Empty<BrowserActionReceipt>(),
            orderedSourceCollectionVerified: true);

        Assert.False(result.Verified);
        Assert.Contains("Oldest", result.Error);
    }

    [Theory]
    [InlineData("Open the latest video by Acme Studio on the media site.")]
    [InlineData("Open Acme Studio's newest upload on the media site.")]
    [InlineData("Play the most recent Acme Studio video.")]
    [InlineData("Open the oldest video by Acme Studio. Verify the publisher and oldest ordering. Stop once the video is open.")]
    [InlineData("Open the oldest video by Acme Studio. Verify the oldest ordering! Stop once the video is open.")]
    [InlineData("Open the oldest video published by Acme Studio. Verify the oldest ordering. Stop once the video is open.")]
    [InlineData("Open the newest article posted by Acme Studio. Stop when the article is open.")]
    public void Ordered_content_requests_extract_the_named_source(string request)
    {
        Assert.Equal(
            "acme studio",
            BrowserAutomationVerifier.ExtractOrderedSourceIdentity(request));
    }

    [Fact]
    public void Latest_explicit_source_correction_outranks_the_superseded_source()
    {
        var corrected = BrowserAutomationCoordinator.BuildCorrectedTaskRequest(
            "Open the latest video by Northwind Media.",
            "No, use the latest video by Contoso Labs instead.");

        Assert.Equal(
            "contoso labs",
            BrowserAutomationVerifier.ExtractOrderedSourceIdentity(corrected));
    }

    [Fact]
    public void Ordered_source_does_not_invent_a_publisher_from_later_instructions()
    {
        var request = "Use Browser Automation to open https://www.youtube.com/@NASA/videos and open the oldest video published by NASA. Verify the publisher and oldest ordering. Stop once the video is open; do not repeat navigation.";
        var source = BrowserAutomationVerifier.ExtractOrderedSourceIdentity(request);
        Assert.Equal("nasa", source);
    }

    [Theory]
    [InlineData("Check the oldest ordering. Stop when the video is open.")]
    [InlineData("Check the latest ordering. Stop when the article is open.")]
    public void Unnamed_source_is_not_synthesized_across_sentence_boundaries(string request)
    {
        Assert.Equal("", BrowserAutomationVerifier.ExtractOrderedSourceIdentity(request));
    }

    [Theory]
    [InlineData("Acme Studio", true, true)]
    [InlineData("Other Studio", true, false)]
    [InlineData("Acme Studio", false, false)]
    public void Published_source_still_requires_both_collection_and_final_publisher(
        string publisher, bool collectionVerified, bool expected)
    {
        var result = _verifier.VerifyTerminal(
            "Open the oldest video published by Acme Studio. Verify the oldest ordering. Stop once the video is open.",
            new BrowserPlannerDecision { Done = true, ResultSummary = "Opened the requested video." },
            new BrowserPageSnapshot
            {
                Url = "https://www.youtube.com/watch?v=example",
                Controls = [PublisherLink(publisher)],
            },
            [new BrowserActionReceipt { Ok = true, Action = "click", TargetName = "Oldest", SelectedChanged = true }],
            orderedSourceCollectionVerified: collectionVerified);
        Assert.Equal(expected, result.Verified);
    }

    [Fact]
    public void Ordered_source_collection_requires_exact_source_identity_and_collection_context()
    {
        var officialCollection = new BrowserPageSnapshot
        {
            Url = "https://media.example/@acme-studio/videos",
            Title = "Acme Studio",
            Controls =
            [
                PublisherLink("Acme Studio"),
                new BrowserControl
                {
                    Role = "tab",
                    AccessibleName = "Videos",
                    Visible = true,
                    Enabled = true,
                },
            ],
        };
        var searchResult = new BrowserPageSnapshot
        {
            Url = "https://media.example/search?q=acme-studio",
            Title = "Search results for Acme Studio",
            Controls =
            [
                PublisherLink("Acme Studio"),
            ],
        };

        Assert.True(BrowserAutomationVerifier.IsVerifiedOrderedSourceCollectionPage(
            officialCollection,
            "Acme Studio"));
        Assert.True(BrowserAutomationVerifier.IsVerifiedOrderedSourceCollectionPage(
            new BrowserPageSnapshot
            {
                Url = "https://media.example/@acme-studio/videos",
                Title = "Acme Studio - Media",
                Controls =
                [
                    new BrowserControl
                    {
                        Role = "tab",
                        AccessibleName = "Videos",
                        Visible = true,
                        Enabled = true,
                    },
                ],
            },
            "Acme Studio"));
        Assert.False(BrowserAutomationVerifier.IsVerifiedOrderedSourceCollectionPage(
            searchResult,
            "Acme Studio"));
    }

    [Fact]
    public void Ordered_source_completion_rejects_a_third_party_item_that_only_mentions_the_source()
    {
        var result = _verifier.VerifyTerminal(
            "Open the latest video by Acme Studio on the media site.",
            new BrowserPlannerDecision
            {
                Done = true,
                ResultSummary = "Opened the latest Acme Studio video.",
            },
            new BrowserPageSnapshot
            {
                Url = "https://media.example/watch?id=review",
                Title = "Acme Studio product review",
                PageText = "A review of Acme Studio products",
                Controls =
                [
                    PublisherLink("Independent Review Lab"),
                ],
            },
            Array.Empty<BrowserActionReceipt>(),
            orderedSourceCollectionVerified: true);

        Assert.False(result.Verified);
        Assert.Contains("publisher/source", result.Error, StringComparison.OrdinalIgnoreCase);
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
    public void Multi_action_request_requires_distinct_evidence_for_every_requested_step()
    {
        const string request =
            "Fill Display name, choose Browser automation, choose Speech recognition, and set the date.";
        var decision = new BrowserPlannerDecision { Done = true, ResultSummary = "Done", CompletionChecks = [
            FinalCheck("name", "textbox", "Display name", "type", "Regression User"),
            FinalCheck("topic", "combobox", "Topic", "select_option", "Browser automation"),
            FinalCheck("custom-topic", "combobox", "Custom topic", "select_option", "Speech recognition"),
            FinalCheck("date", "textbox", "Practice date", "type", "2026-07-29")] };
        var partial = _verifier.VerifyTerminal(
            request,
            decision,
            new BrowserPageSnapshot
            {
                PageText = "Practice form",
                Controls =
                [
                    Field("name", "textbox", "Display name", "Regression User"),
                    Field("topic", "combobox", "Topic", "Browser automation"),
                    Field("date", "textbox", "Practice date", "2026-07-29"),
                ],
            },
            new[]
            {
                Changed("type", "name", "textbox", "Display name", "Regression User", valueMatches: true),
                Changed("select_option", "topic", "combobox", "Topic", "Browser automation", valueMatches: true, selectedChanged: true),
                Changed("type", "date", "textbox", "Practice date", "2026-07-29", valueMatches: true),
            });

        Assert.False(partial.Verified);
        Assert.Contains("3 of 4", partial.Error);

        var complete = _verifier.VerifyTerminal(
            request,
            decision,
            new BrowserPageSnapshot
            {
                PageText = "Practice form",
                Controls =
                [
                    Field("name", "textbox", "Display name", "Regression User"),
                    Field("topic", "combobox", "Topic", "Browser automation"),
                    new BrowserControl
                    {
                        Id = "custom-topic",
                        FieldId = "custom-topic",
                        Role = "combobox",
                        AccessibleName = "Custom topic",
                        Text = "Speech recognition",
                        Visible = true,
                        Enabled = true,
                    },
                    Field("date", "textbox", "Practice date", "2026-07-29"),
                ],
            },
            new[]
            {
                Changed("type", "name", "textbox", "Display name", "Regression User", valueMatches: true),
                Changed("select_option", "topic", "combobox", "Topic", "Browser automation", valueMatches: true, selectedChanged: true),
                Changed(
                    "select_option",
                    "custom-topic",
                    "combobox",
                    "Custom topic",
                    "Speech recognition",
                    valueMatches: true,
                    selectedChanged: true),
                Changed("type", "date", "textbox", "Practice date", "2026-07-29", valueMatches: true),
            });

        Assert.True(complete.Verified);

        var lostEarlierValues = _verifier.VerifyTerminal(
            request,
            decision,
            new BrowserPageSnapshot
            {
                PageText = "Practice form",
                Controls =
                [
                    Field("name", "textbox", "Display name", ""),
                    Field("topic", "combobox", "Topic", ""),
                    Field("custom-topic", "combobox", "Custom topic", "Speech recognition"),
                    Field("date", "textbox", "Practice date", "2026-07-29"),
                ],
            },
            new[]
            {
                Changed("type", "name", "textbox", "Display name", "Regression User", valueMatches: true),
                Changed("select_option", "topic", "combobox", "Topic", "Browser automation", valueMatches: true, selectedChanged: true),
                Changed("select_option", "custom-topic", "combobox", "Custom topic", "Speech recognition", valueMatches: true, selectedChanged: true),
                Changed("type", "date", "textbox", "Practice date", "2026-07-29", valueMatches: true),
            });

        Assert.False(lostEarlierValues.Verified);
        Assert.Contains("2 of 4", lostEarlierValues.Error);
    }

    [Fact]
    public void Form_completion_rejects_planner_checks_that_omit_an_explicit_field_mutation()
    {
        const string request =
            "Use Browser Automation on the current form. Set Text input to Boundary Gamma, "
            + "then set Disabled input to Enabled Value. Leave every other field unchanged and do not submit or reset.";
        var result = _verifier.VerifyTerminal(
            request,
            new BrowserPlannerDecision
            {
                Done = true,
                ResultSummary = "Set Text input and left Disabled input unchanged as requested.",
                CompletionChecks =
                [
                    FinalCheck("text", "textbox", "Text input", "type", "Boundary Gamma"),
                ],
            },
            new BrowserPageSnapshot
            {
                Url = "https://example.test/form",
                Controls =
                [
                    Field("text", "textbox", "Text input", "Boundary Gamma"),
                    new BrowserControl
                    {
                        Id = "disabled",
                        FieldId = "disabled",
                        Role = "textbox",
                        AccessibleName = "Disabled input",
                        Value = "Disabled input",
                        Visible = true,
                        Enabled = false,
                    },
                ],
            },
            [
                Changed(
                    "type",
                    "text",
                    "textbox",
                    "Text input",
                    "Boundary Gamma",
                    valueMatches: true),
            ]);

        Assert.False(result.Verified);
        Assert.Contains("explicitly requested form", result.Error, StringComparison.OrdinalIgnoreCase);

        var duplicateCheck = _verifier.VerifyTerminal(
            request,
            new BrowserPlannerDecision
            {
                Done = true,
                ResultSummary = "Done.",
                CompletionChecks =
                [
                    FinalCheck("text", "textbox", "Text input", "type", "Boundary Gamma"),
                    FinalCheck("text", "textbox", "Text input", "type", "Boundary Gamma"),
                ],
            },
            new BrowserPageSnapshot
            {
                Url = "https://example.test/form",
                Controls = [Field("text", "textbox", "Text input", "Boundary Gamma")],
            },
            [Changed("type", "text", "textbox", "Text input", "Boundary Gamma", valueMatches: true)]);

        Assert.False(duplicateCheck.Verified);
        Assert.Contains("1 of 2", duplicateCheck.Error);
    }

    [Fact]
    public void Authoritative_same_task_correction_replaces_the_superseded_form_obligation()
    {
        var request = BrowserAutomationCoordinator.BuildCorrectedTaskRequest(
            "Use Browser Automation to set Text input to my project reference. "
            + "Ask me for the reference because it has not been provided. Leave all other fields unchanged. Do not submit.",
            "Browser Automation is on the wrong page. I am on the correct page now; continue here. "
            + "Set Username to Boundary Handoff instead. Leave all other fields unchanged. Do not submit.");
        var result = _verifier.VerifyTerminal(
            request,
            new BrowserPlannerDecision
            {
                Done = true,
                ResultSummary = "The Username field has been set to Boundary Handoff.",
                CompletionChecks =
                [
                    FinalCheck("username", "textbox", "Username", "type", "Boundary Handoff"),
                ],
            },
            new BrowserPageSnapshot
            {
                TabId = 43,
                Url = "https://example.test/correct-form",
                Controls = [Field("username", "textbox", "Username", "Boundary Handoff")],
            },
            [
                Changed(
                    "type",
                    "username",
                    "textbox",
                    "Username",
                    "Boundary Handoff",
                    valueMatches: true),
            ]);

        Assert.True(result.Verified, result.Error);
    }

    [Fact]
    public void Authoritative_same_field_value_correction_is_one_final_obligation()
    {
        var request = BrowserAutomationCoordinator.BuildCorrectedTaskRequest(
            "Use Browser Automation to set the Display name field to my project reference. "
            + "Ask me for the reference because it has not been provided.",
            "Set Display name to P11-HANDOFF-0918 instead. Leave every other field unchanged and do not submit.");
        var result = _verifier.VerifyTerminal(
            request,
            new BrowserPlannerDecision
            {
                Done = true,
                ResultSummary = "Display name was corrected.",
                CompletionChecks =
                [
                    FinalCheck("name", "textbox", "Display name", "type", "P11-HANDOFF-0918"),
                ],
            },
            new BrowserPageSnapshot
            {
                TabId = 62,
                Url = "https://example.test/correct-form",
                Controls = [Field("name", "textbox", "Display name", "P11-HANDOFF-0918")],
            },
            [
                Changed(
                    "type",
                    "name",
                    "textbox",
                    "Display name",
                    "P11-HANDOFF-0918",
                    valueMatches: true),
            ]);

        Assert.True(result.Verified, result.Error);
    }

    [Fact]
    public void Additive_same_task_correction_keeps_compatible_form_obligations()
    {
        var request = BrowserAutomationCoordinator.BuildCorrectedTaskRequest(
            "Set Display name to Boundary User.",
            "Also set Topic to Browser automation.");
        var result = _verifier.VerifyTerminal(
            request,
            new BrowserPlannerDecision
            {
                Done = true,
                ResultSummary = "Only Topic was set.",
                CompletionChecks =
                [
                    FinalCheck("topic", "combobox", "Topic", "select_option", "Browser automation"),
                ],
            },
            new BrowserPageSnapshot
            {
                Url = "https://example.test/form",
                Controls =
                [
                    Field("name", "textbox", "Display name", ""),
                    Field("topic", "combobox", "Topic", "Browser automation"),
                ],
            },
            [
                Changed(
                    "select_option",
                    "topic",
                    "combobox",
                    "Topic",
                    "Browser automation",
                    valueMatches: true,
                    selectedChanged: true),
            ]);

        Assert.False(result.Verified);
        Assert.Contains("1 of 2", result.Error);
    }

    [Theory]
    [InlineData("Open https://example.test/form and fill Text input with Delta.", 1)]
    [InlineData("Set Name to Ada, then set Email to ada@example.test.", 2)]
    [InlineData("Choose Blue, check Updates, and clear Notes.", 3)]
    [InlineData("Set Notes to \"set check select\".", 1)]
    [InlineData("Fill the current form from the provided context.", 1)]
    [InlineData("Set Text input to A. Select Two in Dropdown (select). Check Default checkbox.", 3)]
    public void Explicit_form_mutation_count_excludes_navigation_and_quoted_payloads(
        string request,
        int expected) =>
        Assert.Equal(expected, BrowserAutomationVerifier.CountRequestedFormMutationSteps(request));

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
            new BrowserPlannerDecision { Done = true, ResultSummary = "Done", CompletionChecks = [
                FinalCheck("a", "radio", "Answer A", "check", ""), FinalCheck("b", "option", "Answer B", "check", "")] },
            new BrowserPageSnapshot
            {
                PageText = "Practice quiz",
                Controls =
                [
                    new BrowserControl
                    {
                        Id = "answer-a", FieldId = "a",
                        Role = "radio",
                        AccessibleName = "Answer A",
                        Checked = true,
                    },
                    new BrowserControl
                    {
                        Id = "answer-b", FieldId = "b",
                        Role = "option",
                        AccessibleName = "Answer B",
                        Selected = true,
                    },
                ],
            },
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "click",
                    TargetRole = "radio",
                    TargetName = "Answer A", TargetFieldId = "a",
                    CheckedChanged = true,
                },
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "click",
                    TargetRole = "option",
                    TargetName = "Answer B", TargetFieldId = "b",
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
    public void Informational_find_cannot_complete_from_an_unrelated_empty_search_page()
    {
        var result = _verifier.VerifyTerminal(
            "Go to the requested site, open the Ada Lovelace article, and find her date of birth.",
            new BrowserPlannerDecision
            {
                Done = true,
                ResultSummary = "Ada Lovelace's date of birth is 10 December 1815.",
            },
            new BrowserPageSnapshot
            {
                Url = "https://example.test/search?query=",
                Title = "Search",
                PageText = "Search the encyclopedia",
            },
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "open_tab",
                    Navigation = true,
                    BeforeUrl = "https://unrelated.test/",
                    AfterUrl = "https://example.test/search",
                },
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "type",
                    TargetRole = "searchbox",
                    TargetName = "Search",
                    ValueMatches = true,
                    NoVisibleChange = false,
                },
            });

        Assert.False(result.Verified);
        Assert.Contains("not supported", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Informational_find_completes_when_the_final_page_contains_the_reported_result()
    {
        var result = _verifier.VerifyTerminal(
            "Open the Ada Lovelace article and find her date of birth.",
            new BrowserPlannerDecision
            {
                Done = true,
                ResultSummary = "Ada Lovelace's date of birth is 10 December 1815.",
            },
            new BrowserPageSnapshot
            {
                Url = "https://example.test/articles/ada-lovelace",
                Title = "Ada Lovelace",
                PageText = "Augusta Ada King, Countess of Lovelace, was born on 10 December 1815.",
            },
            Array.Empty<BrowserActionReceipt>());

        Assert.True(result.Verified);
    }

    [Fact]
    public void Find_then_click_keeps_the_final_action_contract()
    {
        Assert.False(BrowserAutomationVerifier.RequestsGroundedInformation(
            "Find the application button and click it."));
        Assert.True(BrowserAutomationVerifier.RequestsGroundedInformation(
            "Click the details tab and tell me what eligibility it shows."));
    }

    [Fact]
    public void Final_open_state_is_not_counted_as_a_third_browser_mutation()
    {
        const string request =
            "Open https://example.test/hackathons?order_by=prize. "
            + "Identify the first hackathon and its prize, then open that card using its live link. "
            + "Verify the destination is the same hackathon and stop once it is open.";
        var destination = "https://revenuecat-shipaton-2026.example.test/";
        var result = _verifier.VerifyTerminal(
            request,
            new BrowserPlannerDecision { Done = true, ResultSummary = "Opened RevenueCat Shipaton 2026." },
            new BrowserPageSnapshot
            {
                TabId = 42,
                Url = destination,
                Title = "RevenueCat Shipaton 2026",
                PageText = "$740,000 in prizes",
            },
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "navigate",
                    Navigation = true,
                    BeforeUrl = "https://example.test/",
                    AfterUrl = "https://example.test/hackathons?order_by=prize",
                    TabId = 42,
                },
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "click",
                    TargetRole = "link",
                    TargetName = "RevenueCat Shipaton 2026 $740,000 in prizes",
                    Navigation = true,
                    BeforeUrl = "https://example.test/hackathons?order_by=prize",
                    AfterUrl = destination,
                    TabId = 42,
                },
            });

        Assert.Equal(2, BrowserAutomationVerifier.CountRequestedMutationSteps(request));
        Assert.True(result.Verified);
    }

    [Fact]
    public void Navigated_link_target_cannot_verify_a_different_final_entity()
    {
        var result = _verifier.VerifyTerminal(
            "Open the first hackathon card.",
            new BrowserPlannerDecision { Done = true, ResultSummary = "Opened the first hackathon." },
            new BrowserPageSnapshot
            {
                Url = "https://other-hackathon.example.test/",
                Title = "Other Hackathon",
                PageText = "$10 in prizes",
            },
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "click",
                    TargetRole = "link",
                    TargetName = "RevenueCat Shipaton 2026 $740,000 in prizes",
                    Navigation = true,
                    BeforeUrl = "https://example.test/hackathons",
                    AfterUrl = "https://other-hackathon.example.test/",
                },
            });

        Assert.False(result.Verified);
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

    [Fact]
    public void Click_that_navigates_back_to_a_same_site_parent_is_not_progress()
    {
        var result = _verifier.VerifyAction(
            new BrowserPlannerAction { Type = "click", TargetName = "Browse" },
            new BrowserActionReceipt
            {
                Ok = true,
                Action = "click",
                Navigation = true,
                NoVisibleChange = false,
                BeforeUrl = "https://example.test/results/revenuecat",
                AfterUrl = "https://example.test/",
            });

        Assert.False(result.Verified);
        Assert.Contains("backward", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Terminal_verification_rejects_a_homepage_that_discarded_verified_detail_progress()
    {
        var result = _verifier.VerifyTerminal(
            "Open the requested result page.",
            new BrowserPlannerDecision { Done = true, ResultSummary = "Done" },
            new BrowserPageSnapshot
            {
                Url = "https://example.test/",
                PageText = "Home",
            },
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "click",
                    Navigation = true,
                    NoVisibleChange = false,
                    BeforeUrl = "https://example.test/results",
                    AfterUrl = "https://example.test/results/revenuecat",
                },
            });

        Assert.False(result.Verified);
        Assert.Contains("discarded", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grounded_final_information_outranks_an_obsolete_url_regression_receipt()
    {
        var result = _verifier.VerifyTerminal(
            "Open the requested article and find the subject's date of birth.",
            new BrowserPlannerDecision
            {
                Done = true,
                ResultSummary = "The subject's date of birth is 10 December 1815.",
            },
            new BrowserPageSnapshot
            {
                Url = "https://example.test/articles/subject",
                Title = "Subject",
                PageText = "The subject was born on 10 December 1815.",
            },
            new[]
            {
                new BrowserActionReceipt
                {
                    Ok = true,
                    Action = "click",
                    Navigation = true,
                    BeforeUrl = "https://example.test/search?query=subject",
                    AfterUrl = "https://example.test/articles/subject?from=search",
                },
            });

        Assert.True(result.Verified);
    }

    [Fact]
    public void Corrected_task_accepts_navigation_that_completed_during_replanning()
    {
        var request = BrowserAutomationCoordinator.BuildCorrectedTaskRequest(
            "Use Browser Automation to search the encyclopedia for Charles Babbage and open his article.",
            "No, use Grace Hopper instead. Open the Grace Hopper article.");
        var observedNavigation = BrowserAutomationCoordinator.CreateObservedNavigationReceipt(
            "task-1",
            new BrowserPageSnapshot
            {
                TabId = 42,
                Url = "https://example.test/search?q=grace-hopper",
            },
            new BrowserPageSnapshot
            {
                TabId = 42,
                Url = "https://example.test/articles/grace-hopper",
                Title = "Grace Hopper",
                PageText = "Grace Hopper",
            });

        var result = _verifier.VerifyTerminal(
            request,
            new BrowserPlannerDecision
            {
                Done = true,
                ResultSummary = "The Grace Hopper article is open.",
            },
            new BrowserPageSnapshot
            {
                TabId = 42,
                Url = "https://example.test/articles/grace-hopper",
                Title = "Grace Hopper",
                PageText = "Grace Hopper",
            },
            new[]
            {
                Changed(
                    "type",
                    "search",
                    "searchbox",
                    "Search",
                    "Grace Hopper",
                    valueMatches: true),
                Changed("click", "submit", "button", "Search"),
                observedNavigation,
            });

        Assert.True(result.Verified, result.Error);
    }

    private static BrowserPlannerAction FinalCheck(string field, string role, string name, string type, string value) => new()
    {
        TargetFieldId = field, TargetRole = role, TargetName = name, Type = type, Text = value, Option = value,
    };

    private static BrowserActionReceipt Changed(
        string action,
        string targetId,
        string role,
        string name,
        string expectedValue = "",
        bool valueMatches = false,
        bool selectedChanged = false) => new()
    {
        Ok = true,
        Action = action,
        TargetId = targetId,
        TargetFieldId = targetId,
        TargetRole = role,
        TargetName = name,
        ExpectedValue = expectedValue,
        ValueMatches = valueMatches,
        SelectedChanged = selectedChanged,
        NoVisibleChange = false,
    };

    private static BrowserControl Field(
        string id,
        string role,
        string name,
        string value) => new()
    {
        Id = id,
        FieldId = id,
        Role = role,
        AccessibleName = name,
        Value = value,
        Visible = true,
        Enabled = true,
    };

    private static BrowserControl PublisherLink(string name) => new()
    {
        Id = $"publisher-{name.Replace(' ', '-')}",
        Role = "link",
        AccessibleName = name,
        Text = name,
        Visible = true,
        Enabled = true,
    };
}
