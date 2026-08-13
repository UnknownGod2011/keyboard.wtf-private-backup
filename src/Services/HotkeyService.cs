namespace KeyboardWtf.Services;

using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed class HotkeyService : IMessageFilter, IDisposable
{
    private const int WmHotkey = 0x0312;
    public const string JarvisFallback = "Ctrl+Alt+J";
    public const string CancelFallback = "Ctrl+Alt+Escape";

    private readonly Dictionary<int, (string Name, string Hotkey, Action Handler)> _handlers = new();
    private int _nextId = 100;
    private bool _disposed;
    private bool _filterAdded;

    public event Action<string, string> RegistrationFailed;
    public event Action<string, string, string> RegistrationFallbackUsed;

    public IReadOnlyDictionary<string, string> ActiveRegistrations =>
        _handlers.Values.ToDictionary(entry => entry.Name, entry => entry.Hotkey, StringComparer.OrdinalIgnoreCase);

    public bool RegisterDefaults(HotkeySettings settings, CommandRegistry commands)
    {
        UnregisterAll();
        if (!_filterAdded)
        {
            Application.AddMessageFilter(this);
            _filterAdded = true;
        }

        var hotkeys = settings ?? HotkeySettings.Defaults();
        var results = new[]
        {
            Register("SmartWriting", hotkeys.PushToTalk, commands.ToggleSmartMode),
            Register("Dictation", hotkeys.Dictation, commands.ToggleDictation),
            Register("Jarvis", hotkeys.Jarvis, commands.ToggleJarvisMode, JarvisFallback),
            Register("Cancel", hotkeys.Cancel, commands.CancelCurrent, CancelFallback),
            Register("OpenSettings", hotkeys.OpenSettings, commands.OpenSettings),
        };
        return results.All(result => result);
    }

    public bool TryReplaceAll(
        HotkeySettings settings,
        CommandRegistry commands,
        out string error)
    {
        error = "";
        if (settings == null || commands == null)
        {
            error = "Hotkey settings are unavailable.";
            return false;
        }
        if (HasDuplicates(settings, out var duplicate))
        {
            error = $"Duplicate hotkey: {duplicate}";
            return false;
        }

        if (!_filterAdded)
        {
            Application.AddMessageFilter(this);
            _filterAdded = true;
        }
        var requested = new (string Name, string Hotkey, Action Handler)[]
        {
            ("SmartWriting", settings.PushToTalk, commands.ToggleSmartMode),
            ("Dictation", settings.Dictation, commands.ToggleDictation),
            ("Jarvis", settings.Jarvis, commands.ToggleJarvisMode),
            ("Cancel", settings.Cancel, commands.CancelCurrent),
            ("OpenSettings", settings.OpenSettings, commands.OpenSettings),
        };

        var oldByName = _handlers.ToDictionary(
            pair => pair.Value.Name,
            pair => (Id: pair.Key, pair.Value.Hotkey, pair.Value.Handler),
            StringComparer.OrdinalIgnoreCase);
        var stagedIds = new List<int>();
        foreach (var binding in requested)
        {
            if (oldByName.TryGetValue(binding.Name, out var current)
                && string.Equals(
                    Normalize(current.Hotkey),
                    Normalize(binding.Hotkey),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryRegister(binding.Name, binding.Hotkey, binding.Handler, out var reason, out var stagedId))
            {
                stagedIds.Add(stagedId);
                continue;
            }

            foreach (var staged in stagedIds)
                Unregister(staged);
            error = reason;
            AppLog.Warning($"Hotkey replacement rolled back: {error}");
            return false;
        }

        foreach (var binding in requested)
        {
            if (!oldByName.TryGetValue(binding.Name, out var current)
                || string.Equals(
                    Normalize(current.Hotkey),
                    Normalize(binding.Hotkey),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            Unregister(current.Id);
        }

        AppLog.Info("Hotkey replacement committed atomically");
        return true;
    }

    public static bool HasDuplicates(HotkeySettings settings, out string duplicate)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (settings ?? HotkeySettings.Defaults()).ToDictionary())
        {
            var normalized = Normalize(pair.Value);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;
            if (!seen.Add(normalized))
            {
                duplicate = pair.Value;
                return true;
            }
        }

        duplicate = null;
        return false;
    }

    private bool Register(string name, string hotkey, Action handler, string fallback = null)
    {
        if (TryRegister(name, hotkey, handler, out var primaryReason))
            return true;

        if (!string.IsNullOrWhiteSpace(fallback)
            && !string.Equals(Normalize(hotkey), Normalize(fallback), StringComparison.OrdinalIgnoreCase)
            && TryRegister(name, fallback, handler, out var fallbackReason))
        {
            RegistrationFallbackUsed?.Invoke(name, hotkey, fallback);
            AppLog.Warning($"{name} hotkey {hotkey} was unavailable; registered fallback {fallback}");
            return true;
        }

        RegistrationFailed?.Invoke(
            name,
            string.IsNullOrWhiteSpace(fallback)
                ? primaryReason
                : $"{primaryReason} Fallback {fallback} was also unavailable.");
        return false;
    }

    private bool TryRegister(
        string name,
        string hotkey,
        Action handler,
        out string failureReason) =>
        TryRegister(name, hotkey, handler, out failureReason, out _);

    private bool TryRegister(
        string name,
        string hotkey,
        Action handler,
        out string failureReason,
        out int registeredId)
    {
        failureReason = "";
        registeredId = 0;
        if (!TryParse(hotkey, out var modifiers, out var key))
        {
            failureReason = $"Invalid hotkey: {hotkey}";
            return false;
        }

        var id = _nextId++;
        if (!RegisterHotKey(IntPtr.Zero, id, modifiers, (uint)key))
        {
            failureReason = $"{hotkey} is already in use by Windows or another app.";
            return false;
        }

        _handlers[id] = (name, hotkey, handler);
        registeredId = id;
        AppLog.Info($"Registered hotkey {name}: {hotkey}");
        return true;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WmHotkey)
            return false;

        var id = m.WParam.ToInt32();
        if (_handlers.TryGetValue(id, out var entry))
        {
            try { entry.Handler(); }
            catch (Exception ex) { AppLog.Error(ex, $"Hotkey handler failed: {entry.Name}"); }
            return true;
        }

        return false;
    }

    private void UnregisterAll()
    {
        foreach (var id in _handlers.Keys.ToArray())
            UnregisterHotKey(IntPtr.Zero, id);
        _handlers.Clear();
    }

    private void Unregister(int id)
    {
        UnregisterHotKey(IntPtr.Zero, id);
        _handlers.Remove(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_filterAdded)
        {
            Application.RemoveMessageFilter(this);
            _filterAdded = false;
        }
        UnregisterAll();
    }

    private static string Normalize(string hotkey) =>
        string.Join("+", (hotkey ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Equals("Control", StringComparison.OrdinalIgnoreCase) ? "Ctrl" : p)
            .Select(p => p.ToUpperInvariant()));

    internal static bool TryParse(string hotkey, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;
        var keyCount = 0;

        foreach (var raw in (hotkey ?? string.Empty).Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || raw.Equals("Control", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModControl;
            else if (raw.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModAlt;
            else if (raw.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModShift;
            else if (raw.Equals("Win", StringComparison.OrdinalIgnoreCase) || raw.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModWin;
            else if (raw == ",")
            {
                key = Keys.Oemcomma;
                keyCount++;
            }
            else if (raw.Equals("Space", StringComparison.OrdinalIgnoreCase))
            {
                key = Keys.Space;
                keyCount++;
            }
            else if (Enum.TryParse<Keys>(raw, true, out var parsed))
            {
                key = parsed;
                keyCount++;
            }
            else
                return false;
        }

        return keyCount == 1
            && key != Keys.None
            && modifiers != 0
            && !IsReservedWindowsCombination(modifiers, key);
    }

    private static bool IsReservedWindowsCombination(uint modifiers, Keys key) =>
        (modifiers == ModAlt && key is Keys.F4 or Keys.Tab)
        || (modifiers == (ModControl | ModShift) && key == Keys.Escape)
        || (modifiers == (ModControl | ModAlt) && key == Keys.Delete)
        || (modifiers == ModWin && key is Keys.L or Keys.D or Keys.E or Keys.R or Keys.Tab);

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
