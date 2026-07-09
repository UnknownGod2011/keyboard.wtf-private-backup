namespace KeyboardWtf.Services;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyboardWtf.Helpers;

public sealed class BrowserUseAutomationProvider : IBrowserAutomationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly SettingsService _settings;
    private readonly BrowserAutomationMemoryStore _memory;

    public BrowserUseAutomationProvider(SettingsService settings, BrowserAutomationMemoryStore memory)
    {
        _settings = settings;
        _memory = memory;
    }

    public static BrowserAutomationAvailability GetAvailability(bool enabled)
    {
        if (!enabled)
        {
            return new BrowserAutomationAvailability
            {
                Ready = false,
                Status = "disabled",
                Message = "Experimental smart browser automation is disabled.",
            };
        }

        var python = LocatePython();
        if (string.IsNullOrWhiteSpace(python)
            || (Path.IsPathFullyQualified(python) && !File.Exists(python)))
        {
            return new BrowserAutomationAvailability
            {
                Ready = false,
                Status = "missing_runtime",
                Message = "Python runtime for browser-use was not found.",
            };
        }

        var script = LocateRuntimeScript();
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
        {
            return new BrowserAutomationAvailability
            {
                Ready = false,
                Status = "missing_sidecar",
                Message = "Browser-use sidecar script was not found in the build.",
            };
        }

        var envPath = LocateBrowserEnvFile();
        if (!HasGeminiBrowserKey(envPath))
        {
            return new BrowserAutomationAvailability
            {
                Ready = false,
                Status = "api_key_missing",
                Message = "Browser automation Gemini key was not found in the ignored local env file or process environment.",
            };
        }

        return new BrowserAutomationAvailability
        {
            Ready = true,
            Status = "ready",
            Message = "browser-use sidecar and Gemini key pool are available.",
        };
    }

    public async Task<BrowserAutomationResult> ExecuteAsync(BrowserAutomationRequest request, CancellationToken token)
    {
        if (_settings?.Current.ExperimentalSmartBrowserAutomation != true)
        {
            return new BrowserAutomationResult
            {
                Success = false,
                FailureReason = "Experimental smart browser automation is disabled in Settings.",
                Message = "Experimental smart browser automation is disabled in Settings.",
                Profile = request.Profile,
                Model = request.Model,
            };
        }

        var python = LocatePython();
        var script = LocateRuntimeScript();
        if (string.IsNullOrWhiteSpace(python)
            || (Path.IsPathFullyQualified(python) && !File.Exists(python)))
            return Failure(request, "Browser-use Python runtime was not found. Set KEYBOARD_WTF_BROWSER_USE_PYTHON or run the bake-off setup.");
        if (string.IsNullOrWhiteSpace(script) || !File.Exists(script))
            return Failure(request, "Browser-use runtime script was not found.");

        Directory.CreateDirectory(Path.Combine(SettingsService.AppDataDir, "browser-automation", "requests"));
        Directory.CreateDirectory(Path.Combine(SettingsService.AppDataDir, "browser-automation", "results"));

        var id = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(SettingsService.AppDataDir, "browser-automation", "requests", $"{id}.json");
        var resultPath = Path.Combine(SettingsService.AppDataDir, "browser-automation", "results", $"{id}.json");
        var envPath = LocateBrowserEnvFile();

        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, JsonOptions), token);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script) ?? AppContext.BaseDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.ArgumentList.Add("--request");
        process.StartInfo.ArgumentList.Add(requestPath);
        process.StartInfo.ArgumentList.Add("--output");
        process.StartInfo.ArgumentList.Add(resultPath);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            process.StartInfo.ArgumentList.Add("--env");
            process.StartInfo.ArgumentList.Add(envPath);
        }
        process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        process.StartInfo.Environment["BROWSER_USE_MODEL"] = string.IsNullOrWhiteSpace(request.Model)
            ? (_settings?.Current.BrowserAutomationModel ?? "gemini-3.1-flash-lite")
            : request.Model;

        var start = DateTimeOffset.UtcNow;
        try
        {
            AppLog.Info($"Starting browser-use sidecar: profile={request.Profile}, model={process.StartInfo.Environment["BROWSER_USE_MODEL"]}, targetSite={request.TargetSite}, taskType={request.TaskType}");
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);
            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(request.MaxRuntimeMs + 15_000, 30_000, 240_000));
            var exitTask = process.WaitForExitAsync(token);
            var delayTask = Task.Delay(timeout, token);
            var completed = await Task.WhenAny(exitTask, delayTask);
            if (!ReferenceEquals(completed, exitTask) && !process.HasExited)
            {
                TryKill(process);
                return Failure(request, $"Browser automation timed out after {(int)timeout.TotalSeconds}s.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (File.Exists(resultPath))
            {
                var json = await File.ReadAllTextAsync(resultPath, token);
                var result = JsonSerializer.Deserialize<BrowserAutomationResult>(json, JsonOptions)
                    ?? Failure(request, "Browser automation returned an unreadable result.");
                if (result.Success)
                    _memory?.RecordSuccess(request, result);
                AppLog.Info($"Browser-use sidecar completed: success={result.Success}, runtimeMs={result.RuntimeMs}, finalUrl={result.FinalUrl}, reason={Limit(result.FailureReason ?? "", 120)}");
                return result;
            }

            var error = Redact($"{stdout}\n{stderr}");
            return Failure(request, string.IsNullOrWhiteSpace(error)
                ? "Browser automation exited without a result."
                : Limit(error, 800));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return Failure(request, "Browser automation was cancelled.");
        }
        catch (Exception ex)
        {
            TryKill(process);
            AppLog.Warning(ex, "Browser-use provider failed");
            return Failure(request, Redact(ex.Message));
        }
        finally
        {
            _ = start;
        }
    }

    private static BrowserAutomationResult Failure(BrowserAutomationRequest request, string reason) => new()
    {
        Success = false,
        FailureReason = reason,
        Message = reason,
        Profile = request.Profile,
        Model = request.Model,
    };

    private static string LocatePython()
    {
        var configured = Environment.GetEnvironmentVariable("KEYBOARD_WTF_BROWSER_USE_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var repoPython = FindUpwards(Path.Combine("tools", "browser-automation-bakeoff", ".venv", "Scripts", "python.exe"));
        if (!string.IsNullOrWhiteSpace(repoPython))
            return repoPython;

        return "python";
    }

    private static string LocateRuntimeScript()
    {
        var outputScript = Path.Combine(AppContext.BaseDirectory, "BrowserAutomation", "browser_use_runtime.py");
        if (File.Exists(outputScript))
            return outputScript;

        var sourceScript = FindUpwards(Path.Combine("src", "Services", "BrowserAutomation", "BrowserUseSidecar", "browser_use_runtime.py"));
        return sourceScript ?? outputScript;
    }

    private static string LocateBrowserEnvFile()
    {
        var configured = Environment.GetEnvironmentVariable("KEYBOARD_WTF_BROWSER_AUTOMATION_ENV");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var bakeoffEnv = FindUpwards(Path.Combine("tools", "browser-automation-bakeoff", ".env.local"));
        return bakeoffEnv;
    }

    private static bool HasGeminiBrowserKey(string envPath)
    {
        foreach (var name in new[] { "GEMINI_BROWSER_AGENT_API_KEY", "GOOGLE_BROWSER_AGENT_API_KEY" })
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                return true;
        }

        if (Environment.GetEnvironmentVariables().Keys
            .Cast<object>()
            .Select(Convert.ToString)
            .Any(name => !string.IsNullOrWhiteSpace(name)
                && Regex.IsMatch(name, "^BAKEOFF_GEMINI_API_KEY_\\d+$", RegexOptions.IgnoreCase)
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            return true;

        if (string.IsNullOrWhiteSpace(envPath) || !File.Exists(envPath))
            return false;

        try
        {
            foreach (var line in File.ReadLines(envPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#", StringComparison.Ordinal) || !trimmed.Contains('='))
                    continue;
                var name = trimmed.Split('=', 2)[0].Trim();
                if (name is "GEMINI_BROWSER_AGENT_API_KEY" or "GOOGLE_BROWSER_AGENT_API_KEY"
                    || Regex.IsMatch(name, "^BAKEOFF_GEMINI_API_KEY_\\d+$", RegexOptions.IgnoreCase))
                    return true;
            }
        }
        catch { }

        return false;
    }

    private static string FindUpwards(string relative)
    {
        var starts = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        };

        foreach (var start in starts)
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate))
                    return candidate;
                dir = dir.Parent;
            }
        }

        return null;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static string Redact(string value) =>
        Regex.Replace(value ?? "", @"(AIza[0-9A-Za-z._\-—]+|AQ\.Ab8[0-9A-Za-z._-]+|bu_[0-9A-Za-z._-]+)", "[REDACTED_API_KEY]");

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
