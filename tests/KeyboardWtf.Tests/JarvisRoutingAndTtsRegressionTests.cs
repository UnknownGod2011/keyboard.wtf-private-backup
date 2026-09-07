namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

public sealed class JarvisRoutingAndTtsRegressionTests
{
    [Theory]
    [InlineData("https://example.com/portal", true)]
    [InlineData("http://localhost:8080/test", true)]
    [InlineData("example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a URL", false)]
    public void Explicit_selected_link_alias_accepts_safe_web_destinations(
        string selected,
        bool expected)
    {
        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["text"] = selected,
        };

        Assert.Equal(expected, CommandRegistry.TryGetSelectedHttpUrl(result).Length > 0);
    }

    [Fact]
    public void Selected_hostname_is_canonicalized_before_link_persistence()
    {
        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["text"] = "example.com",
        };

        Assert.Equal("https://example.com/", CommandRegistry.TryGetSelectedHttpUrl(result));
    }

    [Fact]
    public void Deterministic_tool_failure_is_not_executed_twice_in_one_turn()
    {
        var guard = new ToolAttemptGuard();
        var args = JsonSerializer.SerializeToElement(new { app_name = "missing app" });
        guard.Record("open_app", args, new Dictionary<string, object>
        {
            ["ok"] = false,
            ["supported"] = false,
            ["message"] = "I could not find that app.",
        });

        Assert.True(guard.TryGetSuppressed("open_app", args, out var result));
        Assert.True((bool)result["duplicate_suppressed"]);
    }

    [Fact]
    public void Transient_tool_failure_allows_only_one_silent_retry()
    {
        var guard = new ToolAttemptGuard();
        var args = JsonSerializer.SerializeToElement(new { url = "https://example.com" });
        var transient = new Dictionary<string, object>
        {
            ["ok"] = false,
            ["supported"] = true,
            ["message"] = "The connection was interrupted.",
        };
        guard.Record("open_url", args, transient);
        Assert.False(guard.TryGetSuppressed("open_url", args, out _));

        guard.Record("open_url", args, transient);
        Assert.True(guard.TryGetSuppressed("open_url", args, out _));
    }

    [Fact]
    public void Verified_first_tab_switch_receipt_is_reused_not_blocked()
    {
        var receipt = new BrowserPreRouteReceipt
        {
            UserTurn = "Switch to the first Chrome tab.",
            ToolName = "browser_action",
            Ok = true,
            Message = "Switched to the first tab in the existing Chrome window.",
        };
        var now = receipt.CreatedUtc.AddSeconds(1);

        Assert.True(receipt.AppliesTo("browser_action", now));
        Assert.False(receipt.BlocksCompetingTool("browser_action", now));
        Assert.True(receipt.BlocksCompetingTool("type_text", now));
        Assert.True(receipt.Ok);
        Assert.Contains("Switched", receipt.Message);
    }

    [Fact]
    public void Verified_second_tab_switch_echo_does_not_execute_twice()
    {
        var receipt = new BrowserPreRouteReceipt
        {
            UserTurn = "Switch to Chrome's second tab!",
            ToolName = "browser_action",
            Ok = true,
            Message = "Switched to the second tab in the existing Chrome window.",
        };

        Assert.True(receipt.IsEchoOf(
            "switch to chrome's second tab",
            receipt.CreatedUtc.AddSeconds(1)));
        Assert.False(receipt.IsEchoOf(
            "switch to chrome's third tab",
            receipt.CreatedUtc.AddSeconds(1)));
    }

    [Fact]
    public void Flight_task_start_receipt_is_acknowledged_as_started()
    {
        var receipt = new BrowserPreRouteReceipt
        {
            UserTurn = "Find cheap Switzerland flights.",
            ToolName = "browser_automation",
            Ok = true,
            Started = true,
            TaskId = "flight-task",
            Message = "Browser automation started in the existing Chrome session.",
        };

        Assert.True(receipt.AppliesTo(
            "browser_automation",
            receipt.CreatedUtc.AddSeconds(1)));
        Assert.True(receipt.Started);
        Assert.Equal("flight-task", receipt.TaskId);
        Assert.Contains("started", receipt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Durable_workflow_receipt_blocks_reinterpretation_but_accepts_its_one_tool_result()
    {
        var receipt = new BrowserPreRouteReceipt
        {
            UserTurn = "potato patato",
            ToolName = "run_workflow",
            Ok = true,
            Message = "Started potato patato.",
            Result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["message"] = "Started potato patato.",
            },
        };
        var now = receipt.CreatedUtc.AddSeconds(20);

        Assert.True(receipt.AppliesTo("run_workflow", now));
        Assert.False(receipt.BlocksCompetingTool("run_workflow", now));
        Assert.True(receipt.BlocksCompetingTool("browser_automation", now));
        Assert.True(receipt.BlocksCompetingTool("open_url", now));
    }

    [Fact]
    public void Stale_receipt_cannot_contaminate_the_next_turn()
    {
        var receipt = new BrowserPreRouteReceipt
        {
            UserTurn = "Fill this form.",
            ToolName = "browser_automation",
            Ok = true,
            Started = true,
        };

        var later = receipt.CreatedUtc.AddSeconds(13);
        Assert.False(receipt.AppliesTo("browser_automation", later));
        Assert.False(receipt.BlocksCompetingTool("type_text", later));
    }

    [Fact]
    public void Completed_and_cancelled_lifecycle_events_are_terminal_not_background_work()
    {
        foreach (var state in new[] { "Completed", "Cancelled", "Failed" })
        {
            var text = GeminiLiveConversationService.BuildLifecycleEventText(
                new JarvisLifecycleAnnouncement
                {
                    Source = "browser_automation",
                    State = state,
                    Detail = "Verified terminal result.",
                });

            Assert.Contains("no longer running", text);
            Assert.Contains($"\"state\":\"{state}\"", text);
        }
    }

    [Theory]
    [InlineData(BrowserTaskState.Queued, true, "starting")]
    [InlineData(BrowserTaskState.Planning, true, "running")]
    [InlineData(BrowserTaskState.Paused, true, "paused")]
    [InlineData(BrowserTaskState.Completed, false, "completed")]
    [InlineData(BrowserTaskState.Failed, false, "failed")]
    [InlineData(BrowserTaskState.Cancelled, false, "cancelled")]
    public void Browser_status_has_one_authoritative_lifecycle(
        BrowserTaskState detailState,
        bool active,
        string expected)
    {
        var snapshot = new BrowserAutomationSnapshot
        {
            TaskId = "task-1",
            State = detailState,
            CanCancel = active,
        };

        Assert.Equal(expected, CommandRegistry.NormalizeBrowserTaskState(snapshot));
    }

    [Fact]
    public void Missing_task_is_idle_even_if_the_default_detail_state_is_queued()
    {
        Assert.Equal(
            "idle",
            CommandRegistry.NormalizeBrowserTaskState(new BrowserAutomationSnapshot()));
    }

    [Fact]
    public void Cancelled_speech_cannot_clear_the_next_response()
    {
        var leases = new TtsPlaybackLease();
        var first = leases.Begin();

        Assert.Equal(first, leases.CancelCurrent());
        var second = leases.Begin();

        Assert.False(leases.Complete(first));
        Assert.True(leases.IsCurrent(second));
        Assert.True(leases.Complete(second));
        Assert.False(leases.IsCurrent(second));
    }

    [Fact]
    public void Live_speech_buffer_keeps_complete_normal_responses()
    {
        var buffer = GeminiLiveConversationService.CreatePlaybackBuffer();
        var fiveSeconds = new byte[24000 * 2 * 5];

        Assert.Equal(TimeSpan.FromSeconds(45), buffer.BufferDuration);
        Assert.False(buffer.DiscardOnBufferOverflow);
        Assert.True(GeminiLiveConversationService.TryQueuePlaybackAudio(
            buffer,
            fiveSeconds));
        Assert.Equal(fiveSeconds.Length, buffer.BufferedBytes);
    }

    [Fact]
    public void Excessive_live_speech_fails_explicitly_instead_of_dropping_its_tail()
    {
        var buffer = GeminiLiveConversationService.CreatePlaybackBuffer();
        var tooLarge = new byte[buffer.BufferLength + 1];

        Assert.False(GeminiLiveConversationService.TryQueuePlaybackAudio(
            buffer,
            tooLarge));
        Assert.Equal(0, buffer.BufferedBytes);
    }

    [Fact]
    public void Needs_user_action_lifecycle_keeps_the_exact_intervention_and_same_task_contract()
    {
        var text = GeminiLiveConversationService.BuildLifecycleEventText(
            new JarvisLifecycleAnnouncement
            {
                Source = "browser_automation",
                State = "NeedsUserAction",
                Detail = "Please sign in to Gmail in the controlled tab.",
            });

        Assert.Contains("Please sign in to Gmail", text);
        Assert.Contains("same browser task remains paused", text);
        Assert.Contains("say continue", text);
    }

    [Fact]
    public void Zero_control_form_completion_uses_one_bounded_visual_recovery()
    {
        var decision = new BrowserPlannerDecision
        {
            Done = true,
            Action = new BrowserPlannerAction { Type = "done" },
        };

        Assert.True(BrowserAutomationCoordinator.ShouldForceVisualRecovery(
            "Fill this quiz, but do not submit it.",
            new BrowserPageSnapshot { Controls = Array.Empty<BrowserControl>() },
            decision,
            visionRecoveryAlreadyUsed: false));
        Assert.False(BrowserAutomationCoordinator.ShouldForceVisualRecovery(
            "Fill this quiz, but do not submit it.",
            new BrowserPageSnapshot { Controls = Array.Empty<BrowserControl>() },
            decision,
            visionRecoveryAlreadyUsed: true));
    }

    [Fact]
    public void Read_only_completion_and_sensitive_blockers_do_not_force_visual_click_recovery()
    {
        var emptyPage = new BrowserPageSnapshot { Controls = Array.Empty<BrowserControl>() };

        Assert.False(BrowserAutomationCoordinator.ShouldForceVisualRecovery(
            "Summarize this page.",
            emptyPage,
            new BrowserPlannerDecision { Done = true },
            visionRecoveryAlreadyUsed: false));
        Assert.False(BrowserAutomationCoordinator.ShouldForceVisualRecovery(
            "Fill this form.",
            emptyPage,
            new BrowserPlannerDecision
            {
                NeedsUserAction = true,
                UserAction = "Please complete the CAPTCHA manually.",
            },
            visionRecoveryAlreadyUsed: false));
    }
}
