namespace KeyboardWtf.Automation.Input;

public sealed class InputExecutionResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public string Strategy { get; init; } = "";
    public int KeysSent { get; init; }
    public bool ConfirmationRequired { get; init; }
    public bool Blocked { get; init; }

    public static InputExecutionResult Success(string message, string strategy, int keysSent = 1) =>
        new() { Ok = true, Message = message, Strategy = strategy, KeysSent = keysSent };

    public static InputExecutionResult Failed(string message, string strategy = "input") =>
        new() { Ok = false, Message = message, Strategy = strategy };

    public static InputExecutionResult NeedsConfirmation(string message, string strategy = "input-safety") =>
        new() { Ok = false, Message = message, Strategy = strategy, ConfirmationRequired = true };

    public static InputExecutionResult Block(string message, string strategy = "input-safety") =>
        new() { Ok = false, Message = message, Strategy = strategy, Blocked = true };
}
