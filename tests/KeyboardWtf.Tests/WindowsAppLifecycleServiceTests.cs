using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class WindowsAppLifecycleServiceTests
{
    [Fact]
    public void ParsesQuotedRegisteredUninstaller()
    {
        var ok = WindowsAppLifecycleService.TryParseCommand(
            "\"C:\\Users\\Example User\\AppData\\Local\\Programs\\keyboard.wtf\\unins000.exe\" /SILENT",
            out var executable,
            out var arguments);

        Assert.True(ok);
        Assert.Equal(
            "C:\\Users\\Example User\\AppData\\Local\\Programs\\keyboard.wtf\\unins000.exe",
            executable);
        Assert.Equal("/SILENT", arguments);
    }

    [Fact]
    public void ParsesUnquotedExecutableAndArguments()
    {
        var ok = WindowsAppLifecycleService.TryParseCommand(
            "C:\\Tools\\unins000.exe /LOG",
            out var executable,
            out var arguments);

        Assert.True(ok);
        Assert.Equal("C:\\Tools\\unins000.exe", executable);
        Assert.Equal("/LOG", arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"unterminated")]
    [InlineData("not-an-executable")]
    public void RejectsInvalidUninstallCommands(string command)
    {
        Assert.False(
            WindowsAppLifecycleService.TryParseCommand(
                command,
                out _,
                out _));
    }
}
