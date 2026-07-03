namespace KeyboardWtf.Automation.Router;

public sealed class AutomationRouter
{
    public AutomationStrategy Choose(AutomationContext context)
    {
        context ??= new AutomationContext();
        if (context.IsBrowserTask)
        {
            if (context.HasExactWorkflowReplay) return AutomationStrategy.ExactWorkflowReplay;
            if (context.HasParameterizedSitePattern) return AutomationStrategy.ParameterizedSitePatternReplay;
            if (context.BrowserCompanionAvailable) return AutomationStrategy.BrowserCompanionGraphAction;
            if (context.TrustedClickAvailable) return AutomationStrategy.TrustedClickOrType;
            if (context.KeyboardFallbackAllowed) return AutomationStrategy.KeyboardNavigationFallback;
            if (context.BrowserUseRepairEnabled) return AutomationStrategy.BrowserUseRepair;
            return AutomationStrategy.ManualHandoff;
        }

        if (context.NativeApiAvailable) return AutomationStrategy.NativeAppApi;
        if (context.WindowsUiAutomationAvailable) return AutomationStrategy.WindowsUiAutomation;
        if (context.KeyboardFallbackAllowed) return AutomationStrategy.KeyboardNavigationFallback;
        if (context.VisionFallbackAllowed) return AutomationStrategy.DesktopVisionFallback;
        return AutomationStrategy.ManualHandoff;
    }
}
