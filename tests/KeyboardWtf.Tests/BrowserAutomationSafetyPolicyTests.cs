using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserAutomationSafetyPolicyTests
{
    private readonly BrowserAutomationSafetyPolicy _policy = new();

    [Fact]
    public void Enter_on_a_non_search_field_requires_explicit_final_submission()
    {
        var result = _policy.EvaluateAction(
            "Fill this form",
            new BrowserPlannerAction
            {
                Type = "press_key",
                Key = "Enter",
                TargetRole = "textbox",
                TargetName = "Message",
            });

        Assert.False(result.MayStart);
        Assert.True(result.PauseBeforeSensitiveStep);
    }

    [Fact]
    public void Enter_on_search_is_allowed()
    {
        var result = _policy.EvaluateAction(
            "Search this site for browser automation",
            new BrowserPlannerAction
            {
                Type = "press_key",
                Key = "Enter",
                TargetRole = "searchbox",
                TargetName = "Search",
            });

        Assert.True(result.MayStart);
    }

    [Theory]
    [InlineData("textbox", "Find a repository…")]
    [InlineData("textbox", "Repository search")]
    [InlineData("searchbox", "")]
    public void Enter_on_grounded_search_or_navigation_control_is_allowed(
        string role,
        string name)
    {
        var result = _policy.EvaluateAction(
            "Find browser-use/browser-use on GitHub",
            new BrowserPlannerAction
            {
                Type = "press_key",
                Key = "Enter",
                TargetRole = role,
                TargetName = name,
            });

        Assert.True(result.MayStart);
    }

    [Fact]
    public void Enter_on_message_composer_remains_blocked()
    {
        var result = _policy.EvaluateAction(
            "Draft a message but do not send it",
            new BrowserPlannerAction
            {
                Type = "press_key",
                Key = "Enter",
                TargetRole = "textbox",
                TargetName = "Message",
            });

        Assert.False(result.MayStart);
    }

    [Fact]
    public void Enter_is_allowed_in_a_read_only_search_and_compare_workflow()
    {
        var result = _policy.EvaluateAction(
            "Find and compare the cheapest round-trip flights. Do not book or submit anything.",
            new BrowserPlannerAction
            {
                Type = "press_key",
                Key = "Enter",
                TargetRole = "button",
                TargetName = "Continue",
            });

        Assert.True(result.MayStart);
        Assert.False(result.PauseBeforeSensitiveStep);
    }

    [Fact]
    public void Search_context_does_not_allow_enter_on_a_message_composer()
    {
        var result = _policy.EvaluateAction(
            "Find Yaksh and draft a message without sending it.",
            new BrowserPlannerAction
            {
                Type = "press_key",
                Key = "Enter",
                TargetRole = "textbox",
                TargetName = "Message composer",
            });

        Assert.False(result.MayStart);
        Assert.True(result.PauseBeforeSensitiveStep);
    }

    [Fact]
    public void Missing_personal_information_is_not_invented()
    {
        var result = _policy.EvaluateAction(
            "Fill this form",
            new BrowserPlannerAction
            {
                Type = "type",
                TargetRole = "textbox",
                TargetName = "What is your name?",
                Text = "Invented Name",
            });

        Assert.False(result.MayStart);
        Assert.True(result.PauseBeforeSensitiveStep);
    }

    [Fact]
    public void Explicitly_supplied_personal_information_can_be_filled()
    {
        var result = _policy.EvaluateAction(
            "Fill the name field with Ada Lovelace",
            new BrowserPlannerAction
            {
                Type = "type",
                TargetRole = "textbox",
                TargetName = "Full name",
                Text = "Ada Lovelace",
            });

        Assert.True(result.MayStart);
    }

    [Theory]
    [InlineData("Add MX Master 4 to my cart")]
    [InlineData("Fill this form")]
    [InlineData("Copy this page summary")]
    public void Ordinary_requested_interactions_can_start(string request)
    {
        Assert.True(_policy.EvaluateRequest(request).MayStart);
    }

    [Theory]
    [InlineData("Enter my OTP")]
    [InlineData("Complete checkout and payment")]
    [InlineData("Fill the password")]
    public void Sensitive_workflows_pause_at_manual_boundary(string request)
    {
        var result = _policy.EvaluateRequest(request);
        Assert.True(result.MayStart);
        Assert.True(result.PauseBeforeSensitiveStep);
    }

    [Fact]
    public void Final_submit_is_blocked_when_not_explicit()
    {
        var result = _policy.EvaluateAction(
            "Fill this form",
            new BrowserPlannerAction
            {
                Type = "click",
                TargetRole = "button",
                TargetName = "Submit",
            });

        Assert.False(result.MayStart);
        Assert.True(result.PauseBeforeSensitiveStep);
    }

    [Theory]
    [InlineData("Fill this form. Do not submit.")]
    [InlineData("Answer the quiz without submitting it.")]
    [InlineData("Fill every field and stop before Submit.")]
    [InlineData("Draft the message but don't send it.")]
    public void Negated_final_action_is_not_permission(string request)
    {
        var requestDecision = _policy.EvaluateRequest(request);
        var actionDecision = _policy.EvaluateAction(
            request,
            new BrowserPlannerAction
            {
                Type = "click",
                TargetRole = "button",
                TargetName = request.Contains("send", StringComparison.OrdinalIgnoreCase) ? "Send" : "Submit",
            });

        Assert.False(requestDecision.FinalSubmitExplicitlyRequested);
        Assert.False(actionDecision.MayStart);
        Assert.True(actionDecision.PauseBeforeSensitiveStep);
    }

    [Theory]
    [InlineData("Fill and submit this form.")]
    [InlineData("Send the drafted message.")]
    public void Affirmative_final_action_is_explicit_permission(string request)
    {
        var requestDecision = _policy.EvaluateRequest(request);
        var actionDecision = _policy.EvaluateAction(
            request,
            new BrowserPlannerAction
            {
                Type = "click",
                TargetRole = "button",
                TargetName = request.StartsWith("Send", StringComparison.OrdinalIgnoreCase) ? "Send" : "Submit",
            });

        Assert.True(requestDecision.FinalSubmitExplicitlyRequested);
        Assert.True(actionDecision.MayStart);
    }
}
