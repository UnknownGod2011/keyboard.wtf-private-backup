using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PaidLiveStartupPolicyTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void Paid_jarvis_uses_local_speech_before_opening_a_provider_session(
        bool usesPaidBackend,
        bool isLiveActive,
        bool expectedLocalPreflight) =>
        Assert.Equal(
            expectedLocalPreflight,
            CommandRegistry.ShouldUseLocalJarvisPreflight(usesPaidBackend, isLiveActive));
}
