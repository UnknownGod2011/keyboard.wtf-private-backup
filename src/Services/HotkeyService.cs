namespace KeyboardWtf.Services;

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    public const string JarvisFallback = "Ctrl+Alt+J";
    public const string CancelFallback = "Ctrl+Alt+Escape";

    private readonly ConcurrentDictionary<int, (string Name, string Hotkey, Action Handler)> _handlers = new();
    private readonly object _hotkeyGate = new();
    private readonly Func<int, uint, uint, bool> _registerHotKey;
    private readonly Func<int, bool> _unregisterHotKey;
    private readonly HotkeyMessageWindow _messageWindow;
    private int _nextId = 100;
    private bool _disposed;

    public event Action<string, string> RegistrationFailed;
    public event Action<string, string, string> RegistrationFallbackUsed;

    public HotkeyService()
    {
        // RegisterHotKey(NULL, ...) binds delivery to the calling thread's queue.
        // Settings updates run on an HTTP worker thread, so Windows could accept a
        // shortcut that would never be dispatched. A dedicated UI-thread window
        // gives every registration one stable delivery target.
        _messageWindow = new HotkeyMessageWindow(DispatchHotkey);
        _messageWindow.EnsureHandle();
        _registerHotKey = (id, modifiers, key) =>
            _messageWindow.Invoke(() => RegisterHotKey(_messageWindow.Handle, id, modifiers, key));
        _unregisterHotKey = id =>
            _messageWindow.Invoke(() => UnregisterHotKey(_messageWindow.Handle, id));
    }

    internal HotkeyService(
        Func<int, uint, uint, bool> registerHotKey,
        Func<int, bool> unregisterHotKey)
    {
        _registerHotKey = registerHotKey ?? throw new ArgumentNullException(nameof(registerHotKey));
        _unregisterHotKey = unregisterHotKey ?? throw new ArgumentNullException(nameof(unregisterHotKey));
    }

    public IReadOnlyDictionary<string, string> ActiveRegistrations
    {
        get
        {
            return _handlers.Values.ToDictionary(
                entry => entry.Name,
                entry => entry.Hotkey,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool RegisterDefaults(HotkeySettings settings, CommandRegistry commands)
    {
        lock (_hotkeyGate)
            return RegisterDefaultsCore(settings, commands);
    }

    private bool RegisterDefaultsCore(HotkeySettings settings, CommandRegistry commands)
    {
        UnregisterAll();

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
        HotkeySettings previousSettings,
        CommandRegistry commands,
        bool requireAllRequested,
        out string error)
    {
        lock (_hotkeyGate)
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

            var requested = new (string Name, string Hotkey, Action Handler)[]
            {
                ("SmartWriting", settings.PushToTalk, commands.ToggleSmartMode),
                ("Dictation", settings.Dictation, commands.ToggleDictation),
                ("Jarvis", settings.Jarvis, commands.ToggleJarvisMode),
                ("Cancel", settings.Cancel, commands.CancelCurrent),
                ("OpenSettings", settings.OpenSettings, commands.OpenSettings),
            };

            var previous = (previousSettings ?? HotkeySettings.Defaults()).ToDictionary();
            var changed = requireAllRequested
                ? requested
                : requested.Where(binding =>
                {
                    var settingName = binding.Name == "SmartWriting" ? "PushToTalk" : binding.Name;
                    return !previous.TryGetValue(settingName, out var oldValue)
                        || !string.Equals(
                            Normalize(oldValue),
                            Normalize(binding.Hotkey),
                            StringComparison.OrdinalIgnoreCase);
                }).ToArray();

            return TryReplaceBindings(changed, out error);
        }
    }

    internal bool TryReplaceForTests(
        IReadOnlyDictionary<string, string> requested,
        IReadOnlyDictionary<string, string> previous,
        bool requireAllRequested,
        out string error)
    {
        lock (_hotkeyGate)
            return TryReplaceBindings(
                requested.Where(pair => requireAllRequested
                        || previous is null
                        || !previous.TryGetValue(pair.Key, out var previousValue)
                        || !string.Equals(Normalize(previousValue), Normalize(pair.Value), StringComparison.OrdinalIgnoreCase))
                    .Select(pair => (
                        pair.Key.Equals("PushToTalk", StringComparison.OrdinalIgnoreCase) ? "SmartWriting" : pair.Key,
                        pair.Value,
                        (Action)(() => { }))).ToArray(),
                out error);
    }

    internal bool RegisterForIntegrationTest(string name, string hotkey, Action handler, out string error)
    {
        lock (_hotkeyGate)
            return TryRegister(name, hotkey, handler, out error);
    }

    private bool TryReplaceBindings(
        IReadOnlyList<(string Name, string Hotkey, Action Handler)> requested,
        out string error)
    {
        error = "";

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
        if (!_registerHotKey(id, modifiers, (uint)key))
        {
            failureReason = $"{hotkey} is already in use by Windows or another app.";
            return false;
        }

        _handlers[id] = (name, hotkey, handler);
        registeredId = id;
        AppLog.Info($"Registered hotkey {name}: {hotkey}");
        return true;
    }

    private void DispatchHotkey(int id)
    {
        if (!_handlers.TryGetValue(id, out var entry))
            return;

        AppLog.Info($"Hotkey triggered: {entry.Name}");
        try { entry.Handler(); }
        catch (Exception ex) { AppLog.Error(ex, $"Hotkey handler failed: {entry.Name}"); }
    }

    private void UnregisterAll()
    {
        foreach (var id in _handlers.Keys.ToArray())
            _unregisterHotKey(id);
        _handlers.Clear();
    }

    private void Unregister(int id)
    {
        _unregisterHotKey(id);
        _handlers.TryRemove(id, out _);
    }

    public void Dispose()
    {
        lock (_hotkeyGate)
        {
            if (_disposed) return;
            _disposed = true;
            UnregisterAll();
            _messageWindow?.Dispose();
        }
    }

    private sealed class HotkeyMessageWindow : NativeWindow, IDisposable
    {
        private const int WmInvoke = 0x8000 + 0x51;
        private readonly Action<int> _dispatch;
        private readonly ConcurrentDictionary<int, Action> _pendingInvocations = new();
        private readonly int _ownerManagedThreadId = Environment.CurrentManagedThreadId;
        private int _nextInvocationId;

        public HotkeyMessageWindow(Action<int> dispatch) =>
            _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));

        public void EnsureHandle()
        {
            if (Handle != IntPtr.Zero)
                return;

            CreateHandle(new CreateParams
            {
                Caption = "keyboard.wtf hotkey dispatcher",
                Parent = new IntPtr(-3), // HWND_MESSAGE
            });
        }

        public T Invoke<T>(Func<T> operation)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));
            if (Environment.CurrentManagedThreadId == _ownerManagedThreadId)
                return operation();
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException("The hotkey dispatcher is unavailable.");

            T result = default;
            Exception failure = null;
            var invocationId = Interlocked.Increment(ref _nextInvocationId);
            _pendingInvocations[invocationId] = () =>
            {
                try { result = operation(); }
                catch (Exception exception) { failure = exception; }
            };

            SendMessage(Handle, WmInvoke, new IntPtr(invocationId), IntPtr.Zero);
            if (_pendingInvocations.TryRemove(invocationId, out _))
                throw new InvalidOperationException("The hotkey dispatcher did not process the registration request.");
            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey)
            {
                _dispatch(message.WParam.ToInt32());
                return;
            }
            if (message.Msg == WmInvoke)
            {
                if (_pendingInvocations.TryRemove(message.WParam.ToInt32(), out var invocation))
                    invocation();
                return;
            }

            base.WndProc(ref message);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                DestroyHandle();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
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
                if (parsed is >= Keys.F13 and <= Keys.F24)
                    return false;
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
