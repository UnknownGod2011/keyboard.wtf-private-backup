using System.Net;
using KeyboardWtf.Services.BrowserAutomationV2;
using KeyboardWtf.Services.PaidStaging;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class BrowserPaidFailureTests
{
    [Fact]
    public void Credit_refusal_is_not_misrepresented_as_an_unexpected_browser_error()
    {
        var message = BrowserAutomationCoordinator.FriendlyError(new PaidApiException(HttpStatusCode.PaymentRequired, "provider detail"));
        Assert.Contains("usable credits", message);
        Assert.Contains("Account & Billing", message);
        Assert.DoesNotContain("unexpected", message);
        Assert.DoesNotContain("provider detail", message);
    }
}
