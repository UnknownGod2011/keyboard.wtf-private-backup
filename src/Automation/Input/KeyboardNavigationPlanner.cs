namespace KeyboardWtf.Automation.Input;

using KeyboardWtf.Models;
using KeyboardWtf.Services;

public sealed class KeyboardNavigationPlan
{
    public string TargetDescription { get; init; } = "";
    public int MaxTabPresses { get; init; } = 40;
    public TimeSpan MaxRuntime { get; init; } = TimeSpan.FromSeconds(12);
    public InputSafetyContext SafetyContext { get; init; } = new();
}

public sealed class KeyboardNavigationProgress
{
    public int TabPresses { get; init; }
    public BrowserCompanionFocusedElement FocusedElement { get; init; }
    public double Score { get; init; }
}

public static class KeyboardNavigationPlanner
{
    public static double ScoreFocus(BrowserCompanionFocusedElement focus, string targetDescription)
    {
        if (focus == null || !focus.Exists || focus.Sensitive)
            return 0;
        var target = FuzzyMatcher.Normalize(targetDescription);
        if (string.IsNullOrWhiteSpace(target))
            return 0;
        var text = FuzzyMatcher.Normalize(string.Join(
            " ",
            focus.ControlType,
            focus.Role,
            focus.Text,
            focus.AriaLabel,
            focus.Title,
            focus.Placeholder,
            focus.NearbyText,
            focus.CardContext,
            focus.FormContext,
            focus.MenuContext,
            focus.ModalContext,
            focus.Href));
        if (string.IsNullOrWhiteSpace(text))
            return 0;
        var fuzzy = FuzzyMatcher.Score(target, text);
        var targetTokens = target.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 3)
            .ToArray();
        var hits = targetTokens.Count(token => text.Contains(token, StringComparison.Ordinal));
        var overlap = targetTokens.Length == 0 ? 0 : hits / (double)targetTokens.Length;
        var actionBonus = focus.EnterLikelySafe || focus.SpaceLikelySafe ? 0.08 : 0;
        return Math.Clamp(Math.Max(fuzzy, overlap) + actionBonus, 0, 1);
    }
}
