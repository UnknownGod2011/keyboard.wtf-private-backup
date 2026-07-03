namespace KeyboardWtf.Automation.Vision;

public interface IScreenVisionService
{
    Task<IReadOnlyList<VisualTarget>> FindTargetsAsync(string description, CancellationToken token);
}
