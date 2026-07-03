namespace KeyboardWtf.Models;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAgentActionKind
{
    Navigate,
    Click,
    Fill,
    Select,
    Upload,
    Press,
    Wait,
    Verify,
    Extract,
}

public sealed class BrowserTaskRequest
{
    public string Title { get; init; } = "";
    public string OriginalUserCommand { get; init; } = "";
    public string StartUrl { get; init; } = "";
    public string ExpectedOutcome { get; init; } = "";
    public bool UseSelectedText { get; init; }
    public bool UseClipboard { get; init; }
    public bool ContextExplicitlyRequested { get; init; }
    public bool UseSmartBackend { get; init; } = true;
    public bool RiskyActionConfirmed { get; init; }
    public bool AutomationProfileFallbackAllowed { get; init; }
}

public sealed class BrowserAgentActionRequest
{
    public string TaskId { get; init; } = "";
    public BrowserAgentActionKind Action { get; init; }
    public string Role { get; init; } = "";
    public string Target { get; init; } = "";
    public string Value { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string ExpectedText { get; init; } = "";
    public bool ExplicitlyRequested { get; init; }
}

public sealed class BrowserElementSummary
{
    public string Role { get; init; } = "";
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Placeholder { get; init; } = "";
    public string Type { get; init; } = "";
}

public sealed class BrowserPageState
{
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string VisibleText { get; init; } = "";
    public string AriaSnapshot { get; init; } = "";
    public IReadOnlyList<BrowserElementSummary> InteractiveElements { get; init; } =
        Array.Empty<BrowserElementSummary>();
    public bool Loaded { get; init; }
}

public sealed class BrowserBackendActionResult
{
    public bool Ok { get; init; }
    public bool Verified { get; init; }
    public bool Ambiguous { get; init; }
    public string Message { get; init; } = "";
    public string Extraction { get; init; } = "";
    public int MatchCount { get; init; }
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();
    public BrowserPageState State { get; init; }
}

public sealed class BrowserAgentResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public string TaskId { get; init; } = "";
    public bool ConfirmationRequired { get; init; }
    public bool ClarificationRequired { get; init; }
    public bool ManualActionRequired { get; init; }
    public string Extraction { get; init; } = "";
    public BrowserPageState Page { get; init; }
    public JarvisTask Task { get; init; }
}

