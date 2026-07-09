namespace KeyboardWtf.Services;

using System.Text.Json.Serialization;

public enum BrowserAutomationActionLevel
{
    ReadOnly,
    Navigation,
    FormFillPreview,
    CartMutationAfterConfirmation,
    ProtectedFinalActionBlocked,
}

public sealed class BrowserAutomationClassification
{
    public bool ShouldUseBrowserAutomation { get; init; }
    public string TaskType { get; init; } = "unknown";
    public string TargetSite { get; init; } = "";
    public BrowserAutomationActionLevel ActionLevel { get; init; } = BrowserAutomationActionLevel.Navigation;
    public string Profile { get; init; } = "smart";
    public string Reason { get; init; } = "";
    public string[] SuccessCriteria { get; init; } = Array.Empty<string>();
}

public sealed class BrowserAutomationRequest
{
    public string TaskText { get; init; } = "";
    public string TaskType { get; init; } = "unknown";
    public string TargetSite { get; init; } = "";
    public BrowserAutomationActionLevel ActionLevel { get; init; } = BrowserAutomationActionLevel.Navigation;
    public string Profile { get; init; } = "smart";
    public string Model { get; init; } = "";
    public int MaxRuntimeMs { get; init; } = 150_000;
    public bool CartMutationPreconfirmed { get; init; }
    public bool UsePersistentSession { get; init; } = true;
    public string[] SuccessCriteria { get; init; } = Array.Empty<string>();
    public string PatternContext { get; init; } = "";
}

public sealed class BrowserAutomationResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("failureReason")]
    public string FailureReason { get; init; } = "";

    [JsonPropertyName("finalUrl")]
    public string FinalUrl { get; init; } = "";

    [JsonPropertyName("runtimeMs")]
    public int RuntimeMs { get; init; }

    [JsonPropertyName("actionCount")]
    public int ActionCount { get; init; }

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "";

    [JsonPropertyName("screenshotPath")]
    public string ScreenshotPath { get; init; } = "";

    [JsonPropertyName("receiptSummary")]
    public string ReceiptSummary { get; init; } = "";

    [JsonPropertyName("receiptDisagreesWithSummary")]
    public bool ReceiptDisagreesWithSummary { get; init; }
}

public sealed class BrowserAutomationAvailability
{
    public bool Ready { get; init; }
    public string Status { get; init; } = "unknown";
    public string Message { get; init; } = "";
    public string Backend { get; init; } = "browser-use + Gemini";
}

public interface IBrowserAutomationProvider
{
    Task<BrowserAutomationResult> ExecuteAsync(BrowserAutomationRequest request, CancellationToken token);
}
