using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserFinalFieldStateTests
{
    private readonly BrowserAutomationVerifier _verifier = new();

    private static BrowserControl Field(string id, string role, string name, string value) => new()
    {
        Id = id, FieldId = id, Role = role, AccessibleName = name, Value = value,
        Enabled = true, Visible = true,
    };

    private static BrowserPlannerAction Check(BrowserControl field, string type, string value = "") => new()
    {
        Type = type, TargetId = field.Id, TargetFieldId = field.FieldId,
        TargetRole = field.Role, TargetName = field.AccessibleName, Text = value, Option = value,
    };

    private BrowserVerificationResult Verify(string request, BrowserControl[] fields,
        BrowserPlannerAction[] checks, BrowserActionReceipt[] receipts = null) =>
        _verifier.VerifyTerminal(request, new() { Done = true, CompletionChecks = checks },
            new() { TabId = 1, Controls = fields }, receipts ?? []);

    [Fact]
    public void Native_select_label_is_distinct_from_value_and_action_words_in_labels_are_not_goals()
    {
        var name = Field("name", "textbox", "Text input", "Boundary Alpha");
        var notes = Field("notes", "textbox", "Textarea", "Review notes");
        var choice = Field("choice", "combobox", "Dropdown (select) Open this select menu One Two Three", "2");
        choice.SelectedText = "Two";
        var enabled = Field("enabled", "checkbox", "Default checkbox", "on");
        enabled.Checked = true;
        var result = Verify("Set Text input to Boundary Alpha. Set Textarea to Review notes. Select Two in Dropdown (select). Check Default checkbox.",
            [name, notes, choice, enabled], [Check(name, "type", name.Value), Check(notes, "type", notes.Value),
                Check(choice, "select_option", "Two"), Check(enabled, "check")]);
        Assert.True(result.Verified, result.Error);
    }

    [Fact]
    public void Available_but_unselected_option_cannot_prove_completion()
    {
        var field = Field("choice", "combobox", "Options Alpha Beta Gamma", "1");
        field.SelectedText = "Alpha";
        Assert.False(Verify("Select Beta", [field], [Check(field, "select_option", "Beta")]).Verified);
    }

    [Fact]
    public void One_verb_with_multiple_fields_still_requires_every_declared_final_state()
    {
        var name = Field("name", "textbox", "Name", "N");
        var email = Field("email", "textbox", "Email", "");
        Assert.False(Verify("Fill Name and Email with the supplied values", [name, email],
            [Check(name, "type", "N"), Check(email, "type", "user@example.test")]).Verified);
    }

    [Fact]
    public void Already_correct_fields_and_authorized_empty_values_do_not_require_duplicate_writes()
    {
        var notes = Field("notes", "textbox", "Notes", "");
        var flag = Field("flag", "checkbox", "Updates", "on");
        Assert.True(Verify("Clear Notes and uncheck Updates", [notes, flag],
            [Check(notes, "type", ""), Check(flag, "uncheck")]).Verified);
    }

    [Fact]
    public void Targeted_multiple_field_edits_do_not_require_unrelated_required_values()
    {
        var a = Field("a", "textbox", "A", "a");
        var b = Field("b", "textbox", "B", "b");
        var other = Field("other", "textbox", "Other", "");
        other.FieldRequired = true;
        Assert.True(Verify("Set A to a and set B to b; leave everything else alone", [a, b, other],
            [Check(a, "type", "a"), Check(b, "type", "b")]).Verified);
    }

    [Fact]
    public void Stale_or_ambiguous_control_identity_is_not_a_terminal_success()
    {
        var a = Field("a", "checkbox", "Repeated", "on");
        var b = Field("b", "checkbox", "Repeated", "on");
        a.Checked = true;
        var check = Check(b, "check");
        Assert.False(Verify("Check the second choice", [a, b], [check]).Verified);
        check.TargetId = "old";
        check.TargetFieldId = "missing";
        Assert.False(Verify("Check the second choice", [a, b], [check]).Verified);
    }

    [Fact]
    public void Explicit_current_page_checks_do_not_require_fields_from_an_earlier_tab()
    {
        var current = Field("current", "textbox", "Name", "Correct");
        var old = new BrowserActionReceipt { Ok = true, Action = "type", TabId = 2,
            TargetFieldId = "old", TargetRole = "textbox", TargetName = "Name",
            ExpectedValue = "Old", ValueMatches = true };
        Assert.True(Verify("Continue here with the corrected Name", [current],
            [Check(current, "type", "Correct")], [old]).Verified);
    }

    [Fact]
    public void Missing_earlier_field_on_the_same_page_cannot_be_hidden_by_a_last_field_check()
    {
        var current = Field("current", "textbox", "Last", "Correct");
        var earlier = new BrowserActionReceipt { Ok = true, Action = "type", TabId = 1,
            TargetFieldId = "earlier", TargetRole = "textbox", TargetName = "First",
            ExpectedValue = "Lost", ValueMatches = true };
        Assert.False(Verify("Fill both fields", [current],
            [Check(current, "type", "Correct")], [earlier]).Verified);
    }

    [Fact]
    public void Correction_replaces_previous_value_but_not_other_fields()
    {
        var current = Field("current", "textbox", "Name", "Correct");
        var previous = new BrowserActionReceipt { Ok = true, Action = "type", TabId = 1,
            TargetFieldId = current.FieldId, TargetRole = current.Role, TargetName = current.AccessibleName,
            ExpectedValue = "Old", ValueMatches = true };
        Assert.True(Verify("Use the corrected Name", [current], [Check(current, "type", "Correct")], [previous]).Verified);
    }

    [Fact]
    public void Unnamed_exact_field_can_be_verified_but_unnamed_group_cannot_guess_a_member()
    {
        var field = Field("exact", "textbox", "", "Known");
        Assert.True(Verify("Use the supplied text", [field], [Check(field, "type", "Known")]).Verified);
        var a = Field("a", "checkbox", "", "on");
        var b = Field("b", "checkbox", "", "on");
        b.FieldId = a.FieldId;
        var ambiguous = Check(a, "uncheck");
        ambiguous.TargetId = "";
        Assert.False(Verify("Uncheck the requested choice", [a, b], [ambiguous]).Verified);
    }

    [Fact]
    public void Checked_state_cannot_be_asserted_on_a_text_field()
    {
        var field = Field("exact", "textbox", "Note", "Known");
        Assert.False(Verify("Change the note", [field], [Check(field, "uncheck")]).Verified);
    }
}
