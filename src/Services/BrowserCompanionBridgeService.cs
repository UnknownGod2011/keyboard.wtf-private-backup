namespace KeyboardWtf.Services;

using System.Collections.Concurrent;
using KeyboardWtf.Models;

public sealed class BrowserCompanionBridgeService
{
    public const string BridgeHeader = "X-Keyboard-Wtf-Companion";
    public const string BridgeHeaderValue = "keyboard-wtf-companion-v1";

    private sealed class PendingCommand
    {
        public BrowserCompanionCommand Command { get; init; }
        public TaskCompletionSource<BrowserCompanionCommandResult> Completion { get; init; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _lock = new();
    private readonly ConcurrentQueue<BrowserCompanionCommand> _queue = new();
    private readonly Dictionary<string, PendingCommand> _pending = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _lastSeenAt;
    private DateTimeOffset _lastGraphAt;
    private DateTimeOffset _lastCommandSentAt;
    private DateTimeOffset _lastCommandResultAt;
    private string _sessionId = "";
    private string _extensionVersion = "";
    private string _contentScriptVersion = "";
    private string _browserName = "";
    private string _activeTabUrl = "";
    private string _lastCommandType = "";
    private string _lastCommandResult = "";
    private BrowserCompanionControlGraph _lastGraph;
    private string _lastError = "";

    public BrowserBackendStatus Status
    {
        get
        {
            lock (_lock)
            {
                var connected = IsConnectedLocked();
                return new BrowserBackendStatus
                {
                    Initialized = true,
                    Available = connected,
                    BrowserChannel = connected
                        ? $"Browser Companion Extension {_extensionVersion}".Trim()
                        : "Browser Companion Extension",
                    ProfilePath = "",
                    LastError = connected ? "" : _lastError.Length > 0
                        ? _lastError
                        : "Browser Companion Extension is not connected.",
                    ConnectionState = connected
                        ? "Connected"
                        : _lastSeenAt == default ? "Waiting for extension" : "Disconnected",
                    SessionId = _sessionId,
                    ExtensionVersion = _extensionVersion,
                    ContentScriptVersion = _contentScriptVersion,
                    BrowserName = _browserName,
                    ActiveTabUrl = _activeTabUrl,
                    LastHeartbeatAt = _lastSeenAt == default ? null : _lastSeenAt,
                    LastGraphReceivedAt = _lastGraphAt == default ? null : _lastGraphAt,
                    LastCommandSentAt = _lastCommandSentAt == default ? null : _lastCommandSentAt,
                    LastCommandResultAt = _lastCommandResultAt == default ? null : _lastCommandResultAt,
                    LastCommandType = _lastCommandType,
                    LastCommandResult = _lastCommandResult,
                    LastControlCount = _lastGraph?.Controls?.Count ?? 0,
                    TopVisibleControls = TopControlsLocked(_lastGraph),
                };
            }
        }
    }

    public BrowserBackendStatus DetailedStatus(
        int bridgePort,
        bool browserUseFallbackEnabled,
        bool automationProfileFallbackEnabled)
    {
        var status = Status;
        return new BrowserBackendStatus
        {
            Initialized = status.Initialized,
            Available = status.Available,
            BrowserChannel = status.BrowserChannel,
            ProfilePath = status.ProfilePath,
            LastError = status.LastError,
            ConnectionState = status.ConnectionState,
            SessionId = status.SessionId,
            ExtensionVersion = status.ExtensionVersion,
            ContentScriptVersion = status.ContentScriptVersion,
            BrowserName = status.BrowserName,
            ActiveTabUrl = status.ActiveTabUrl,
            LastHeartbeatAt = status.LastHeartbeatAt,
            LastGraphReceivedAt = status.LastGraphReceivedAt,
            LastCommandSentAt = status.LastCommandSentAt,
            LastCommandResultAt = status.LastCommandResultAt,
            LastCommandType = status.LastCommandType,
            LastCommandResult = status.LastCommandResult,
            BridgePort = bridgePort,
            BridgeUrl = bridgePort > 0 ? $"http://localhost:{bridgePort}" : "",
            LastControlCount = status.LastControlCount,
            TopVisibleControls = status.TopVisibleControls,
            BrowserUseFallbackEnabled = browserUseFallbackEnabled,
            AutomationProfileFallbackEnabled = automationProfileFallbackEnabled,
        };
    }

    public BrowserCompanionControlGraph LastGraph
    {
        get
        {
            lock (_lock)
                return _lastGraph;
        }
    }

    public void Register(
        string sessionId,
        string extensionVersion,
        string contentScriptVersion = "",
        string browserName = "",
        string activeTabUrl = "")
    {
        lock (_lock)
        {
            var incomingVersion = Clean(extensionVersion, 40);
            if (IsConnectedLocked()
                && !string.Equals(_sessionId, Clean(sessionId, 80), StringComparison.OrdinalIgnoreCase)
                && CompareVersion(_extensionVersion, incomingVersion) > 0)
            {
                // Do not let an older manually loaded extension steal command routing from a newer session.
                return;
            }
            _sessionId = Clean(sessionId, 80);
            _extensionVersion = incomingVersion;
            if (!string.IsNullOrWhiteSpace(contentScriptVersion))
                _contentScriptVersion = Clean(contentScriptVersion, 40);
            if (!string.IsNullOrWhiteSpace(browserName))
                _browserName = Clean(browserName, 80);
            if (!string.IsNullOrWhiteSpace(activeTabUrl))
                _activeTabUrl = Clean(activeTabUrl, 260);
            _lastSeenAt = DateTimeOffset.UtcNow;
            _lastError = "";
        }
    }

    public void SubmitGraph(BrowserCompanionControlGraph graph)
    {
        if (graph == null)
            return;
        lock (_lock)
        {
            _lastGraph = graph;
            _lastGraphAt = DateTimeOffset.UtcNow;
            if (LooksLikeExtensionSessionId(graph.SessionId))
                _sessionId = Clean(graph.SessionId, 80);
            if (!string.IsNullOrWhiteSpace(graph.ExtensionVersion))
                _extensionVersion = Clean(graph.ExtensionVersion, 40);
            if (!string.IsNullOrWhiteSpace(graph.ContentScriptVersion))
                _contentScriptVersion = Clean(graph.ContentScriptVersion, 40);
            if (!string.IsNullOrWhiteSpace(graph.BrowserName))
                _browserName = Clean(graph.BrowserName, 80);
            if (!string.IsNullOrWhiteSpace(graph.Url))
                _activeTabUrl = Clean(graph.Url, 260);
            _lastSeenAt = DateTimeOffset.UtcNow;
            _lastError = "";
        }
    }

    public BrowserCompanionCommand NextCommand(string sessionId)
    {
        var cleanSession = Clean(sessionId, 80);
        lock (_lock)
        {
            if (string.Equals(_sessionId, cleanSession, StringComparison.OrdinalIgnoreCase))
                _lastSeenAt = DateTimeOffset.UtcNow;
        }
        if (!_queue.TryPeek(out var command))
            return null;
        if (!string.Equals(command.SessionId, cleanSession, StringComparison.OrdinalIgnoreCase))
            return null;
        return _queue.TryDequeue(out command) ? command : null;
    }

    public void Complete(BrowserCompanionCommandResult result)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.CommandId))
            return;

