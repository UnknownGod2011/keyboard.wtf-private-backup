namespace KeyboardWtf.Automation.Router;

using KeyboardWtf.Automation.Input;

public sealed class AutomationContext
{
    public bool IsBrowserTask { get; init; }
    public bool HasExactWorkflowReplay { get; init; }
    public bool HasParameterizedSitePattern { get; init; }
    public bool BrowserCompanionAvailable { get; init; }
    public bool TrustedClickAvailable { get; init; }
    public bool KeyboardFallbackAllowed { get; init; }
    public bool BrowserUseRepairEnabled { get; init; }
    public bool NativeApiAvailable { get; init; }
    public bool WindowsUiAutomationAvailable { get; init; }
    public bool VisionFallbackAllowed { get; init; }
    public bool RequiresLoggedInBrowser { get; init; }
    public InputSafetyContextKind SafetyContext { get; init; } = InputSafetyContextKind.Unknown;
}
