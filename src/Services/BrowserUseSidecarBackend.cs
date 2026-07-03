namespace KeyboardWtf.Services;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyboardWtf.Models;

public interface ISmartBrowserTaskBackend : IAsyncDisposable
{
    BrowserBackendStatus Status { get; }
    Task<BrowserSmartTaskResult> RunAsync(
        BrowserSmartTaskRequest request,
        CancellationToken token);
}

public sealed class BrowserUseSidecarBackend : ISmartBrowserTaskBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly string _sidecarDirectory;
    private readonly string _scriptPath;
    private readonly string _pythonPath;
    private string _lastError = "";

    public BrowserUseSidecarBackend(
        string sidecarDirectory = null,
        string pythonPath = null)
    {
        _sidecarDirectory = sidecarDirectory ?? FindSidecarDirectory();
        _scriptPath = string.IsNullOrWhiteSpace(_sidecarDirectory)
            ? ""
            : Path.Combine(_sidecarDirectory, "browser_use_sidecar.py");
        _pythonPath = string.IsNullOrWhiteSpace(pythonPath)
            ? FindPythonPath(_sidecarDirectory)
            : pythonPath;
    }

    public BrowserBackendStatus Status
    {
        get
        {
            var scriptExists = File.Exists(_scriptPath);
            var pythonConfigured = !string.IsNullOrWhiteSpace(_pythonPath);
            var keyConfigured = GeminiKeyConfigured();
            return new BrowserBackendStatus
            {
                Initialized = scriptExists,
                Available = scriptExists && pythonConfigured && keyConfigured,
                BrowserChannel = "browser-use + Gemini",
                ProfilePath = string.IsNullOrWhiteSpace(_sidecarDirectory)
                    ? ""
                    : Path.Combine(_sidecarDirectory, ".browser-use-sidecar-profile"),
                LastError = !scriptExists
                    ? "Browser-use sidecar is not installed for this local build."
                    : !keyConfigured
                        ? "Browser-use backend needs Gemini API key configured."
                        : _lastError,
            };
        }
    }

    public async Task<BrowserSmartTaskResult> RunAsync(
        BrowserSmartTaskRequest request,
        CancellationToken token)
    {
        if (!File.Exists(_scriptPath))
            return Failure("Browser-use sidecar is not installed for this local build.", BrowserSmartTaskStatus.Failed);
        if (!GeminiKeyConfigured())
            return Failure("Browser-use backend needs Gemini API key configured.", BrowserSmartTaskStatus.Failed);

        var timeout = request.Timeout <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(2)
            : request.Timeout;
        var payloadPath = Path.Combine(Path.GetTempPath(), $"keyboard-wtf-browser-use-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            payloadPath,
            JsonSerializer.Serialize(new
            {
                task_id = request.TaskId,
                title = request.Title,
                original_user_command = request.OriginalUserCommand,
                start_url = request.StartUrl,
                context_summary = request.ContextSummary,
                risky_action_confirmed = request.RiskyActionConfirmed,
                timeout_seconds = (int)Math.Ceiling(timeout.TotalSeconds),
                max_steps = 14,
            }, JsonOptions),
            Encoding.UTF8,
            token);

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _pythonPath,
                WorkingDirectory = _sidecarDirectory,
                Arguments = $"-u \"{_scriptPath}\" --input \"{payloadPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            process.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            process.StartInfo.Environment["PYTHONUTF8"] = "1";

            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    stderr.AppendLine(args.Data);
            };

            if (!process.Start())
                return Failure("Browser-use sidecar could not be started.", BrowserSmartTaskStatus.Failed);

            process.BeginErrorReadLine();
            var outputTask = process.StandardOutput.ReadToEndAsync(token);
            var exited = await WaitForExitAsync(process, timeout, token);
            if (!exited)
            {
                TryKill(process);
                _lastError = "Browser-use sidecar timed out.";
                return Failure("Browser-use backend timed out.", BrowserSmartTaskStatus.TimedOut);
            }

            var stdout = (await outputTask).Trim();
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            {
                _lastError = Sanitized(stderr.ToString());
                return Failure(ClassifyProcessError(_lastError), BrowserSmartTaskStatus.Failed);
            }

            var jsonLine = stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(line => line.StartsWith("{", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(jsonLine))
            {
                _lastError = Sanitized(stderr.ToString());
                return Failure("Browser-use backend did not return structured JSON.", BrowserSmartTaskStatus.Failed);
            }

            var result = JsonSerializer.Deserialize<BrowserSmartTaskResult>(jsonLine, JsonOptions);
            if (result == null)
                return Failure("Browser-use backend returned an invalid response.", BrowserSmartTaskStatus.Failed);
            _lastError = result.Ok ? "" : result.Message;
            return result;
        }
        catch (OperationCanceledException)
        {
            return Failure("Browser-use backend was cancelled.", BrowserSmartTaskStatus.Cancelled);
        }
        catch (Exception ex)
        {
            _lastError = Sanitized(ex.Message);
            return Failure(ClassifyProcessError(_lastError), BrowserSmartTaskStatus.Failed);
        }
        finally
        {
            try { File.Delete(payloadPath); } catch { }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private bool GeminiKeyConfigured()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MODEL_API_KEY")))
            return true;
        foreach (var filename in new[] { ".env.local", ".env" })
        {
            var path = string.IsNullOrWhiteSpace(_sidecarDirectory)
                ? ""
                : Path.Combine(_sidecarDirectory, filename);
            if (!File.Exists(path))
                continue;
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("GEMINI_API_KEY=", StringComparison.Ordinal)
                    || trimmed.StartsWith("GOOGLE_API_KEY=", StringComparison.Ordinal)
                    || trimmed.StartsWith("MODEL_API_KEY=", StringComparison.Ordinal))
                {
                    return trimmed[(trimmed.IndexOf('=') + 1)..].Trim().Length > 0;
                }
            }
        }
        return false;
    }

    private static async Task<bool> WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken token)
    {
        var waitTask = process.WaitForExitAsync(token);
        var finished = await Task.WhenAny(waitTask, Task.Delay(timeout, token)) == waitTask;
        if (finished)
            await waitTask;
        return finished;
    }

    private static BrowserSmartTaskResult Failure(
        string message,
        BrowserSmartTaskStatus status) => new()
    {
        Ok = false,
        Status = status,
        Message = message,
        CanFallbackToDirect = status is BrowserSmartTaskStatus.Failed
            or BrowserSmartTaskStatus.QuotaLimited
            or BrowserSmartTaskStatus.TimedOut,
        Backend = "browser-use + Gemini",
        Progress =
        [
            new BrowserSmartTaskStep
            {
                Title = "Browser-use stopped",
                Status = "failed",
                Detail = message,
            },
        ],
    };

    private static string ClassifyProcessError(string value)
    {
        var text = value ?? "";
        if (text.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || text.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate", StringComparison.OrdinalIgnoreCase))
            return "Browser-use backend is temporarily quota-limited by Gemini.";
        if (text.Contains("Gemini API key", StringComparison.OrdinalIgnoreCase))
            return "Browser-use backend needs Gemini API key configured.";
        return "Browser-use backend failed before completing the task.";
    }

    private static string Sanitized(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return System.Text.RegularExpressions.Regex.Replace(
            value,
            @"AIza[0-9A-Za-z_\-]{20,}",
            "[redacted-gemini-key]");
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

    private static string FindPythonPath(string sidecarDirectory)
    {
        if (!string.IsNullOrWhiteSpace(sidecarDirectory))
        {
            var local = Path.Combine(sidecarDirectory, ".venv", "Scripts", "python.exe");
            if (File.Exists(local))
                return local;
        }
        return "python";
    }

    private static string FindSidecarDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "tools", "browser-agent-spike");
            if (File.Exists(Path.Combine(candidate, "browser_use_sidecar.py")))
                return candidate;
            current = current.Parent;
        }

        current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "tools", "browser-agent-spike");
            if (File.Exists(Path.Combine(candidate, "browser_use_sidecar.py")))
                return candidate;
            current = current.Parent;
        }
        return "";
    }
}