        PendingCommand pending = null;
        lock (_lock)
        {
            _lastSeenAt = DateTimeOffset.UtcNow;
            if (!_pending.TryGetValue(result.CommandId, out pending))
                return;
            if (!string.IsNullOrWhiteSpace(result.SessionId)
                && !string.Equals(pending.Command.SessionId, result.SessionId, StringComparison.OrdinalIgnoreCase))
                return;
            if (result.Graph != null)
            {
                _lastGraph = result.Graph;
                _lastGraphAt = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(result.Graph.Url))
                    _activeTabUrl = Clean(result.Graph.Url, 260);
                if (!string.IsNullOrWhiteSpace(result.Graph.ContentScriptVersion))
                    _contentScriptVersion = Clean(result.Graph.ContentScriptVersion, 40);
                if (!string.IsNullOrWhiteSpace(result.Graph.BrowserName))
                    _browserName = Clean(result.Graph.BrowserName, 80);
            }
            _pending.Remove(result.CommandId);
            _lastCommandResultAt = DateTimeOffset.UtcNow;
            _lastCommandResult = Clean(
                result.Ok
                    ? $"{result.Status}: {result.Message}"
                    : $"{result.Status}: {result.Message}",
                260);
            if (!result.Ok && !string.IsNullOrWhiteSpace(result.Message))
                _lastError = Clean(result.Message, 300);
        }
        pending?.Completion.TrySetResult(result);
    }

    public async Task<BrowserCompanionCommandResult> ExecuteAsync(
        BrowserCompanionCommand command,
        TimeSpan timeout,
        CancellationToken token)
    {
        if (command == null)
            throw new ArgumentNullException(nameof(command));
        if (!Status.Available)
        {
            return new BrowserCompanionCommandResult
            {
                CommandId = command.CommandId,
                TaskId = command.TaskId,
                Ok = false,
                Status = "extension_disconnected",
                Message = "Browser Companion Extension is not connected.",
            };
        }

        string targetSession;
        lock (_lock)
            targetSession = _sessionId;
        var scopedCommand = new BrowserCompanionCommand
        {
            CommandId = command.CommandId,
            SessionId = targetSession,
            TaskId = command.TaskId,
            Type = command.Type,
            Url = command.Url,
            ControlId = command.ControlId,
            Text = command.Text,
            TimeoutMs = command.TimeoutMs,
        };
        var pending = new PendingCommand { Command = scopedCommand };
        lock (_lock)
        {
            _pending[scopedCommand.CommandId] = pending;
            _lastCommandSentAt = DateTimeOffset.UtcNow;
            _lastCommandType = Clean(scopedCommand.Type, 80);
        }
        _queue.Enqueue(scopedCommand);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        using var registration = cts.Token.Register(() =>
        {
            lock (_lock)
                _pending.Remove(command.CommandId);
            pending.Completion.TrySetResult(new BrowserCompanionCommandResult
            {
                CommandId = command.CommandId,
                TaskId = command.TaskId,
                Ok = false,
                Status = "timed_out",
                Message = $"Browser Companion command timed out: {command.Type}.",
            });
        });
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    private bool IsConnectedLocked() =>
        _lastSeenAt != default
        && DateTimeOffset.UtcNow - _lastSeenAt < TimeSpan.FromSeconds(10);

    private static IReadOnlyList<string> TopControlsLocked(BrowserCompanionControlGraph graph) =>
        graph?.Controls?
            .Where(control => control.Visible && !control.Sensitive)
            .Select(control => Clean(
                string.Join(
                    " ",
                    new[]
                    {
                        control.ControlType,
                        control.Role,
                        control.Text,
                        control.AriaLabel,
                        control.Title,
                        control.Placeholder,
                    }.Where(value => !string.IsNullOrWhiteSpace(value))),
                120))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray()
        ?? Array.Empty<string>();

    private static bool LooksLikeExtensionSessionId(string value) =>
        Guid.TryParse(value, out _)
        || (!string.IsNullOrWhiteSpace(value)
            && !value.Contains(':', StringComparison.Ordinal)
            && value.Length >= 16
            && value.Length <= 80);

    private static int CompareVersion(string current, string incoming)
    {
        if (!Version.TryParse(current, out var currentVersion))
            currentVersion = new Version(0, 0, 0);
        if (!Version.TryParse(incoming, out var incomingVersion))
            incomingVersion = new Version(0, 0, 0);
        return currentVersion.CompareTo(incomingVersion);
    }

    private static string Clean(string value, int maxLength)
    {
        var clean = string.Join(
            " ",
            (value ?? "").ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }
}
