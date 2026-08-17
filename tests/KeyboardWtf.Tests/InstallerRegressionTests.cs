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

    [Fact]
    public void Uninstall_removes_only_the_native_host_owned_by_that_installation()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "installer", "keyboard-wtf.iss"));

        Assert.Contains("procedure UnregisterOwnedNativeHost", installer, StringComparison.Ordinal);
        Assert.Contains("CompareText(RegisteredManifest, OwnedManifest) <> 0", installer, StringComparison.Ordinal);
        Assert.Contains("RegDeleteKeyIncludingSubkeys(HKCU, NativeHostRegistryPath)", installer, StringComparison.Ordinal);
        Assert.Contains("if CurUninstallStep = usUninstall then", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_stops_only_processes_running_from_that_installation()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "installer", "keyboard-wtf.iss"));

        Assert.Contains("procedure StopOwnedProcessesForUninstall", installer, StringComparison.Ordinal);
        Assert.Contains("TargetExecutable := ExpandConstant('{app}\\{#MyAppExeName}')", installer, StringComparison.Ordinal);
        Assert.Contains("[IO.Path]::GetFullPath($_.Path) -eq $target", installer, StringComparison.Ordinal);
        Assert.Contains("StopOwnedProcessesForUninstall();", installer, StringComparison.Ordinal);
        Assert.DoesNotContain("taskkill", installer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Paid_production_candidate_does_not_register_for_startup()
    {
        var root = FindRepositoryRoot();
        var buildScript = File.ReadAllText(Path.Combine(root, "scripts", "build-windows-release.ps1"));
        var compilerArgumentsStart = buildScript.IndexOf("$isccArguments = @()", StringComparison.Ordinal);
        var candidateBlockStart = buildScript.IndexOf(
            "if ($PaidProductionCandidate)",
            compilerArgumentsStart,
            StringComparison.Ordinal);
        var compilerStart = buildScript.IndexOf("& $iscc", candidateBlockStart, StringComparison.Ordinal);

        Assert.True(candidateBlockStart >= 0 && compilerStart > candidateBlockStart);
        Assert.Contains("'/DMySkipStartup'", buildScript[candidateBlockStart..compilerStart], StringComparison.Ordinal);
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
