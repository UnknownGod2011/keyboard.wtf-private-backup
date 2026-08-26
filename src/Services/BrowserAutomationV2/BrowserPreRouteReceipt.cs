namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Text.RegularExpressions;

/// <summary>
/// Keeps one locally pre-routed Jarvis turn coherent until the Live model
/// receives its matching tool result. This prevents a verified local action
/// from being executed twice or reported as a failure by the model.
/// </summary>
internal sealed class BrowserPreRouteReceipt
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(12);

    public string UserTurn { get; init; } = "";
    public string ToolName { get; init; } = "";
    public bool Ok { get; init; }
    public bool Started { get; init; }
    public string TaskId { get; init; } = "";
    public string Message { get; init; } = "";
    public Dictionary<string, object> Result { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    public bool IsFresh(DateTime utcNow) => utcNow - CreatedUtc <=
        (string.Equals(ToolName, "run_workflow", StringComparison.Ordinal)
            ? TimeSpan.FromSeconds(60)
            : Lifetime);

    public bool IsEchoOf(string userTurn, DateTime utcNow) =>
        IsFresh(utcNow)
        && string.Equals(
            Normalize(UserTurn),
            Normalize(userTurn),
            StringComparison.Ordinal);

    public bool IsIncrementalContinuationOf(string userTurn, DateTime utcNow)
    {
        if (!IsFresh(utcNow))
            return false;
        var existing = Normalize(UserTurn);
        var current = Normalize(userTurn);
        return existing.Length > 0
            && current.Length > 0
            && (string.Equals(existing, current, StringComparison.Ordinal)
                || current.StartsWith(existing + " ", StringComparison.Ordinal)
                || existing.StartsWith(current + " ", StringComparison.Ordinal));
    }

    public bool AppliesTo(string toolName, DateTime utcNow) =>
        IsFresh(utcNow)
        && string.Equals(ToolName, toolName?.Trim(), StringComparison.Ordinal);

    public bool BlocksCompetingTool(string toolName, DateTime utcNow) =>
        IsFresh(utcNow)
        && !AppliesTo(toolName, utcNow)
        && (string.Equals(ToolName, "run_workflow", StringComparison.Ordinal)
            || !CommandRegistry.IsBrowserTaskLifecycleTool(toolName));

    private static string Normalize(string value) =>
        Regex.Replace((value ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
}
