namespace KeyboardWtf.Models;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JarvisTaskStatus
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed,
    TimedOut,
    WaitingForConfirmation,
    WaitingForClarification,
    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JarvisTaskStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    TimedOut,
    WaitingForConfirmation,
    WaitingForClarification,
    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskConfidence
{
    High,
    Medium,
    Low,
    Unknown,
}

public sealed class JarvisTaskStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public JarvisTaskStepStatus Status { get; set; } = JarvisTaskStepStatus.Pending;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class JarvisTask
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string OriginalUserCommand { get; set; } = "";
    public JarvisTaskStatus Status { get; set; } = JarvisTaskStatus.Pending;
    public List<JarvisTaskStep> Steps { get; set; } = new();
    public List<string> PermissionsNeeded { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Result { get; set; } = "";
    public List<string> Receipt { get; set; } = new();
    public string ContextSummary { get; set; } = "";
    public string PendingPrompt { get; set; } = "";
    public List<string> PendingOptions { get; set; } = new();
    public string PendingActionDescription { get; set; } = "";
    public string LastClarification { get; set; } = "";
    public TaskConfidence Confidence { get; set; } = TaskConfidence.Unknown;
}
