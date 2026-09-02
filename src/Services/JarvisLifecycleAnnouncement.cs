namespace KeyboardWtf.Services;

public sealed class JarvisLifecycleAnnouncement
{
    public string Source { get; init; } = "";
    public string State { get; init; } = "";
    public string Detail { get; init; } = "";
    public string FallbackSpeech { get; init; } = "";
    public string InterventionKind { get; init; } = "";
    public string MissingInformation { get; init; } = "";
    public string InterventionExplanation { get; init; } = "";
    public bool InterventionRequired { get; init; }
    public bool InterventionCanSkip { get; init; }
}
