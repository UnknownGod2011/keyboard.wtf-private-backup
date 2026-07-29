namespace KeyboardWtf.Services.BrowserAutomationV2;

using System.Text.RegularExpressions;

public sealed class BrowserSemanticControlRanker
{
    public IReadOnlyList<BrowserControl> Rank(
        string goal,
        IEnumerable<BrowserControl> controls,
        int limit = 120)
    {
        var terms = Terms(goal);
        return (controls ?? Array.Empty<BrowserControl>())
            .Where(control => control.Visible && control.Enabled)
            .Select(control => new
            {
                Control = control,
                Score = Score(control, terms),
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => Area(item.Control))
            .ThenBy(item => item.Control.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .Select(item => item.Control)
            .ToArray();
    }

    internal static int Score(BrowserControl control, IReadOnlyCollection<string> terms)
    {
        var name = Normalize(control.AccessibleName);
        var text = Normalize(control.Text);
        var label = Normalize(control.NearbyLabel);
        var placeholder = Normalize(control.Placeholder);
        var title = Normalize(control.Title);
        var role = Normalize(control.Role);
        var score = control.Visible && control.Enabled ? 5 : -500;

        foreach (var term in terms)
        {
            if (name == term || text == term || label == term)
                score += 90;
            else
            {
                if (name.Contains(term)) score += 42;
                if (text.Contains(term)) score += 36;
                if (label.Contains(term)) score += 30;
                if (placeholder.Contains(term)) score += 28;
                if (title.Contains(term)) score += 24;
            }
        }

        if (control.Editable && terms.Any(term =>
            term is "search" or "fill" or "type" or "message" or "input" or "field"))
        {
            score += 32;
        }
        if (role is "button" or "tab" or "link" or "textbox" or "combobox"
            or "checkbox" or "radio" or "option" or "menuitem")
        {
            score += 12;
        }
        if (control.Selected || control.Checked)
            score += terms.Any(term => term is "selected" or "checked" or "active") ? 20 : 2;

        var area = Area(control);
        if (area > 180_000)
            score -= 40;
        else if (area is > 0 and < 70_000)
            score += 5;

        return score;
    }

    private static string[] Terms(string goal)
    {
        var normalized = Normalize(goal);
        var words = Regex.Matches(normalized, @"[\p{L}\p{N}]+")
            .Select(match => match.Value)
            .Where(word => word.Length > 2 && !StopWords.Contains(word))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var phrases = new List<string>(words);
        if (words.Length > 1)
        {
            for (var index = 0; index < words.Length - 1; index++)
                phrases.Add($"{words[index]} {words[index + 1]}");
        }
        return phrases.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static double Area(BrowserControl control) =>
        Math.Max(0, control.Bounds?.Width ?? 0)
        * Math.Max(0, control.Bounds?.Height ?? 0);

    private static string Normalize(string value) =>
        Regex.Replace(value ?? "", @"\s+", " ").Trim().ToLowerInvariant();

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "this", "that", "with", "from", "into", "then", "page", "website",
        "browser", "please", "current", "open", "click", "select", "choose",
    };
}
