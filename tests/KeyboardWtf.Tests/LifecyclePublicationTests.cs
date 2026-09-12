using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KeyboardWtf.Services;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class LifecyclePublicationTests
{
    [Theory]
    [InlineData("Fail", "cancelled")]
    [InlineData("Complete", "cancelled")]
    [InlineData("Fail", "completed")]
    [InlineData("Complete", "completed")]
    [InlineData("Fail", "replaced")]
    [InlineData("Complete", "replaced")]
    public void Late_terminal_callback_cannot_publish_notifications_or_change_a_finished_task(string method, string ending)
    {
        var notices = 0;
        var owner = new BrowserAutomationCoordinator(null,
            NotificationService.CreateForTesting((_, _, _) => notices++), null, null);
        var type = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", BindingFlags.NonPublic)!;
        var runtime = Activator.CreateInstance(type, nonPublic: true)!;
        var originalState = ending == "cancelled" ? BrowserTaskState.Cancelled
            : ending == "completed" ? BrowserTaskState.Completed : BrowserTaskState.Executing;
        type.GetProperty("State")!.SetValue(runtime, originalState);
        typeof(BrowserAutomationCoordinator).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(owner, ending == "replaced" ? null : runtime);
        if (ending == "cancelled")
        {
            type.GetProperty("State")!.SetValue(runtime, BrowserTaskState.Partial);
            Assert.True(owner.RequestCancel(announceOutcome: false));
        }
        var announcements = 0;
        owner.AnnouncementRequested += _ => announcements++;
        var callback = typeof(BrowserAutomationCoordinator).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!;
        callback.Invoke(owner, method == "Complete"
            ? new[] { runtime, BrowserAutomationOutcome.Completed, "Late success", "Late evidence" }
            : new[] { runtime, (object)BrowserAutomationOutcome.Failed, "Late failure" });
        Assert.Equal(originalState, type.GetProperty("State")!.GetValue(runtime));
        Assert.Equal(0, notices);
        Assert.Equal(0, announcements);
        ((CancellationTokenSource)type.GetProperty("Cancellation")!.GetValue(runtime)!).Dispose();
    }

    [Theory]
    [InlineData("state-changed")]
    [InlineData("task-replaced")]
    [InlineData("intervention-replaced")]
    public void Browser_notice_keeps_its_original_task_state_and_intervention_authority(string change)
    {
        var owner = new BrowserAutomationCoordinator(null, null, null, null);
        var type = typeof(BrowserAutomationCoordinator).GetNestedType("Runtime", BindingFlags.NonPublic)!;
        var runtime = Activator.CreateInstance(type, nonPublic: true)!;
        var active = typeof(BrowserAutomationCoordinator).GetField("_active", BindingFlags.NonPublic | BindingFlags.Instance)!;
        active.SetValue(owner, runtime);
        type.GetProperty("State")!.SetValue(runtime, BrowserTaskState.NeedsUserAction);
        type.GetProperty("Intervention")!.SetValue(runtime, new BrowserUserIntervention { Missing = "Email", Required = true });
        JarvisLifecycleAnnouncement notice = null;
        owner.AnnouncementRequested += value => notice = value;
        typeof(BrowserAutomationCoordinator).GetMethod("AnnounceOutcome", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(owner, new[] { runtime, (object)BrowserTaskState.NeedsUserAction });
        Assert.NotNull(notice);
        Assert.True(notice.CanPublish);
        Assert.Empty(notice.FallbackSpeech); // No independent TTS owner after goodbye.
        if (change == "state-changed") type.GetProperty("State")!.SetValue(runtime, BrowserTaskState.Cancelled);
        if (change == "task-replaced") active.SetValue(owner, null);
        if (change == "intervention-replaced") type.GetProperty("Intervention")!.SetValue(runtime, new BrowserUserIntervention());
        Assert.False(notice.CanPublish);
        ((CancellationTokenSource)type.GetProperty("Cancellation")!.GetValue(runtime)!).Dispose();
    }

    [Fact]
    public async Task Current_notice_publishes_once_on_its_originating_socket()
    {
        await using var run = await Connection.CreateAsync();
        Assert.True(await run.Live.TryAnnounceLifecycleAsync(Notice()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bytes = new byte[8192];
        var received = await run.Server.ReceiveAsync(bytes, timeout.Token);
        var text = Encoding.UTF8.GetString(bytes, 0, received.Count);
        Assert.Contains("KEYBOARD.WTF_BACKGROUND_LIFECYCLE_EVENT", text);
        Assert.Contains("Captured test task failed", text);
    }

    [Theory]
    [InlineData("new-user-turn")]
    [InlineData("task-invalidated")]
    [InlineData("socket-replaced")]
    [InlineData("session-ended")]
    public async Task Waiting_notice_cannot_outlive_its_authority(string change)
    {
        await using var run = await Connection.CreateAsync();
        var valid = true;
        var sendLock = run.Field<SemaphoreSlim>("_sendLock");
        await sendLock.WaitAsync();
        var oldTurn = run.Field<TaskCompletionSource<bool>>("_turnComplete");
        var delivery = run.Live.TryAnnounceLifecycleAsync(Notice(() => valid));
        Assert.False(delivery.IsCompleted);
        Assert.Same(oldTurn, run.Field<TaskCompletionSource<bool>>("_turnComplete"));
        switch (change)
        {
            case "new-user-turn": run.Set("_userTranscriptRevision", 1L); break;
            case "task-invalidated": valid = false; break;
            case "socket-replaced": run.Set("_socket", null); break;
            case "session-ended": run.Session.Cancel(); break;
        }
        sendLock.Release();
        Assert.False(await delivery.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(oldTurn, run.Field<TaskCompletionSource<bool>>("_turnComplete"));
        Assert.True(oldTurn.Task.IsCompletedSuccessfully);
        Assert.False(run.HasIncomingBytes);
    }

    [Fact]
    public async Task Background_notice_does_not_break_an_unfinished_user_turn_after_grace_expires()
    {
        await using var run = await Connection.CreateAsync();
        var pendingTurn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        run.Set("_turnComplete", pendingTurn);
        Assert.False(await run.Live.TryAnnounceLifecycleAsync(Notice()).WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(pendingTurn.Task.IsCompleted);
        Assert.False(run.HasIncomingBytes);
    }

    private static JarvisLifecycleAnnouncement Notice(Func<bool> valid = null) => new()
    {
        Source = "browser_automation", State = "Failed", Detail = "Captured test task failed",
        IsCurrent = valid,
    };

    // A real loopback WebSocket transport, with no provider, auth, microphone,
    // billing, or app launch. TcpListener port 0 avoids shared-port test races.
    private sealed class Connection : IAsyncDisposable
    {
        public GeminiLiveConversationService Live { get; } = new(null, null, null);
        public CancellationTokenSource Session { get; } = new();
        public WebSocket Server { get; private set; }
        private readonly ClientWebSocket _client = new();
        private TcpClient _peer;
        public bool HasIncomingBytes => _peer.Available > 0;

        public static async Task<Connection> CreateAsync()
        {
            var run = new Connection();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                var connecting = run._client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), timeout.Token);
                run._peer = await listener.AcceptTcpClientAsync(timeout.Token);
                var stream = run._peer.GetStream();
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (header.Length >= 8192 || await stream.ReadAsync(one, timeout.Token) != 1)
                        throw new IOException("Incomplete loopback WebSocket handshake.");
                    header.Append((char)one[0]);
                }
                var key = header.ToString().Split("\r\n")
                    .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    .Split(':', 2)[1].Trim();
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), timeout.Token);
                run.Server = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
                await connecting;
                run.Set("_socket", run._client);
                run.Set("_sessionCts", run.Session);
                run.Set("_paidLiveReady", true);
                return run;
            }
            catch
            {
                await run.DisposeAsync();
                throw;
            }
        }

        public T Field<T>(string name) => (T)typeof(GeminiLiveConversationService)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Live);
        public void Set(string name, object value) => typeof(GeminiLiveConversationService)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Live, value);

        public ValueTask DisposeAsync()
        {
            Session.Cancel();
            // Prevent the app shutdown routine from waiting for a mock provider close.
            Set("_socket", null);
            _client.Dispose();
            Server?.Dispose();
            _peer?.Dispose();
            Live.Dispose();
            Session.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
