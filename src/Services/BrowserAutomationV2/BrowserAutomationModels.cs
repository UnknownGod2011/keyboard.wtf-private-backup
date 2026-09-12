namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Text.Json.Serialization;

public enum BrowserAutomationDecision
{
    SimpleTool,
    BrowserAutomation,
    AskUser,
}

public enum BrowserTaskState
{
    Queued,
    Planning,
    ReadingPage,
    WaitingForPage,
    AnalyzingImage,
    Executing,
    Verifying,
    Paused,
    NeedsUserAction,
    Completed,
    CompletedWithNote,
    Partial,
    Blocked,
    Cancelling,
    Cancelled,
    Failed,
}

public enum BrowserAutomationOutcome
{
    Completed,
    CompletedWithNote,
    Partial,
    NeedsUserAction,
    Blocked,
    Cancelled,
    Failed,
}

public sealed class BrowserClassification
{
    public BrowserAutomationDecision Decision { get; init; }
    public string Reason { get; init; } = "";
    public bool RefersToCurrentPage { get; init; }
    public bool PrefersSelectedContent { get; init; }
}

public sealed class BrowserAutomationSnapshot
{
    public string TaskId { get; init; } = "";
    public string Request { get; init; } = "";
    public BrowserTaskState State { get; init; } = BrowserTaskState.Queued;
    public string CurrentAction { get; init; } = "";
    public string CurrentDomain { get; init; } = "";
    public string CurrentTabTitle { get; init; } = "";
    public int? WindowId { get; init; }
    public int? TabId { get; init; }
    public IReadOnlyList<int> ControlledTabIds { get; init; } = Array.Empty<int>();
    public DateTime StartedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public bool CanPause { get; init; }
    public bool CanResume { get; init; }
    public bool CanCancel { get; init; }
    public bool VisionRecovery { get; init; }
    public BrowserUserIntervention Intervention { get; init; }
    public string InterventionKind { get; init; } = "";
    public string InterventionReason { get; init; } = "";
    public string LastError { get; init; } = "";
    public BrowserAutomationOutcome? Outcome { get; init; }
    public string ResultSummary { get; init; } = "";
}

public sealed class BrowserUserIntervention
{
    public string Kind { get; init; } = "manual_step";
    public string Missing { get; init; } = "";
    public string Explanation { get; init; } = "";
    public bool Required { get; init; }
    public bool CanSkip { get; init; }
}

public sealed class BrowserHelperStatus
{
    public bool NativeHostRegistered { get; init; }
    public bool Connected { get; init; }
    public bool CommandChannelReady { get; init; }
    public bool CommandAuthoritySupported { get; init; }
    public bool ActiveTabFresh { get; init; }
    public bool ActiveTabIsDisposableProtectedOrBlank { get; init; }
    public bool MultipleHelpersDetected { get; init; }
    public string ExpectedVersion { get; init; } = BrowserHelperProtocol.Version;
    public string ServiceWorkerVersion { get; init; } = "";
    public string ContentScriptVersion { get; init; } = "";
    public string ExtensionId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public int? WindowId { get; init; }
    public int? TabId { get; init; }
    public string ActiveUrl { get; init; } = "";
    public string ActiveTitle { get; init; } = "";
    public long GraphVersion { get; init; }
    public string DomEpoch { get; init; } = "";
    public int ControlCount { get; init; }
    public DateTime? LastHeartbeatUtc { get; init; }
    public string LastError { get; init; } = "";
}

public sealed class BrowserControl
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("accessibleName")]
    public string AccessibleName { get; set; } = "";

    [JsonPropertyName("ariaLabel")]
    public string AriaLabel { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("placeholder")]
    public string Placeholder { get; set; } = "";

    [JsonPropertyName("inputType")]
    public string InputType { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("selectedText")]
    public string SelectedText { get; set; } = "";

    [JsonPropertyName("nearbyLabel")]
    public string NearbyLabel { get; set; } = "";

    [JsonPropertyName("fieldId")]
    public string FieldId { get; set; } = "";

    [JsonPropertyName("fieldLabel")]
    public string FieldLabel { get; set; } = "";

    [JsonPropertyName("fieldRequired")]
    public bool? FieldRequired { get; set; }

    [JsonPropertyName("fieldAnswered")]
    public bool FieldAnswered { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("selected")]
    public bool Selected { get; set; }

    [JsonPropertyName("checked")]
    public bool Checked { get; set; }

    [JsonPropertyName("editable")]
    public bool Editable { get; set; }

    [JsonPropertyName("visible")]
    public bool Visible { get; set; }

    [JsonPropertyName("frameId")]
    public int FrameId { get; set; }

    [JsonPropertyName("bounds")]
    public BrowserRectangle Bounds { get; set; } = new();

    [JsonPropertyName("safeActions")]
    public string[] SafeActions { get; set; } = Array.Empty<string>();
}

public sealed class BrowserRectangle
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }
}

