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
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(20, 2)]
    public void Paid_reconnect_backoff_is_bounded_and_exponential(
        int failures,
        int expectedSeconds) =>
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            GeminiLiveConversationService.PaidReconnectDelay(failures));

    [Theory]
    [InlineData(true, null, 10, "client_cancel")]
    [InlineData(false, System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, 30, "remote_normal_close")]
    [InlineData(false, System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, 600, "provider_connection_rollover")]
    public void Provider_close_diagnostics_distinguish_user_stop_from_rollover(
        bool clientCancelled,
        System.Net.WebSockets.WebSocketCloseStatus? closeStatus,
        int socketAgeSeconds,
        string expected) =>
        Assert.Equal(
            expected,
            GeminiLiveConversationService.ClassifyProviderSocketClose(
                clientCancelled,
                closeStatus,
                null,
                TimeSpan.FromSeconds(socketAgeSeconds)));

    [Fact]
    public void Intentional_safe_rollover_has_an_authoritative_diagnostic_category() =>
        Assert.Equal(
            "safe_physical_rollover",
            GeminiLiveConversationService.ClassifyProviderSocketClose(
                false,
                System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                null,
                TimeSpan.FromMinutes(9.5),
                intentionalRollover: true));

    [Fact]
    public void Physical_rollover_requires_a_completed_quiet_turn_without_tools()
    {
        Assert.True(GeminiLiveConversationService.IsSafePhysicalRollover(
            turnComplete: true,
            activeToolCalls: 0,
            bufferedPlaybackBytes: 0));
        Assert.False(GeminiLiveConversationService.IsSafePhysicalRollover(true, 1, 0));
        Assert.False(GeminiLiveConversationService.IsSafePhysicalRollover(true, 0, 960));
        Assert.False(GeminiLiveConversationService.IsSafePhysicalRollover(false, 0, 0));
    }

    [Theory]
    [InlineData(true, true, true, false, false, null)]
    [InlineData(false, false, true, true, false, null)]
    [InlineData(false, true, false, true, false, null)]
    [InlineData(false, true, true, false, false, null)]
    [InlineData(false, true, true, true, false, "soft_age_target")]
    [InlineData(false, true, true, false, true, "provider_go_away")]
    [InlineData(false, true, true, true, true, "provider_go_away")]
    public void Cancelled_or_stale_rollover_signal_cannot_mark_handoff_pending(
        bool cancellationRequested,
        bool expectedSessionIsCurrent,
        bool expectedSocketIsCurrent,
        bool softTargetCompletedSuccessfully,
        bool providerGoAwayCompletedSuccessfully,
        string expected) =>
        Assert.Equal(
            expected,
            GeminiLiveConversationService.ResolvePaidRolloverSignal(
                cancellationRequested,
                expectedSessionIsCurrent,
                expectedSocketIsCurrent,
                softTargetCompletedSuccessfully,
                providerGoAwayCompletedSuccessfully));

    [Fact]
    public void Session_resumption_handle_is_bounded_and_omitted_for_a_new_logical_session()
    {
        Assert.Empty(GeminiLiveConversationService.BuildSessionResumptionConfig(""));
        Assert.Equal(
            "resume-safe-123",
            GeminiLiveConversationService.BuildSessionResumptionConfig(" resume-safe-123 ")["handle"]);
        Assert.Empty(GeminiLiveConversationService.BuildSessionResumptionConfig(
            new string('x', 9000)));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void Account_refresh_after_stop_requires_a_real_paid_provider_socket(
        bool paid,
        bool signedIn,
        bool hadProviderSocket,
        bool expected) =>
        Assert.Equal(
            expected,
            GeminiLiveConversationService.ShouldRefreshAccountAfterStop(
                paid,
                signedIn,
                hadProviderSocket));

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
