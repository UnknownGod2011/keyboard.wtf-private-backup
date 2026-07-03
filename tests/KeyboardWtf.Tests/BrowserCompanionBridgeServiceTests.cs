namespace KeyboardWtf.Tests;

using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

public sealed class BrowserCompanionBridgeServiceTests
{
    [Fact]
    public async Task QueuesCommandAndCompletesWithGraph()
    {
        var bridge = new BrowserCompanionBridgeService();
        bridge.Register("session-1", "0.1.0");

        var pending = bridge.ExecuteAsync(
            new BrowserCompanionCommand
            {
                TaskId = "task-1",
                Type = "getGraph",
            },
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

        Assert.Null(bridge.NextCommand("other-session"));

        var command = bridge.NextCommand("session-1");
        Assert.NotNull(command);
        Assert.Equal("getGraph", command.Type);
        Assert.Equal("session-1", command.SessionId);

        bridge.Complete(new BrowserCompanionCommandResult
        {
            CommandId = command.CommandId,
            SessionId = "other-session",
            TaskId = command.TaskId,
            Ok = true,
            Status = "completed",
        });

        Assert.False(pending.IsCompleted);

        bridge.Complete(new BrowserCompanionCommandResult
        {
            CommandId = command.CommandId,
            SessionId = command.SessionId,
            TaskId = command.TaskId,
            Ok = true,
            Status = "completed",
            Message = "Captured controls.",
            Graph = new BrowserCompanionControlGraph
            {
                Url = "https://www.youtube.com/@MrBeast/videos",
                Title = "MrBeast - Videos",
                PageType = "youtube_channel_videos",
                Controls =
                [
                    new BrowserCompanionControl
                    {
                        ControlId = "kc_1",
                        Role = "button",
                        Text = "Oldest",
                        Enabled = true,
                        Visible = true,
                        ConfidenceScore = 0.95,
                    },
                ],
            },
        });

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(result.Ok);
        Assert.Equal("youtube_channel_videos", result.Graph.PageType);
        Assert.Equal("Oldest", bridge.LastGraph.Controls[0].Text);
    }

    [Fact]
    public async Task CommandTimesOutCleanlyWhenExtensionDoesNotRespond()
    {
        var bridge = new BrowserCompanionBridgeService();
        bridge.Register("session-1", "0.1.0");

        var result = await bridge.ExecuteAsync(
            new BrowserCompanionCommand { Type = "getGraph" },
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("timed_out", result.Status);
    }

    [Fact]
    public void DetailedStatusIncludesConnectionAndGraphMetadata()
    {
        var bridge = new BrowserCompanionBridgeService();
        bridge.Register(
            "session-1",
            "0.1.5",
            contentScriptVersion: "0.1.5",
            browserName: "Chrome",
            activeTabUrl: "https://www.youtube.com/");
        bridge.SubmitGraph(new BrowserCompanionControlGraph
        {
            SessionId = "session-1",
            Url = "https://www.youtube.com/",
            Title = "YouTube",
            ExtensionVersion = "0.1.5",
            ContentScriptVersion = "0.1.5",
            BrowserName = "Chrome",
            Controls =
            [
                new BrowserCompanionControl
                {
                    ControlId = "kc_1",
                    ControlType = "search",
                    Role = "searchbox",
                    Text = "Search",
                    Visible = true,
                    Enabled = true,
                },
            ],
        });

        var status = bridge.DetailedStatus(
            bridgePort: 8090,
            browserUseFallbackEnabled: false,
            automationProfileFallbackEnabled: false);

        Assert.True(status.Available);
        Assert.Equal("Connected", status.ConnectionState);
        Assert.Equal("0.1.5", status.ExtensionVersion);
        Assert.Equal("0.1.5", status.ContentScriptVersion);
        Assert.Equal("Chrome", status.BrowserName);
        Assert.Equal("https://www.youtube.com/", status.ActiveTabUrl);
        Assert.Equal("http://localhost:8090", status.BridgeUrl);
        Assert.Equal(1, status.LastControlCount);
        Assert.Contains(status.TopVisibleControls, value => value.Contains("Search", StringComparison.OrdinalIgnoreCase));
    }
}
