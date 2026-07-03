namespace KeyboardWtf.Automation.Input;

using KeyboardWtf.Models;
using KeyboardWtf.Services;

public interface IFocusInspector
{
    Task<BrowserCompanionFocusedElement> InspectBrowserFocusAsync(string taskId, CancellationToken token);
}

public sealed class BrowserCompanionFocusInspector : IFocusInspector
{
    private readonly BrowserCompanionBridgeService _bridge;

    public BrowserCompanionFocusInspector(BrowserCompanionBridgeService bridge)
    {
        _bridge = bridge;
    }

    public async Task<BrowserCompanionFocusedElement> InspectBrowserFocusAsync(string taskId, CancellationToken token)
    {
        if (_bridge?.Status.Available != true)
            return new BrowserCompanionFocusedElement();

        var result = await _bridge.ExecuteAsync(
            new BrowserCompanionCommand
            {
                TaskId = taskId,
                Type = "inspectFocusedElement",
                TimeoutMs = 5000,
            },
            TimeSpan.FromSeconds(5),
            token);
        return result.Ok ? result.FocusedElement ?? new BrowserCompanionFocusedElement() : new BrowserCompanionFocusedElement();
    }
}