public sealed class BrowserPageSnapshot
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = "";

    [JsonPropertyName("windowId")]
    public int WindowId { get; set; }

    [JsonPropertyName("tabId")]
    public int TabId { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("graphVersion")]
    public long GraphVersion { get; set; }

    [JsonPropertyName("domEpoch")]
    public string DomEpoch { get; set; } = "";

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = "";

    [JsonPropertyName("pageText")]
    public string PageText { get; set; } = "";

    [JsonPropertyName("selectedText")]
    public string SelectedText { get; set; } = "";

    // This is deliberately just focus metadata. It lets the coordinator use a
    // cursor the user already placed in an editor without harvesting editor
    // contents or guessing at a broad surrounding panel.
    [JsonPropertyName("activeEditable")]
    public bool ActiveEditable { get; set; }

    [JsonPropertyName("activeEditorId")]
    public string ActiveEditorId { get; set; } = "";

    [JsonPropertyName("activeEditorRole")]
    public string ActiveEditorRole { get; set; } = "";

    [JsonPropertyName("activeEditorName")]
    public string ActiveEditorName { get; set; } = "";

    [JsonPropertyName("controls")]
    public BrowserControl[] Controls { get; set; } = Array.Empty<BrowserControl>();
}

public sealed class BrowserActionRequest
{
    // Updated Helpers recheck this command's existing desktop waiter before
    // mutation. Older public desktop releases omit the flag and stay compatible.
    public bool RequiresAuthorityCheck => true;
    public string CommandId { get; init; } = Guid.NewGuid().ToString("N");
    public string TaskId { get; init; } = "";
    public string Type { get; init; } = "";
    public string ExpectedSessionId { get; init; } = "";
    public int? WindowId { get; init; }
    public int? TabId { get; init; }
    public long? GraphVersion { get; init; }
    public string DomEpoch { get; init; } = "";
    public string ControlId { get; init; } = "";
    public string TargetRole { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string TargetFieldId { get; init; } = "";
    public string Text { get; init; } = "";
    public string Url { get; init; } = "";
    public string Key { get; init; } = "";
    public string Option { get; init; } = "";
    public bool Replace { get; init; }
    public bool CaptureBeforeAction { get; init; }
}

public sealed class BrowserActionReceipt
{
    public string SessionId { get; set; } = "";
    public bool AlreadyClosed { get; set; }
    public string CommandId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public bool Ok { get; set; }
    public bool Cancelled { get; set; }
    public bool Stale { get; set; }
    public bool NoVisibleChange { get; set; }
    public string Error { get; set; } = "";
    public string Action { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string TargetRole { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string TargetFieldId { get; set; } = "";
    // Task-scoped verification metadata supplied by the coordinator. The
    // extension proves the immediate mutation; this value lets terminal
    // verification confirm that the requested state still exists after later
    // form actions or rerenders.
    public string ExpectedValue { get; set; } = "";
    public string BeforeUrl { get; set; } = "";
    public string AfterUrl { get; set; } = "";
    public string BeforeFingerprint { get; set; } = "";
    public string AfterFingerprint { get; set; } = "";
    public bool ValueMatches { get; set; }
    public bool SelectedChanged { get; set; }
    public bool CheckedChanged { get; set; }
    public bool Navigation { get; set; }
    public bool NativeInputUsed { get; set; }
    public int? WindowId { get; set; }
    public int? TabId { get; set; }
    public long GraphVersion { get; set; }
    public string DomEpoch { get; set; } = "";
    public BrowserRectangle TargetBounds { get; set; } = new();
    public string ScreenshotDataUrl { get; set; } = "";
}

internal static class BrowserHelperProtocol
{
    public const string Version = "2.1.21";
    public const string PackageVersion = "2.1.27";
    public const string StoreExtensionId = "gojdebdikepkpapmnggeljneamjdigbh";
    public const string DevelopmentExtensionId = "gfndpohemndahmmeealkdobepjacblnf";
    public const string ExtensionId = StoreExtensionId;
    public const string NativeHostName = "wtf.keyboard.browser";
#if KEYBOARD_WTF_PAID_PRODUCTION_CANDIDATE
    // The side-by-side verification build must not attach its Chrome native
    // host to an already-running stable/BYOK process. Production and normal
    // upgrades retain the established pipe name below.
    public const string PipeName = "keyboard.wtf.browser.v2.paid-production-candidate";
#else
    public const string PipeName = "keyboard.wtf.browser.v2";
#endif

    public static IReadOnlyList<string> AllowedExtensionIds { get; } =
        new[] { StoreExtensionId, DevelopmentExtensionId };

    public static IReadOnlyList<string> AllowedExtensionOrigins { get; } =
        AllowedExtensionIds
            .Select(id => $"chrome-extension://{id}/")
            .ToArray();

    public static bool IsAllowedExtensionId(string extensionId) =>
        !string.IsNullOrWhiteSpace(extensionId)
        && AllowedExtensionIds.Contains(extensionId, StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowedExtensionOrigin(string origin) =>
        !string.IsNullOrWhiteSpace(origin)
        && AllowedExtensionOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
}