public sealed class BrowserBackendStatus
{
    public bool Initialized { get; init; }
    public bool Available { get; init; }
    public string BrowserChannel { get; init; } = "";
    public string ProfilePath { get; init; } = "";
    public string LastError { get; init; } = "";
    public string ConnectionState { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string ExtensionVersion { get; init; } = "";
    public string ContentScriptVersion { get; init; } = "";
    public string BrowserName { get; init; } = "";
    public string ActiveTabUrl { get; init; } = "";
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public DateTimeOffset? LastGraphReceivedAt { get; init; }
    public DateTimeOffset? LastCommandSentAt { get; init; }
    public DateTimeOffset? LastCommandResultAt { get; init; }
    public string LastCommandType { get; init; } = "";
    public string LastCommandResult { get; init; } = "";
    public string BridgeUrl { get; init; } = "";
    public int BridgePort { get; init; }
    public int LastControlCount { get; init; }
    public IReadOnlyList<string> TopVisibleControls { get; init; } = Array.Empty<string>();
    public bool BrowserUseFallbackEnabled { get; init; }
    public bool AutomationProfileFallbackEnabled { get; init; }
}

public enum BrowserActionRisk
{
    Safe,
    ConfirmationRequired,
    ManualOnly,
    Blocked,
}

public sealed class BrowserSafetyDecision
{
    public BrowserActionRisk Risk { get; init; }
    public string Reason { get; init; } = "";
    public bool ChangesExternalState { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAgentBackendKind
{
    DirectPlaywright,
    BrowserCompanionExtension,
    BrowserUseGemini,
    BrowserOSExternal,
    StagehandExperimental,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserSmartTaskStatus
{
    Completed,
    Failed,
    Blocked,
    QuotaLimited,
    TimedOut,
    Cancelled,
    NeedsConfirmation,
    NeedsClarification,
}

public sealed class BrowserAgentRouteInput
{
    public string Task { get; init; } = "";
    public string StartUrl { get; init; } = "";
    public BrowserAgentActionKind? Action { get; init; }
    public string Domain { get; init; } = "";
    public bool BrowserUseAvailable { get; init; }
    public bool BrowserCompanionAvailable { get; init; }
    public bool BrowserOSAvailable { get; init; }
    public bool AutomationProfileFallbackAllowed { get; init; }
    public bool RequiresLoggedInSession { get; init; }
    public bool DirectDeterministicEnough { get; init; }
    public bool KnownWorkflowMatch { get; init; }
    public bool RiskyActionConfirmed { get; init; }
}

public sealed class BrowserAgentRouteDecision
{
    public BrowserAgentBackendKind SelectedBackend { get; init; }
    public BrowserAgentBackendKind FallbackBackend { get; init; } = BrowserAgentBackendKind.DirectPlaywright;
    public BrowserSafetyDecision Safety { get; init; } = new();
    public bool RequiresBrowserExtension { get; init; }
    public bool UseSmartTaskRunner => SelectedBackend == BrowserAgentBackendKind.BrowserUseGemini;
    public string Reason { get; init; } = "";
}

public sealed class BrowserCompanionControlGraph
{
    public string SessionId { get; init; } = "";
    public int TabId { get; init; }
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public string ExtensionVersion { get; init; } = "";
    public string ContentScriptVersion { get; init; } = "";
    public string BrowserName { get; init; } = "";
    public string PageType { get; init; } = "";
    public string RouteKey { get; init; } = "";
    public int GraphVersion { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public int CandidateCount { get; init; }
    public int VisibleCandidateCount { get; init; }
    public int ShadowRootsScanned { get; init; }
    public IReadOnlyList<string> PageSignals { get; init; } = Array.Empty<string>();
    public IReadOnlyList<BrowserCompanionControl> Controls { get; init; } = Array.Empty<BrowserCompanionControl>();
}

public sealed class BrowserCompanionControl
{
    public string ControlId { get; init; } = "";
    public string Role { get; init; } = "";
    public string TagName { get; init; } = "";
    public string ControlType { get; init; } = "";
    public string PageRegion { get; init; } = "";
    public string Text { get; init; } = "";
    public string AriaLabel { get; init; } = "";
    public string Title { get; init; } = "";
    public string Placeholder { get; init; } = "";
    public string InputType { get; init; } = "";
    public bool Selected { get; init; }
    public bool Checked { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Visible { get; init; } = true;
    public string DisabledReason { get; init; } = "";
    public BrowserCompanionBounds Bounds { get; init; } = new();
    public string Href { get; init; } = "";
    public IReadOnlyList<string> SelectorCandidates { get; init; } = Array.Empty<string>();
    public string DomPathSignature { get; init; } = "";
    public string NearbyText { get; init; } = "";
    public string ParentSection { get; init; } = "";
    public string CardContext { get; init; } = "";
    public string FormContext { get; init; } = "";
    public string MenuContext { get; init; } = "";
    public string ModalContext { get; init; } = "";
    public string TabContext { get; init; } = "";
    public string RepeatingGroup { get; init; } = "";
    public string PositionKind { get; init; } = "";
    public double ConfidenceScore { get; init; }
    public bool Sensitive { get; init; }
    public bool ShadowDom { get; init; }
}

public sealed class BrowserCompanionFocusedElement
{
    public bool Exists { get; init; }
    public string Role { get; init; } = "";
    public string TagName { get; init; } = "";
    public string ControlType { get; init; } = "";
    public string Text { get; init; } = "";
    public string AriaLabel { get; init; } = "";
    public string Title { get; init; } = "";
    public string Placeholder { get; init; } = "";
    public string Href { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Selected { get; init; }
    public bool Checked { get; init; }
    public BrowserCompanionBounds Bounds { get; init; } = new();
    public string NearbyText { get; init; } = "";
    public string CardContext { get; init; } = "";
    public string FormContext { get; init; } = "";
    public string MenuContext { get; init; } = "";
    public string ModalContext { get; init; } = "";
    public bool Sensitive { get; init; }
    public double Confidence { get; init; }
    public bool EnterLikelySafe { get; init; }
    public bool SpaceLikelySafe { get; init; }
}

public sealed class BrowserCompanionBounds
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public sealed class BrowserCompanionCommand
{
    public string CommandId { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string Type { get; init; } = "";
    public string Url { get; init; } = "";
    public string ControlId { get; init; } = "";
    public string Text { get; init; } = "";
    public int TimeoutMs { get; init; } = 15000;
}

public sealed class BrowserCompanionCommandResult
{
    public string CommandId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string TaskId { get; init; } = "";
    public bool Ok { get; init; }
    public string Status { get; init; } = "";
    public string Message { get; init; } = "";
    public int RuntimeMs { get; init; }
    public BrowserCompanionControlGraph Graph { get; init; }
    public BrowserCompanionFocusedElement FocusedElement { get; init; }
}

public sealed class BrowserCompanionTelemetry
{
    public int RuntimeMs { get; set; }
    public int Actions { get; set; }
    public int GraphRefreshes { get; set; }
    public int LlmCalls { get; set; }
    public bool ReplayUsed { get; set; }
    public bool RepairUsed { get; set; }
    public bool WorkflowSaved { get; set; }
    public string Backend { get; set; } = "browser-companion-extension";
}

public sealed class BrowserSmartTaskRequest
{
    public string TaskId { get; init; } = "";
    public string Title { get; init; } = "";
    public string OriginalUserCommand { get; init; } = "";
    public string StartUrl { get; init; } = "";
    public string ContextSummary { get; init; } = "";
    public bool RiskyActionConfirmed { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

public sealed class BrowserSmartTaskStep
{
    public string Title { get; init; } = "";
    public string Status { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class BrowserSmartTaskResult
{
    public bool Ok { get; init; }
    public BrowserSmartTaskStatus Status { get; init; }
    public string Message { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Backend { get; init; } = "";
    public int DurationMs { get; init; }
    public int Steps { get; init; }
    public int Clarifications { get; init; }
    public int Confirmations { get; init; }
    public IReadOnlyList<BrowserSmartTaskStep> Progress { get; init; } = Array.Empty<BrowserSmartTaskStep>();
    public bool CanFallbackToDirect { get; init; }
}

public sealed class BrowserWorkflowMemory
{
    public string WorkflowId { get; set; } = Guid.NewGuid().ToString("N");
    public BrowserWorkflowMemoryKind MemoryKind { get; set; } = BrowserWorkflowMemoryKind.ParameterizedWorkflowTemplate;
    public string Domain { get; set; } = "";
    public string TaskType { get; set; } = "";
    public string TaskPattern { get; set; } = "";
    public string EntityKey { get; set; } = "";
    public List<string> ParameterKeys { get; set; } = new();
    public string FinalUrl { get; set; } = "";
    public List<string> UserIntentExamples { get; set; } = new();
    public string BackendThatDiscoveredIt { get; set; } = "";
    public List<string> LearnedSteps { get; set; } = new();
    public List<string> DeterministicActions { get; set; } = new();
    public List<string> SelectorsOrLocatorHints { get; set; } = new();
    public List<string> SemanticFallbackDescriptions { get; set; } = new();
    public List<string> RequiredContext { get; set; } = new();
    public List<string> SafetyBoundaries { get; set; } = new();
    public bool ConfirmationRequired { get; set; }
    public List<string> HardStopConditions { get; set; } = new();
    public string PageSignature { get; set; } = "";
    public int AverageRuntimeMs { get; set; }
    public int LastRuntimeMs { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public string FailureReason { get; set; } = "";
    public string FailureUrl { get; set; } = "";
    public List<string> FailurePageSignals { get; set; } = new();
    public DateTimeOffset? FailureExpiresAt { get; set; }
    public bool Quarantined { get; set; }
    public string QuarantineReason { get; set; } = "";
    public double Confidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StaleAfter { get; set; } = DateTimeOffset.UtcNow.AddDays(45);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserWorkflowMemoryKind
{
    ParameterizedWorkflowTemplate,
    ExactSuccessfulInstance,
    FailureHint,
    LegacyQuarantined,
}

public sealed class BrowserWorkflowMatch
{
    public BrowserWorkflowMemory Workflow { get; init; }
    public double Score { get; init; }
    public bool HighConfidence => Workflow != null && Score >= 0.78 && Workflow.Confidence >= 0.7;
}
