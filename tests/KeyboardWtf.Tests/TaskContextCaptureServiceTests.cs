namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class TaskContextCaptureServiceTests
{
    [Fact]
    public async Task SelectedTextHasPriorityAndIsCompacted()
    {
        var service = new TaskContextCaptureService(
            _ => Task.FromResult("Project: keyboard.wtf\nRequirement: Fill the Devpost form"),
            () => "clipboard fallback",
            () => ("Document", "winword"));

        var result = await service.CaptureAsync(new TaskContextRequest
        {
            UserExplicitlyRequested = true,
            UseSelectedText = true,
            UseClipboard = true,
        }, CancellationToken.None);

        Assert.True(result.Captured);
        Assert.Equal(TaskContextSource.SelectedText, result.Source);
        Assert.Equal("keyboard.wtf", result.StructuredFields["Project"]);
        Assert.Contains("Devpost", result.Summary);
    }

    [Fact]
    public async Task PrivateTextIsNotReadWithoutExplicitRequest()
    {
        var selectedCalled = false;
        var service = new TaskContextCaptureService(
            _ =>
            {
                selectedCalled = true;
                return Task.FromResult("private");
            },
            () => "private clipboard",
            () => ("Window", "app"));

        var result = await service.CaptureAsync(new TaskContextRequest
        {
            UserExplicitlyRequested = false,
            UseSelectedText = true,
            UseClipboard = true,
        }, CancellationToken.None);

        Assert.False(result.Captured);
        Assert.False(selectedCalled);
        Assert.Contains("did not explicitly request", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LongContextIsBounded()
    {
        var result = TaskContextCaptureService.Compact(new string('x', TaskContextCaptureService.MaxContextChars + 500));

        Assert.True(result.Truncated);
        Assert.Equal(TaskContextCaptureService.MaxContextChars, result.Text.Length);
    }
}
