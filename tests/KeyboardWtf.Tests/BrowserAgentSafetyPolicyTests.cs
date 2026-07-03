namespace KeyboardWtf.Tests;

using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

public sealed class BrowserAgentSafetyPolicyTests
{
    [Fact]
    public void NormalFormFillIsAllowed()
    {
        var decision = BrowserAgentSafetyPolicy.Evaluate(new BrowserAgentActionRequest
        {
            Action = BrowserAgentActionKind.Fill,
            Role = "label",
            Target = "Project name",
            Value = "keyboard.wtf",
        });

        Assert.Equal(BrowserActionRisk.Safe, decision.Risk);
    }

    [Theory]
    [InlineData("Add to cart")]
    [InlineData("Submit application")]
    [InlineData("Publish video")]
    [InlineData("Delete account")]
    public void ExternalStateActionsRequireConfirmation(string target)
    {
        var decision = BrowserAgentSafetyPolicy.Evaluate(new BrowserAgentActionRequest
        {
            Action = BrowserAgentActionKind.Click,
            Role = "button",
            Target = target,
        });

        Assert.Equal(BrowserActionRisk.ConfirmationRequired, decision.Risk);
    }

    [Theory]
    [InlineData("Checkout")]
    [InlineData("Pay now")]
    [InlineData("Enter OTP")]
    [InlineData("Solve CAPTCHA")]
    public void ProtectedActionsAreManualOnly(string target)
    {
        var decision = BrowserAgentSafetyPolicy.Evaluate(new BrowserAgentActionRequest
        {
            Action = BrowserAgentActionKind.Click,
            Role = "button",
            Target = target,
        });

        Assert.Equal(BrowserActionRisk.ManualOnly, decision.Risk);
    }

    [Fact]
    public void UploadWithoutExplicitUserRequestNeedsConfirmation()
    {
        var file = Path.GetTempFileName();
        try
        {
            var decision = BrowserAgentSafetyPolicy.Evaluate(
                new BrowserAgentActionRequest
                {
                    Action = BrowserAgentActionKind.Upload,
                    Target = "Upload file",
                    FilePath = file,
                    ExplicitlyRequested = false,
                },
                "Fill this form");

            Assert.Equal(BrowserActionRisk.ConfirmationRequired, decision.Risk);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void DevpostReadOnlyExtractionIsNotMisclassifiedAsPosting()
    {
        var decision = BrowserAgentSafetyPolicy.Evaluate(new BrowserAgentActionRequest
        {
            Action = BrowserAgentActionKind.Extract,
            Target = "highest prize pool hackathon on Devpost",
        });

        Assert.Equal(BrowserActionRisk.Safe, decision.Risk);
    }
}
