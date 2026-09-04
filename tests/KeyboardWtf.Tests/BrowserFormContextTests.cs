using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserFormContextTests
{
    private static BrowserControl Choice(string id, string field, string label, bool selected) => new()
    {
        Id = id, FieldId = field, FieldLabel = label, FieldRequired = true,
        FieldAnswered = selected, Role = "radio", AccessibleName = "4",
        Checked = selected, Visible = true, Enabled = true,
    };

    [Fact]
    public void Repeated_choice_names_require_unique_field_grounding()
    {
        var first = Choice("g1c1", "usefulness", "Usefulness", true);
        var second = Choice("g1c2", "quality", "Quality", false);
        var page = new BrowserPageSnapshot { Controls = [first, second] };
        var action = new BrowserPlannerAction { Type = "click", TargetRole = "radio", TargetName = "4" };
        Assert.Null(BrowserAutomationCoordinator.FindMatchingControl(page, action));
        action.TargetFieldId = "quality";
        Assert.Same(second, BrowserAutomationCoordinator.FindMatchingControl(page, action));
        action.TargetId = "g1c1";
        Assert.Null(BrowserAutomationCoordinator.FindMatchingControl(page, action));
    }

    [Fact]
    public void Completion_cannot_accept_a_rating_in_the_wrong_required_question()
    {
        var page = new BrowserPageSnapshot
        {
            Controls = [Choice("new1", "usefulness", "Usefulness", true), Choice("new2", "quality", "Quality", false)],
        };
        var receipt = new BrowserActionReceipt
        {
            Ok = true, Action = "click", TargetName = "4", TargetRole = "radio",
            TargetFieldId = "usefulness", CheckedChanged = true,
        };
        var result = new BrowserAutomationVerifier().VerifyTerminal(
            "Fill the survey and skip unknown optional fields. Do not submit.",
            new BrowserPlannerDecision { Done = true }, page, [receipt]);
        Assert.False(result.Verified);
        Assert.Contains("Quality", result.Error);
        Assert.Contains("required", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Optional_empty_fields_do_not_block_complete_required_answers()
    {
        var answer = Choice("new1", "quality", "Quality", true);
        var optional = Choice("new2", "optional", "Optional detail", false);
        optional.FieldRequired = false;
        var page = new BrowserPageSnapshot { Controls = [answer, optional] };
        var receipt = new BrowserActionReceipt
        {
            Ok = true, Action = "click", TargetName = "4", TargetRole = "radio",
            TargetId = "old1", TargetFieldId = "quality", CheckedChanged = true,
        };
        var verifier = new BrowserAutomationVerifier();
        Assert.True(verifier.VerifyTerminal("Fill the survey", new() { Done = true }, page, [receipt]).Verified);
        receipt.TargetFieldId = "optional";
        Assert.False(verifier.VerifyTerminal("Fill the survey", new() { Done = true }, page, [receipt]).Verified);
    }

    [Fact]
    public void Planner_receives_question_identity_and_actual_required_state()
    {
        var control = Choice("g1c1", "quality", "Quality", false);
        var prompt = BrowserAutomationPlanner.BuildPrompt("Fill the survey", new(), [], [control]);
        Assert.Contains("fieldId=\"quality\"", prompt);
        Assert.Contains("field=\"Quality\" required=True answered=False", prompt);
        Assert.Contains("action.targetFieldId", prompt);
    }
}
