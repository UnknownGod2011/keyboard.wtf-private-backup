namespace KeyboardWtf.Tests;

using Xunit;

public sealed class InstallerRegressionTests
{
    [Fact]
    public void Upgrade_pauses_and_restores_the_existing_native_host_registration()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "installer", "keyboard-wtf.iss"));

        Assert.Contains("function PrepareToInstall", installer, StringComparison.Ordinal);
        Assert.Contains("RegDeleteValue(HKCU, NativeHostRegistryPath, '')", installer, StringComparison.Ordinal);
        Assert.Contains("procedure CurStepChanged", installer, StringComparison.Ordinal);
        Assert.Contains("procedure DeinitializeSetup", installer, StringComparison.Ordinal);
        Assert.Contains("RestoreNativeHostRegistration", installer, StringComparison.Ordinal);
        Assert.Contains("RegWriteStringValue", installer, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KeyboardWtf.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the keyboard.wtf repository root.");
    }
}
