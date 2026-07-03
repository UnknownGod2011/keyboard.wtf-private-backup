namespace KeyboardWtf.Automation.Input;

public sealed class InputDiagnostics
{
    public string StrategySelected { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> KeysUsed { get; } = new();
    public List<string> FocusedElementsSeen { get; } = new();
    public List<string> SafetyDecisions { get; } = new();
    public bool FallbackTriggered { get; set; }
    public bool Success { get; set; }
    public string FailureReason { get; set; } = "";
}
