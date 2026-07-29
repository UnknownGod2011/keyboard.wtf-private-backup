using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class StartupExecutablePathTests
{
    [Fact]
    public void Source_tree_candidate_does_not_replace_installed_startup()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"keyboard-wtf-startup-{Guid.NewGuid():N}");
        var current = Path.Combine(root, "artifacts", "validation", "keyboard.wtf.exe");
        var installed = Path.Combine(root, "installed", "keyboard.wtf.exe");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
            File.WriteAllText(current, "");
            File.WriteAllText(installed, "");

            Assert.Equal(
                installed,
                KeyboardWtfApp.ResolveStartupExecutablePath(current, installed));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Installed_candidate_keeps_its_own_path()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"keyboard-wtf-installed-{Guid.NewGuid():N}");
        var current = Path.Combine(root, "keyboard.wtf.exe");

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(current, "");

            Assert.Equal(
                current,
                KeyboardWtfApp.ResolveStartupExecutablePath(current, current));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
