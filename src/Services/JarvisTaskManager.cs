namespace KeyboardWtf.Services;

using KeyboardWtf.Models;

public sealed class JarvisTaskCapacityException : InvalidOperationException
{
    public JarvisTaskCapacityException()
        : base($"Jarvis can run at most {JarvisTaskManager.MaxActiveTasks} active tasks.")
    {
    }
}

public sealed class JarvisTaskManager : IDisposable
{
    public const int MaxActiveTasks = 4;
    public const int MaxStoredTasks = 40;

    private sealed class TaskRuntime
    {
        public JarvisTask Task { get; init; }
        public CancellationTokenSource Cancellation { get; init; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, TaskRuntime> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event Action<JarvisTask> TaskChanged;
    public event Action<string, bool> ConfirmationResponded;
    public event Action<string, string> ClarificationResponded;

    public JarvisTask Create(
        string title,
        string originalUserCommand,
        IEnumerable<string> stepTitles,
        IEnumerable<string> permissionsNeeded = null,
        TimeSpan? timeout = null)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_tasks.Values.Count(runtime => IsActive(runtime.Task.Status)) >= MaxActiveTasks)
                throw new JarvisTaskCapacityException();

            var task = new JarvisTask
            {
                TaskId = Guid.NewGuid().ToString("N"),
                Title = Clean(title, 120, "Jarvis task"),
                OriginalUserCommand = Clean(originalUserCommand, 2000, ""),
                Status = JarvisTaskStatus.Pending,
                Steps = (stepTitles ?? Array.Empty<string>())
                    .Where(step => !string.IsNullOrWhiteSpace(step))
                    .Take(20)
                    .Select(step => new JarvisTaskStep { Title = Clean(step, 160, "Task step") })
                    .ToList(),
                PermissionsNeeded = (permissionsNeeded ?? Array.Empty<string>())
                    .Where(permission => !string.IsNullOrWhiteSpace(permission))
                    .Select(permission => Clean(permission, 160, ""))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToList(),
            };
            var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(15));
            _tasks[task.TaskId] = new TaskRuntime { Task = task, Cancellation = cancellation };
            _ = WatchTimeoutAsync(task.TaskId, cancellation.Token);
            TrimLocked();
            PublishLocked(task);
            return Clone(task);
        }
    }

    public IReadOnlyList<JarvisTask> Snapshot(bool includeCompleted = true)
    {
        lock (_lock)
        {
            return _tasks.Values
                .Select(runtime => runtime.Task)
                .Where(task => includeCompleted || IsActive(task.Status))
                .OrderByDescending(task => IsActive(task.Status))
                .ThenByDescending(task => task.UpdatedAt)
                .Select(Clone)
                .ToArray();
        }
    }

    public JarvisTask Get(string taskId)
    {
        lock (_lock)
            return TryRuntime(taskId, out var runtime) ? Clone(runtime.Task) : null;
    }

    public CancellationToken TokenFor(string taskId)
    {
        lock (_lock)
        {
            if (!TryRuntime(taskId, out var runtime))
                return new CancellationToken(canceled: true);
            return runtime.Cancellation.Token;
        }
    }

    public JarvisTask Start(string taskId)
    {
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            task.Status = JarvisTaskStatus.Running;
            Touch(task);
            PublishLocked(task);
            return Clone(task);
        }
    }

    public JarvisTask SetConfidence(string taskId, TaskConfidence confidence)
    {
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            task.Confidence = confidence;
            Touch(task);
            PublishLocked(task);
            return Clone(task);
        }
    }

    public JarvisTask SetContext(string taskId, string summary)
    {
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            task.ContextSummary = Clean(summary, 3000, "");
            Touch(task);
            PublishLocked(task);
            return Clone(task);
        }
    }

    public JarvisTask SetStep(
        string taskId,
        string stepId,
        JarvisTaskStepStatus status,
        string detail = null)
    {
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            var step = task.Steps.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, stepId, StringComparison.OrdinalIgnoreCase));
            if (step == null)
                throw new InvalidOperationException("Task step was not found.");
            step.Status = status;
            if (detail != null)
                step.Detail = Clean(detail, 1000, "");
            if (status == JarvisTaskStepStatus.Running && step.StartedAt == null)
                step.StartedAt = DateTimeOffset.UtcNow;
            if (status is JarvisTaskStepStatus.Completed
                or JarvisTaskStepStatus.Failed
                or JarvisTaskStepStatus.TimedOut
                or JarvisTaskStepStatus.Cancelled)
            {
                step.CompletedAt = DateTimeOffset.UtcNow;
            }
            Touch(task);
            PublishLocked(task);
            return Clone(task);
        }
    }

    public JarvisTask SetStepByIndex(
        string taskId,
        int index,
        JarvisTaskStepStatus status,
        string detail = null)
    {
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            if (index < 0 || index >= task.Steps.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return SetStep(taskId, task.Steps[index].Id, status, detail);
        }
    }

    public JarvisTask AddReceipt(string taskId, string item)
    {
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            var clean = Clean(item, 1000, "");
            if (clean.Length > 0)
            {
                task.Receipt.Add(clean);
                if (task.Receipt.Count > 40)
                    task.Receipt.RemoveAt(0);
            }
            Touch(task);
            PublishLocked(task);
            return Clone(task);
        }
    }

    public JarvisTask WaitForConfirmation(
        string taskId,
        string stepId,
        string prompt,
        string actionDescription)
    {
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            task.Status = JarvisTaskStatus.WaitingForConfirmation;
            task.PendingPrompt = Clean(prompt, 1000, "Confirmation required.");
            task.PendingActionDescription = Clean(actionDescription, 1000, "");
            SetStep(taskId, stepId, JarvisTaskStepStatus.WaitingForConfirmation, task.PendingPrompt);
            Touch(task);
            PublishLocked(task);
            return Clone(task);
        }
    }

    public JarvisTask RespondToConfirmation(string taskId, bool confirmed)
    {
        JarvisTask snapshot;
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            if (task.Status != JarvisTaskStatus.WaitingForConfirmation)
                return Clone(task);
            task.Status = confirmed ? JarvisTaskStatus.Running : JarvisTaskStatus.Cancelled;
            task.PendingPrompt = "";
            if (!confirmed)
            {
                task.Result = "The user declined the pending action.";
                task.CompletedAt = DateTimeOffset.UtcNow;
            }
            Touch(task);
            PublishLocked(task);
            snapshot = Clone(task);
        }
        ConfirmationResponded?.Invoke(taskId, confirmed);
        return snapshot;
    }

    public JarvisTask WaitForClarification(
        string taskId,
        string stepId,
        string prompt,
        IEnumerable<string> options = null)
    {
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            task.Status = JarvisTaskStatus.WaitingForClarification;
            task.PendingPrompt = Clean(prompt, 1000, "Clarification is needed.");
            task.PendingOptions = (options ?? Array.Empty<string>())
                .Select(option => Clean(option, 240, ""))
                .Where(option => option.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
            SetStep(taskId, stepId, JarvisTaskStepStatus.WaitingForClarification, task.PendingPrompt);
            Touch(task);
            PublishLocked(task);
            return Clone(task);
        }
    }

    public JarvisTask ProvideClarification(string taskId, string clarification)
    {
        JarvisTask snapshot;
        var clean = Clean(clarification, 1000, "");
        if (clean.Length == 0)
            throw new InvalidOperationException("Clarification cannot be empty.");
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            if (task.Status != JarvisTaskStatus.WaitingForClarification)
                return Clone(task);
            task.LastClarification = clean;
            task.PendingPrompt = "";
            task.PendingOptions.Clear();
            task.Status = JarvisTaskStatus.Running;
            Touch(task);
            PublishLocked(task);
            snapshot = Clone(task);
        }
        ClarificationResponded?.Invoke(taskId, clean);
        return snapshot;
    }

    public JarvisTask Pause(string taskId)
    {
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            if (task.Status == JarvisTaskStatus.Running)
                task.Status = JarvisTaskStatus.Paused;
            Touch(task);
            PublishLocked(task);
            return Clone(task);
        }
    }

    public JarvisTask Resume(string taskId)
    {
        lock (_lock)
        {
            var task = RequiredTask(taskId);
            if (task.Status == JarvisTaskStatus.Paused)
                task.Status = JarvisTaskStatus.Running;
            Touch(task);
            PublishLocked(task);
            return Clone(task);
        }
    }

    public JarvisTask Complete(string taskId, string result)
    {
        lock (_lock)
        {
            if (!TryRuntime(taskId, out var runtime))
                throw new InvalidOperationException("Task was not found.");
            runtime.Cancellation.Cancel();
            runtime.Task.Status = JarvisTaskStatus.Completed;
            runtime.Task.Result = Clean(result, 3000, "Task completed.");
            runtime.Task.CompletedAt = DateTimeOffset.UtcNow;
            foreach (var step in runtime.Task.Steps.Where(step =>
                step.Status is JarvisTaskStepStatus.Pending or JarvisTaskStepStatus.Running))
            {
                step.Status = JarvisTaskStepStatus.Completed;
                step.CompletedAt = DateTimeOffset.UtcNow;
            }
            Touch(runtime.Task);
            PublishLocked(runtime.Task);
            return Clone(runtime.Task);
        }
    }

    public JarvisTask Fail(string taskId, string error)
    {
        lock (_lock)
        {
            if (!TryRuntime(taskId, out var runtime))
                throw new InvalidOperationException("Task was not found.");
            runtime.Cancellation.Cancel();
            runtime.Task.Status = JarvisTaskStatus.Failed;
            runtime.Task.Result = Clean(error, 3000, "Task failed.");
            runtime.Task.CompletedAt = DateTimeOffset.UtcNow;
            foreach (var step in runtime.Task.Steps.Where(step =>
                step.Status is JarvisTaskStepStatus.Pending
                    or JarvisTaskStepStatus.Running
                    or JarvisTaskStepStatus.WaitingForConfirmation
                    or JarvisTaskStepStatus.WaitingForClarification))
            {
                step.Status = JarvisTaskStepStatus.Failed;
                step.Detail = Clean(error, 1000, "Task failed.");
                step.CompletedAt = DateTimeOffset.UtcNow;
            }
            Touch(runtime.Task);
            PublishLocked(runtime.Task);
            return Clone(runtime.Task);
        }
    }

    public JarvisTask Timeout(string taskId, string reason = null)
    {
        lock (_lock)
        {
            if (!TryRuntime(taskId, out var runtime))
                return null;
            if (!IsActive(runtime.Task.Status))
                return Clone(runtime.Task);
            runtime.Cancellation.Cancel();
            runtime.Task.Status = JarvisTaskStatus.TimedOut;
            runtime.Task.Result = Clean(reason, 1000, "Task timed out.");
            runtime.Task.CompletedAt = DateTimeOffset.UtcNow;
            foreach (var step in runtime.Task.Steps.Where(step =>
                step.Status is JarvisTaskStepStatus.Pending
                    or JarvisTaskStepStatus.Running
                    or JarvisTaskStepStatus.WaitingForConfirmation
                    or JarvisTaskStepStatus.WaitingForClarification))
            {
                step.Status = JarvisTaskStepStatus.TimedOut;
                step.Detail = Clean(reason, 1000, "Task timed out.");
                step.CompletedAt = DateTimeOffset.UtcNow;
            }
            Touch(runtime.Task);
            PublishLocked(runtime.Task);
            return Clone(runtime.Task);
        }
    }

    public JarvisTask Cancel(string taskId, string reason = null)
    {
        lock (_lock)
        {
            if (!TryRuntime(taskId, out var runtime))
                return null;
            runtime.Cancellation.Cancel();
            runtime.Task.Status = JarvisTaskStatus.Cancelled;
            runtime.Task.Result = Clean(reason, 1000, "Task cancelled.");
            runtime.Task.CompletedAt = DateTimeOffset.UtcNow;
            foreach (var step in runtime.Task.Steps.Where(step =>
                step.Status is JarvisTaskStepStatus.Pending
                    or JarvisTaskStepStatus.Running
                    or JarvisTaskStepStatus.WaitingForConfirmation
                    or JarvisTaskStepStatus.WaitingForClarification))
            {
                step.Status = JarvisTaskStepStatus.Cancelled;
                step.CompletedAt = DateTimeOffset.UtcNow;
            }
            Touch(runtime.Task);
            PublishLocked(runtime.Task);
            return Clone(runtime.Task);
        }
    }

    public int CancelAllActive(string reason = null)
    {
        string[] ids;
        lock (_lock)
        {
            ids = _tasks.Values
                .Where(runtime => IsActive(runtime.Task.Status))
                .Select(runtime => runtime.Task.TaskId)
                .ToArray();
        }
        foreach (var id in ids)
            Cancel(id, reason);
        return ids.Length;
    }

    public bool IsRunnable(string taskId)
    {
        lock (_lock)
        {
            return TryRuntime(taskId, out var runtime)
                && runtime.Task.Status == JarvisTaskStatus.Running
                && !runtime.Cancellation.IsCancellationRequested;
        }
    }

    private JarvisTask RequiredTask(string taskId)
    {
        if (!TryRuntime(taskId, out var runtime))
            throw new InvalidOperationException("Task was not found.");
        return runtime.Task;
    }

    private bool TryRuntime(string taskId, out TaskRuntime runtime) =>
        _tasks.TryGetValue((taskId ?? "").Trim(), out runtime);

    private static bool IsActive(JarvisTaskStatus status) =>
        status is JarvisTaskStatus.Pending
            or JarvisTaskStatus.Running
            or JarvisTaskStatus.Paused
            or JarvisTaskStatus.WaitingForConfirmation
            or JarvisTaskStatus.WaitingForClarification;

    private async Task WatchTimeoutAsync(string taskId, CancellationToken token)
    {
        try
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetResult());
            await completion.Task.ConfigureAwait(false);
            lock (_lock)
            {
                if (!TryRuntime(taskId, out var runtime) || !IsActive(runtime.Task.Status))
                    return;
            }
            Timeout(taskId, "Task timed out.");
        }
        catch
        {
            // Task timeout watcher must never crash the app.
        }
    }

    private void TrimLocked()
    {
        while (_tasks.Count > MaxStoredTasks)
        {
            var oldest = _tasks.Values
                .Where(runtime => !IsActive(runtime.Task.Status))
                .OrderBy(runtime => runtime.Task.UpdatedAt)
                .FirstOrDefault();
            if (oldest == null)
                break;
            oldest.Cancellation.Dispose();
            _tasks.Remove(oldest.Task.TaskId);
        }
    }

    private void PublishLocked(JarvisTask task)
    {
        var snapshot = Clone(task);
        ThreadPool.QueueUserWorkItem(_ => TaskChanged?.Invoke(snapshot));
    }

    private static void Touch(JarvisTask task) =>
        task.UpdatedAt = DateTimeOffset.UtcNow;

    private static string Clean(string value, int maxLength, string fallback)
    {
        var clean = string.Join(
            " ",
            (value ?? "").ReplaceLineEndings(" ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (clean.Length == 0)
            clean = fallback;
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }

    private static JarvisTask Clone(JarvisTask task) => new()
    {
        TaskId = task.TaskId,
        Title = task.Title,
        OriginalUserCommand = task.OriginalUserCommand,
        Status = task.Status,
        Steps = task.Steps.Select(step => new JarvisTaskStep
        {
            Id = step.Id,
            Title = step.Title,
            Detail = step.Detail,
            Status = step.Status,
            StartedAt = step.StartedAt,
            CompletedAt = step.CompletedAt,
        }).ToList(),
        PermissionsNeeded = task.PermissionsNeeded.ToList(),
        CreatedAt = task.CreatedAt,
        UpdatedAt = task.UpdatedAt,
        CompletedAt = task.CompletedAt,
        Result = task.Result,
        Receipt = task.Receipt.ToList(),
        ContextSummary = task.ContextSummary,
        PendingPrompt = task.PendingPrompt,
        PendingOptions = task.PendingOptions.ToList(),
        PendingActionDescription = task.PendingActionDescription,
        LastClarification = task.LastClarification,
        Confidence = task.Confidence,
    };

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(JarvisTaskManager));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var runtime in _tasks.Values)
            {
                try { runtime.Cancellation.Cancel(); } catch { }
                runtime.Cancellation.Dispose();
            }
            _tasks.Clear();
        }
    }
}
