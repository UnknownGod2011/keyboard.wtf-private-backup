namespace KeyboardWtf.Automation.Vision;

public sealed class DesktopVisionFallback : IScreenVisionService
{
    public Task<IReadOnlyList<VisualTarget>> FindTargetsAsync(string description, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<VisualTarget>>(Array.Empty<VisualTarget>());
}
