namespace KeyboardWtf.Automation.Input;

public enum InputKey
{
    Enter,
    Space,
    Tab,
    Escape,
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    Home,
    End,
    PageUp,
    PageDown,
    Backspace,
    Delete,
    Control,
    Alt,
    Shift,
    Windows,
    A,
    C,
    F,
    L,
    R,
    S,
    T,
    V,
    W,
}

public static class InputKeyParser
{
    public static bool TryParse(string value, out InputKey key)
    {
        var normalized = Normalize(value);
        var recognized = true;
        key = normalized switch
        {
            "enter" or "return" => InputKey.Enter,
            "space" or "spacebar" => InputKey.Space,
            "tab" => InputKey.Tab,
            "escape" or "esc" => InputKey.Escape,
            "up" or "arrowup" or "arrow up" => InputKey.ArrowUp,
            "down" or "arrowdown" or "arrow down" => InputKey.ArrowDown,
            "left" or "arrowleft" or "arrow left" => InputKey.ArrowLeft,
            "right" or "arrowright" or "arrow right" => InputKey.ArrowRight,
            "home" => InputKey.Home,
            "end" => InputKey.End,
            "pageup" or "page up" or "pgup" => InputKey.PageUp,
            "pagedown" or "page down" or "pgdn" => InputKey.PageDown,
            "backspace" => InputKey.Backspace,
            "delete" or "del" => InputKey.Delete,
            "ctrl" or "control" => InputKey.Control,
            "alt" => InputKey.Alt,
            "shift" => InputKey.Shift,
            "win" or "windows" or "windows key" => InputKey.Windows,
            "a" => InputKey.A,
            "c" => InputKey.C,
            "f" => InputKey.F,
            "l" => InputKey.L,
            "r" => InputKey.R,
            "s" => InputKey.S,
            "t" => InputKey.T,
            "v" => InputKey.V,
            "w" => InputKey.W,
            _ => Unknown(),
        };
        return recognized;

        InputKey Unknown()
        {
            recognized = false;
            return default;
        }
    }

    private static string Normalize(string value) =>
        string.Join(
            " ",
            (value ?? "")
                .Trim()
                .Replace("+", " ", StringComparison.Ordinal)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
}
