namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class JarvisPermissionPolicyTests
{
    [Theory]
    [InlineData("inspect_screen", "")]
    [InlineData("take_screenshot", "")]
    [InlineData("take_photo", "")]
    [InlineData("open_camera", "")]
    public void ExplicitPrivacyActionsAutoExecuteWhenModeAllowsIt(string tool, string action)
    {
        Assert.False(JarvisPermissionPolicy.RequiresConfirmation(
            tool,
            action,
            "",
            JarvisPermissionMode.AutoExecute));
    }

    [Theory]
    [InlineData("inspect_screen", "")]
    [InlineData("take_screenshot", "")]
    [InlineData("take_photo", "")]
    [InlineData("open_camera", "")]
    public void ExplicitPrivacyActionsAskInAlwaysAskMode(string tool, string action)
    {
        Assert.True(JarvisPermissionPolicy.RequiresConfirmation(
            tool,
            action,
            "",
            JarvisPermissionMode.AlwaysAsk));
    }

    [Fact]
    public void ClosingVirtualDesktopAlwaysAsks()
    {
        Assert.True(JarvisPermissionPolicy.RequiresConfirmation(
            "virtual_desktop_action",
            "close",
            "",
            JarvisPermissionMode.AutoExecute));
    }

    [Fact]
    public void WindowsRecordingAutoExecutes()
    {
        Assert.False(JarvisPermissionPolicy.RequiresConfirmation(
            "windows_recording_action",
            "toggle",
            "",
            JarvisPermissionMode.AutoExecute));
    }

    [Fact]
    public void NormalWindowCloseAutoExecutes()
    {
        Assert.False(JarvisPermissionPolicy.RequiresConfirmation(
            "window_action",
            "close",
            "",
            JarvisPermissionMode.AutoExecute));
    }

    [Fact]
    public void RoutineActionsAskInAlwaysAskMode()
    {
        Assert.True(JarvisPermissionPolicy.RequiresConfirmation(
            "open_app",
            "",
            "Notepad",
            JarvisPermissionMode.AlwaysAsk));
    }

    [Fact]
    public void ReadOnlySearchDoesNotAsk()
    {
        Assert.False(JarvisPermissionPolicy.RequiresConfirmation(
            "search_files",
            "",
            "",
            JarvisPermissionMode.AlwaysAsk));
    }
}
