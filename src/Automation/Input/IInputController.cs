namespace KeyboardWtf.Automation.Input;

public interface IInputController
{
    Task<InputExecutionResult> PressKeyAsync(InputKey key, InputSafetyContext context, CancellationToken token);
    Task<InputExecutionResult> PressChordAsync(InputChord chord, InputSafetyContext context, CancellationToken token);
}
