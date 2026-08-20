using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PaidLiveStartupPolicyTests
{
    [Fact]
    public void Paid_jarvis_does_not_transcribe_a_separate_local_preflight_turn() =>
        Assert.False(CommandRegistry.UsesJarvisTranscriptPreflight);

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Paid_provider_close_does_not_end_the_logical_conversation(
        bool usesPaidBackend,
        bool logicalSessionActive,
        bool expected) =>
        Assert.Equal(
            expected,
            GeminiLiveConversationService.ShouldKeepLogicalSessionAfterProviderClose(
                usesPaidBackend,
                logicalSessionActive));

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(20, 4)]
    public void Paid_reconnect_backoff_is_bounded_and_exponential(
        int failures,
        int expectedSeconds) =>
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            GeminiLiveConversationService.PaidReconnectDelay(failures));

    [Fact]
    public void Provider_rollover_context_is_bounded_sanitized_and_keeps_recent_turns()
    {
        var entries = Enumerable.Range(1, 20)
            .Select(number => new GeminiLiveConversationService.ConversationContextEntry(
                number % 2 == 0 ? "Jarvis" : "untrusted-role",
                $"turn {number}\r\n" + new string('x', 700)))
            .ToArray();

        var context = GeminiLiveConversationService.BuildBoundedRolloverContext(entries);

        Assert.DoesNotContain("turn 1 ", context, StringComparison.Ordinal);
        Assert.Contains("turn 20", context, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', context);
        Assert.DoesNotContain("untrusted-role", context, StringComparison.Ordinal);
        Assert.True(context.Length <= 4000);
    }
}
