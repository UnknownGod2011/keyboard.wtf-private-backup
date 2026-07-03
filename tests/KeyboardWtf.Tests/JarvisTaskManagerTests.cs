namespace KeyboardWtf.Tests;

using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

public sealed class JarvisTaskManagerTests
{
    [Fact]
    public void TaskProgressCancellationAndReceiptAreTracked()
    {
        using var manager = new JarvisTaskManager();
        var task = manager.Create("Fill form", "Fill the mock form", ["Open page", "Fill fields", "Verify"]);

        manager.Start(task.TaskId);
        manager.SetStepByIndex(task.TaskId, 0, JarvisTaskStepStatus.Completed, "Page opened.");
        manager.AddReceipt(task.TaskId, "Opened the mock page.");
        var cancelled = manager.Cancel(task.TaskId, "Stopped by user.");

        Assert.Equal(JarvisTaskStatus.Cancelled, cancelled.Status);
        Assert.Contains("Opened the mock page.", cancelled.Receipt);
        Assert.Equal(JarvisTaskStepStatus.Completed, cancelled.Steps[0].Status);
        Assert.All(
            cancelled.Steps.Skip(1),
            step => Assert.Equal(JarvisTaskStepStatus.Cancelled, step.Status));
    }

    [Fact]
    public void FifthParallelTaskIsRejected()
    {
        using var manager = new JarvisTaskManager();
        for (var index = 0; index < JarvisTaskManager.MaxActiveTasks; index++)
            manager.Start(manager.Create($"Task {index}", $"Command {index}", ["Work"]).TaskId);

        Assert.Throws<JarvisTaskCapacityException>(() =>
            manager.Create("Task 5", "Too many", ["Work"]));
    }

    [Fact]
    public void WaitingForClarificationStaysActiveAndCanResume()
    {
        using var manager = new JarvisTaskManager();
        var task = manager.Create("Choose project", "Find my project", ["Find project"]);
        manager.Start(task.TaskId);

        var waiting = manager.WaitForClarification(
            task.TaskId,
            task.Steps[0].Id,
            "Which project?",
            ["keyboard.wtf", "Another project"]);
        var resumed = manager.ProvideClarification(task.TaskId, "keyboard.wtf");

        Assert.Equal(JarvisTaskStatus.WaitingForClarification, waiting.Status);
        Assert.Equal(JarvisTaskStepStatus.WaitingForClarification, waiting.Steps[0].Status);
        Assert.Equal(JarvisTaskStatus.Running, resumed.Status);
        Assert.Equal("keyboard.wtf", resumed.LastClarification);
    }

    [Fact]
    public async Task TaskChangedEventStreamsSnapshots()
    {
        using var manager = new JarvisTaskManager();
        var completion = new TaskCompletionSource<JarvisTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TaskChanged += task =>
        {
            if (task.Status == JarvisTaskStatus.Completed)
                completion.TrySetResult(task);
        };
        var task = manager.Create("Verify", "Verify event stream", ["Work"]);
        manager.Start(task.TaskId);

        manager.Complete(task.TaskId, "Verified.");
        var completed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("Verified.", completed.Result);
        Assert.Equal(JarvisTaskStatus.Completed, completed.Status);
    }

    [Fact]
    public void CompleteCancelsTaskTokenSoBackendsStopLooping()
    {
        using var manager = new JarvisTaskManager();
        var task = manager.Create("Play video", "Play oldest video", ["Open", "Verify"]);
        manager.Start(task.TaskId);
        var token = manager.TokenFor(task.TaskId);

        var completed = manager.Complete(task.TaskId, "Video opened.");

        Assert.Equal(JarvisTaskStatus.Completed, completed.Status);
        Assert.True(token.IsCancellationRequested);
        Assert.False(manager.IsRunnable(task.TaskId));
    }

    [Fact]
    public async Task TimeoutMarksTaskAndPendingStepsAsTimedOut()
    {
        using var manager = new JarvisTaskManager();
        var task = manager.Create("Slow browser task", "Wait too long", ["Open", "Inspect"], timeout: TimeSpan.FromMilliseconds(80));
        manager.Start(task.TaskId);
        manager.SetStepByIndex(task.TaskId, 0, JarvisTaskStepStatus.Running, "Opening.");

        await Task.Delay(250);
        var timedOut = manager.Get(task.TaskId);

        Assert.Equal(JarvisTaskStatus.TimedOut, timedOut.Status);
        Assert.All(
            timedOut.Steps,
            step => Assert.True(step.Status is JarvisTaskStepStatus.TimedOut or JarvisTaskStepStatus.Completed));
        Assert.Contains("timed out", timedOut.Result, StringComparison.OrdinalIgnoreCase);
    }
}
