namespace KeyboardWtf.Automation.Input;

public sealed class InputChord
{
    public IReadOnlyList<InputKey> Keys { get; init; } = Array.Empty<InputKey>();

    public string Display => string.Join("+", Keys.Select(DisplayKey));

    public static InputChord From(params InputKey[] keys) =>
        new() { Keys = keys ?? Array.Empty<InputKey>() };

    public static bool TryParse(string value, out InputChord chord)
    {
        chord = null;
        var parts = (value ?? "")
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;
        var keys = new List<InputKey>();
        foreach (var part in parts)
        {
            if (!InputKeyParser.TryParse(part, out var key))
                return false;
            keys.Add(key);
        }
        chord = new InputChord { Keys = keys };
        return true;
    }

    private static string DisplayKey(InputKey key) => key switch
    {
        InputKey.Control => "Ctrl",
        InputKey.Windows => "Win",
        InputKey.ArrowUp => "ArrowUp",
        InputKey.ArrowDown => "ArrowDown",
        InputKey.ArrowLeft => "ArrowLeft",
        InputKey.ArrowRight => "ArrowRight",
        _ => key.ToString(),
    };
}
