namespace KeyboardWtf.Tests;

using KeyboardWtf.Automation.Input;
using KeyboardWtf.Automation.Router;
using KeyboardWtf.Models;
using Xunit;

public sealed class InputControlLayerTests
{
    [Theory]
    [InlineData("Enter", InputKey.Enter)]
    [InlineData("Space", InputKey.Space)]
    [InlineData("Shift", InputKey.Shift)]
    [InlineData("Page Down", InputKey.PageDown)]
    public void ParsesSupportedSingleKeys(string value, InputKey expected)
    {
        Assert.True(InputKeyParser.TryParse(value, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParsesSafeShortcutChord()
    {
        Assert.True(InputChord.TryParse("Ctrl+L", out var chord));
        Assert.Equal("Ctrl+L", chord.Display);
    }

    [Fact]
    public void BlocksDangerousShortcut()
    {
        InputChord.TryParse("Alt+F4", out var chord);
        var decision = new InputSafetyPolicy().Evaluate(
            InputAction.ChordAction(chord),
            new InputSafetyContext { Kind = InputSafetyContextKind.ReadOnlyNavigation });

        Assert.Equal(InputRisk.Blocked, decision.Risk);
    }

    [Fact]
    public void AllowsAddToCartActivationAfterUpfrontCartApproval()
    {
        var focus = new BrowserCompanionFocusedElement
        {
            Exists = true,
            Role = "button",
            ControlType = "cart-action",
            Text = "Add to Cart",
            Enabled = true,
            EnterLikelySafe = true,
        };
        var decision = new InputSafetyPolicy().Evaluate(
            InputAction.Activate(InputKey.Enter, "Add to Cart"),
            new InputSafetyContext
            {
                Kind = InputSafetyContextKind.CartMutationApproved,
                FocusedElement = focus,
                UpfrontCartMutationApproved = true,
            });

        Assert.Equal(InputRisk.Safe, decision.Risk);
    }

    [Fact]
    public void BlocksSensitiveFocusedField()
    {
        var decision = new InputSafetyPolicy().Evaluate(
            InputAction.Type("123456"),
            new InputSafetyContext
            {
                Kind = InputSafetyContextKind.FormFillPreview,
                FocusedElement = new BrowserCompanionFocusedElement
                {
                    Exists = true,
                    Sensitive = true,
                    Role = "textbox",
                },
            });

        Assert.Equal(InputRisk.Blocked, decision.Risk);
    }

    [Fact]
    public void KeyboardNavigationScoringMatchesFocusedControlContext()
    {
        var focus = new BrowserCompanionFocusedElement
        {
            Exists = true,
            Role = "button",
            ControlType = "cart-action",
            Text = "Add to Cart",
            NearbyText = "Logitech MX Master product page",
            EnterLikelySafe = true,
        };

        Assert.True(KeyboardNavigationPlanner.ScoreFocus(focus, "Add to Cart") >= 0.82);
    }

    [Fact]
    public void RouterPrefersCompanionBeforeKeyboardFallback()
    {
        var strategy = new AutomationRouter().Choose(new AutomationContext
        {
            IsBrowserTask = true,
            BrowserCompanionAvailable = true,
            KeyboardFallbackAllowed = true,
            BrowserUseRepairEnabled = true,
        });

        Assert.Equal(AutomationStrategy.BrowserCompanionGraphAction, strategy);
    }
}
