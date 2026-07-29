namespace KeyboardWtf.Services;

using System.Diagnostics;
using Microsoft.Win32;

internal static class WindowsAppLifecycleService
{
    private const string UninstallRegistryRoot =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    internal static void WaitForRestartParent(string[] args)
    {
        var index = Array.FindIndex(
            args ?? Array.Empty<string>(),
            value => string.Equals(value, "--restart-after", StringComparison.OrdinalIgnoreCase));
        if (index < 0
            || index + 1 >= args.Length
            || !int.TryParse(args[index + 1], out var parentPid)
            || parentPid <= 0
            || parentPid == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit(15_000);
        }
        catch (ArgumentException)
        {
            // The previous instance already exited.
        }
        catch (InvalidOperationException)
        {
            // The previous instance already exited.
        }
    }

    internal static bool StartRestartHelper(out string error)
    {
        error = "";
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                error = "The running keyboard.wtf executable could not be found.";
                return false;
            }

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("--restart-after");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool LaunchRegisteredUninstaller(
        bool removeUserData,
        out string error)
    {
        error = "";
        try
        {
            var uninstallString = FindRegisteredUninstaller();
            if (string.IsNullOrWhiteSpace(uninstallString))
            {
                error = "The registered keyboard.wtf uninstaller was not found.";
                return false;
            }

            if (!TryParseCommand(uninstallString, out var executable, out var arguments)
                || !File.Exists(executable))
            {
                error = "The registered keyboard.wtf uninstaller is unavailable.";
                return false;
            }

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Arguments = arguments,
            };
            if (removeUserData)
            {
                start.Arguments = string.IsNullOrWhiteSpace(start.Arguments)
                    ? "/REMOVEUSERDATA"
                    : $"{start.Arguments} /REMOVEUSERDATA";
            }
            Process.Start(start);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static string FindRegisteredUninstaller()
    {
        using var root = Registry.CurrentUser.OpenSubKey(UninstallRegistryRoot);
        if (root == null)
            return "";

        foreach (var name in root.GetSubKeyNames())
        {
            using var app = root.OpenSubKey(name);
            var displayName = app?.GetValue("DisplayName") as string;
            if (!string.Equals(displayName, "keyboard.wtf", StringComparison.OrdinalIgnoreCase))
                continue;

            return app?.GetValue("UninstallString") as string ?? "";
        }

        return "";
    }

    internal static bool TryParseCommand(
        string command,
        out string executable,
        out string arguments)
    {
        executable = "";
        arguments = "";
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var value = command.Trim();
        if (value[0] == '"')
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote <= 1)
                return false;
            executable = value[1..closingQuote];
            arguments = value[(closingQuote + 1)..].Trim();
            return true;
        }

        if (File.Exists(value))
        {
            executable = value;
            return true;
        }

        var exeEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd < 0)
            return false;
        exeEnd += 4;
        executable = value[..exeEnd].Trim();
        arguments = value[exeEnd..].Trim();
        return true;
    }
}
