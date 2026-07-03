namespace KeyboardWtf.Automation.Input;

using KeyboardWtf.Models;

public enum InputSafetyContextKind
{
    Unknown,
    ReadOnlyNavigation,
    FormFillPreview,
    CartMutationApproved,
    DestructiveAction,
    PaymentOrCheckout,
}

public enum InputRisk
{
    Safe,
    NeedsConfirmation,
    Blocked,
}

public sealed class InputSafetyContext
{
    public InputSafetyContextKind Kind { get; init; } = InputSafetyContextKind.Unknown;
    public BrowserCompanionFocusedElement FocusedElement { get; init; }
    public bool UpfrontCartMutationApproved { get; init; }
    public bool UserExplicitlyConfirmed { get; init; }
    public string TaskDescription { get; init; } = "";
}

public sealed class InputSafetyDecision
{
    public InputRisk Risk { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class InputSafetyPolicy
{
    public InputSafetyDecision Evaluate(InputAction action, InputSafetyContext context)
    {
        context ??= new InputSafetyContext();
        if (action == null)
            return Blocked("Input action is missing.");

        if (context.Kind == InputSafetyContextKind.PaymentOrCheckout)
            return Blocked("Payment, checkout, and order-placement flows are manual-only.");
        if (context.Kind == InputSafetyContextKind.DestructiveAction && !context.UserExplicitlyConfirmed)
            return NeedsConfirmation("This input could perform a destructive or irreversible action.");
        if (context.FocusedElement?.Sensitive == true)
            return Blocked("Focused control is sensitive. Jarvis will not type or activate password, OTP, payment, or private fields.");

        if (action.Kind == InputActionKind.PressChord)
            return EvaluateChord(action.Chord, context);
        if (action.Kind == InputActionKind.TypeText)
            return EvaluateTyping(context);
        if (action.Kind == InputActionKind.ActivateFocusedControl)
            return EvaluateActivation(action, context);
        if (action.Kind == InputActionKind.PressKey)
            return EvaluateKey(action.Key, context);

        return Blocked("Unsupported input action.");
    }

    private static InputSafetyDecision EvaluateKey(InputKey? key, InputSafetyContext context)
    {
        if (key is null)
            return Blocked("Key is missing.");
        return key.Value switch
        {
            InputKey.Tab or InputKey.Escape or InputKey.ArrowUp or InputKey.ArrowDown
                or InputKey.ArrowLeft or InputKey.ArrowRight or InputKey.Home or InputKey.End
                or InputKey.PageUp or InputKey.PageDown => Safe("Navigation key is safe."),
            InputKey.Enter or InputKey.Space => context.FocusedElement == null || context.FocusedElement.EnterLikelySafe || context.FocusedElement.SpaceLikelySafe
                ? Safe("Activation key is safe in current context.")
                : NeedsConfirmation("Enter/Space may activate an ambiguous focused control."),
            InputKey.Backspace or InputKey.Delete => NeedsConfirmation("Delete/backspace can remove content."),
            _ => NeedsConfirmation("This key requires confirmation outside a known safe context."),
        };
    }

    private static InputSafetyDecision EvaluateActivation(InputAction action, InputSafetyContext context)
    {
        var focus = context.FocusedElement;
        if (focus == null || !focus.Exists)
            return NeedsConfirmation("No focused browser control was verified.");
        var label = $"{focus.ControlType} {focus.Role} {focus.Text} {focus.AriaLabel} {focus.NearbyText}".ToLowerInvariant();
        if (label.Contains("checkout") || label.Contains("payment") || label.Contains("place order") || label.Contains("pay now"))
            return Blocked("Checkout/payment controls are manual-only.");
        if (label.Contains("delete") || label.Contains("archive") || label.Contains("send") || label.Contains("publish"))
            return context.UserExplicitlyConfirmed
                ? Safe("User explicitly confirmed the risky focused action.")
                : NeedsConfirmation("Focused control can send, publish, archive, or delete.");
        if (label.Contains("add to cart") || label.Contains("add to basket"))
            return context.UpfrontCartMutationApproved || context.Kind == InputSafetyContextKind.CartMutationApproved
                ? Safe("Cart mutation was approved upfront.")
                : NeedsConfirmation("Adding to cart needs one upfront confirmation.");
        if (action.Key == InputKey.Enter && focus.EnterLikelySafe)
            return Safe("Focused control reports Enter as safe.");
        if (action.Key == InputKey.Space && focus.SpaceLikelySafe)
            return Safe("Focused control reports Space as safe.");
        return context.Kind is InputSafetyContextKind.ReadOnlyNavigation or InputSafetyContextKind.FormFillPreview
            ? Safe("Focused read-only/form-preview activation is safe.")
            : NeedsConfirmation("Focused activation is ambiguous.");
    }

    private static InputSafetyDecision EvaluateTyping(InputSafetyContext context)
    {
        if (context.FocusedElement?.Sensitive == true)
            return Blocked("Focused field is sensitive.");
        return context.Kind switch
        {
            InputSafetyContextKind.FormFillPreview or InputSafetyContextKind.ReadOnlyNavigation => Safe("Typing is safe in the verified text field context."),
            _ => NeedsConfirmation("Typing requires a verified non-sensitive focused field."),
        };
    }

    private static InputSafetyDecision EvaluateChord(InputChord chord, InputSafetyContext context)
    {
        if (chord?.Keys == null || chord.Keys.Count == 0)
            return Blocked("Shortcut is missing.");
        var display = chord.Display.ToLowerInvariant();

        if (display is "alt+f4" or "ctrl+shift+delete" or "win+l")
            return Blocked("This shortcut is blocked because it can close, lock, or erase browser data.");
        if (display.Contains("win") && (display.Contains("r") || display.Contains("x")))
            return Blocked("System launch/admin shortcuts are blocked.");
        if (display == "ctrl+w")
            return context.UserExplicitlyConfirmed
                ? Safe("User confirmed closing a tab/window.")
                : NeedsConfirmation("Closing a tab/window can lose unsaved work.");
        if (display == "ctrl+r")
            return context.Kind == InputSafetyContextKind.ReadOnlyNavigation
                ? Safe("Reload is safe in read-only navigation context.")
                : NeedsConfirmation("Reloading can lose unsaved form edits.");

        var safe = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ctrl+l",
            "ctrl+f",
            "ctrl+t",
            "ctrl+tab",
            "ctrl+shift+tab",
            "ctrl+c",
            "ctrl+v",
            "ctrl+a",
            "ctrl+s",
            "shift+tab",
            "shift+enter",
            "ctrl+enter",
        };
        if (safe.Contains(display))
        {
            if (display is "ctrl+v" or "ctrl+a" or "ctrl+s")
                return context.Kind is InputSafetyContextKind.FormFillPreview or InputSafetyContextKind.ReadOnlyNavigation
                    ? Safe("Shortcut is safe in the verified context.")
                    : NeedsConfirmation("Clipboard/select/save shortcuts require a verified safe context.");
            return Safe("Shortcut is safe.");
        }

        if (display == "alt+tab")
            return context.UserExplicitlyConfirmed
                ? Safe("User confirmed app switching.")
                : NeedsConfirmation("Alt+Tab requires visible user intent.");

        return NeedsConfirmation("Shortcut is not on the safe allowlist.");
    }

    private static InputSafetyDecision Safe(string reason) => new() { Risk = InputRisk.Safe, Reason = reason };
    private static InputSafetyDecision NeedsConfirmation(string reason) => new() { Risk = InputRisk.NeedsConfirmation, Reason = reason };
    private static InputSafetyDecision Blocked(string reason) => new() { Risk = InputRisk.Blocked, Reason = reason };
}
