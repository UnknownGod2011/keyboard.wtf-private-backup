using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserSemanticControlRankerTests
{
    [Fact]
    public void Exact_small_control_beats_broad_parent_container()
    {
        var controls = new[]
        {
            Control("parent", "region", "Latest Popular Oldest videos", 900, 500),
            Control("oldest", "tab", "Oldest", 92, 40),
            Control("popular", "tab", "Popular", 92, 40),
        };

        var ranked = new BrowserSemanticControlRanker().Rank("click Oldest", controls);
        Assert.Equal("oldest", ranked[0].Id);
    }

    [Fact]
    public void Search_goal_prefers_editable_searchbox()
    {
        var controls = new[]
        {
            Control("search-link", "link", "Search help", 100, 30),
            Control("searchbox", "textbox", "Search", 420, 40, editable: true),
        };

        var ranked = new BrowserSemanticControlRanker().Rank("search for browser-use", controls);
        Assert.Equal("searchbox", ranked[0].Id);
    }

    [Fact]
    public void Explicit_current_page_click_uses_one_unique_exact_control()
    {
        var page = new BrowserPageSnapshot
        {
            Controls = new[]
            {
                Control("latest", "tab", "Latest", 92, 40),
                Control("oldest", "tab", "Oldest", 92, 40),
            },
        };

        var matched = BrowserAutomationCoordinator.TryBuildDirectCurrentPageAction(
            "On this page, click the Oldest control and stop after it is selected.",
            page,
            out var action);

        Assert.True(matched);
        Assert.Equal("click", action.Type);
        Assert.Equal("oldest", action.TargetId);
        Assert.True(action.CaptureBeforeAction);
    }

    [Fact]
    public void Direct_click_tolerates_a_natural_model_paraphrase()
    {
        var page = new BrowserPageSnapshot
        {
            Controls = new[]
            {
                Control("latest", "tab", "Latest", 92, 40),
                Control("oldest", "tab", "Oldest", 92, 40),
            },
        };

        Assert.True(BrowserAutomationCoordinator.TryBuildDirectCurrentPageAction(
            "click on the Oldest page control and stop once it is selected",
            page,
            out var action));
        Assert.Equal("oldest", action.TargetId);
    }

    [Fact]
    public void Direct_current_page_action_defers_when_exact_name_is_ambiguous()
    {
        var page = new BrowserPageSnapshot
        {
            Controls = new[]
            {
                Control("first", "button", "Continue", 92, 40),
                Control("second", "button", "Continue", 92, 40),
            },
        };

        Assert.False(BrowserAutomationCoordinator.TryBuildDirectCurrentPageAction(
            "Click Continue on this page.",
            page,
            out _));
    }

    [Fact]
    public void Direct_action_recognizes_an_already_selected_live_tab()
    {
        var action = new BrowserPlannerAction
        {
            Type = "click",
            TargetId = "oldest",
            TargetRole = "tab",
            TargetName = "Oldest",
        };
        var control = Control("oldest", "tab", "Oldest", 92, 40);
        control.Selected = true;

        Assert.True(BrowserAutomationCoordinator.DirectActionAlreadySatisfied(
            action,
            control));
    }

    [Theory]
    [InlineData("2.1.0", false)]
    [InlineData("2.1.1", true)]
    [InlineData("3.0.0", true)]
    [InlineData("", false)]
    public void Optional_action_capture_requires_a_non_blocking_helper(
        string version,
        bool expected)
    {
        Assert.Equal(
            expected,
            BrowserAutomationCoordinator.SupportsSafeActionCapture(version));
    }

    [Fact]
    public void Planner_does_not_repeat_a_field_value_already_verified_live()
    {
        Assert.True(BrowserAutomationCoordinator.DirectActionAlreadySatisfied(
            new BrowserPlannerAction
            {
                Type = "type",
                Text = "Sep 14, 2026",
            },
            new BrowserControl
            {
                Role = "textbox",
                Value = "Sep 14, 2026",
                Visible = true,
                Enabled = true,
            }));
    }

    private static BrowserControl Control(
        string id,
        string role,
        string name,
        double width,
        double height,
        bool editable = false) => new()
    {
        Id = id,
        Role = role,
        AccessibleName = name,
        Text = name,
        Enabled = true,
        Visible = true,
        Editable = editable,
        Bounds = new BrowserRectangle { Width = width, Height = height },
        SafeActions = editable ? new[] { "type" } : new[] { "click" },
    };
}
