namespace KeyboardWtf.Automation.Input;

public enum InputActionKind
{
    PressKey,
    PressChord,
    TypeText,
    ActivateFocusedControl,
}

public sealed class InputAction
{
    public InputActionKind Kind { get; init; }
    public InputKey? Key { get; init; }
    public InputChord Chord { get; init; }
    public string Text { get; init; } = "";
    public string Purpose { get; init; } = "";

    public static InputAction Press(InputKey key, string purpose = "") =>
        new() { Kind = InputActionKind.PressKey, Key = key, Purpose = purpose };

    public static InputAction ChordAction(InputChord chord, string purpose = "") =>
        new() { Kind = InputActionKind.PressChord, Chord = chord, Purpose = purpose };

    public static InputAction Activate(InputKey key, string purpose = "") =>
        new() { Kind = InputActionKind.ActivateFocusedControl, Key = key, Purpose = purpose };

    public static InputAction Type(string text, string purpose = "") =>
        new() { Kind = InputActionKind.TypeText, Text = text ?? "", Purpose = purpose };
}
