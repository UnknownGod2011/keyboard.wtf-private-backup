namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class JarvisTimerServiceTests
{
    [Fact]
    public async Task Timer_completes_once_notifies_and_leaves_no_active_runtime()
    {
        var notifications = new List<string>();
        var spoken = new List<string>();
        var completed = new TaskCompletionSource<JarvisTimerSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new JarvisTimerService(
            (title, message) => notifications.Add($"{title}:{message}"),
            message => spoken.Add(message),
            (_, _, _) => { });
        service.Changed += snapshot =>
        {
            if (snapshot.State == JarvisTimerState.Completed)
                completed.TrySetResult(snapshot);
        };

        service.Start(TimeSpan.FromMilliseconds(120), "short test");
        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(JarvisTimerState.Completed, result.State);
        Assert.Empty(service.ActiveTimers);
        Assert.Single(notifications);
        Assert.Single(spoken);
    }

    [Fact]
    public async Task Cancel_is_idempotent_and_prevents_late_completion()
    {
        var terminalStates = new List<JarvisTimerState>();
        using var service = new JarvisTimerService(
            (_, _) => { },
            _ => { },
            (_, _, _) => { });
        service.Changed += snapshot =>
        {
            if (snapshot.State != JarvisTimerState.Running)
                terminalStates.Add(snapshot.State);
        };

        var timer = service.Start(TimeSpan.FromMilliseconds(180), "cancel me");
        Assert.Equal(1, service.Cancel(timer.Id));
        Assert.Equal(0, service.Cancel(timer.Id));
        await Task.Delay(350);

        Assert.Equal(new[] { JarvisTimerState.Cancelled }, terminalStates);
        Assert.Empty(service.ActiveTimers);
    }
}
