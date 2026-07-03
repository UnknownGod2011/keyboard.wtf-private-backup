namespace KeyboardWtf.Automation.Router;

public enum AutomationStrategy
{
    ExactWorkflowReplay,
    ParameterizedSitePatternReplay,
    BrowserCompanionGraphAction,
    TrustedClickOrType,
    KeyboardNavigationFallback,
    BrowserUseRepair,
    NativeAppApi,
    WindowsUiAutomation,
    DesktopVisionFallback,
    ManualHandoff,
}
