namespace KeyboardWtf.Automation.Vision;

public sealed class VisualTarget
{
    public string Label { get; init; } = "";
    public string Role { get; init; } = "";
    public ScreenRegion Bounds { get; init; } = new();
    public double Confidence { get; init; }
    public bool Sensitive { get; init; }
}
