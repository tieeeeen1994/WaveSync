using WaveSync.Audio;

namespace WaveSync.UI;

/// <summary>System-wide hotkeys via RegisterHotKey on a hidden message-only window.</summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager() => CreateHandle(new CreateParams { Parent = HwndMessage });

    /// <returns>null on success, otherwise why it failed.</returns>
    public string? Register(string keys, Action action)
    {
        if (!TryParse(keys, out var modifiers, out var virtualKey))
        {
            return $"\"{keys}\" isn't a key combination I understand";
        }

        var id = _nextId++;
        if (!NativeMethods.RegisterHotKey(Handle, id, modifiers | ModNoRepeat, virtualKey))
        {
            return $"{keys} is already used by another app";
        }

        _actions[id] = action;
        return null;
    }

    public void Clear()
    {
        foreach (var id in _actions.Keys)
        {
            NativeMethods.UnregisterHotKey(Handle, id);
        }

        _actions.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && _actions.TryGetValue((int)m.WParam, out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("Hotkey action failed", ex);
            }

            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>Parses "Ctrl+Alt+F1", "Ctrl+Shift+1", "Win+Alt+H", "Ctrl+Alt+NumPad3", etc.</summary>
    private static bool TryParse(string text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        Keys? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0]))
                    {
                        key = Keys.D0 + (raw[0] - '0');
                    }
                    else if (!raw.All(char.IsDigit) && Enum.TryParse<Keys>(raw, ignoreCase: true, out var parsed))
                    {
                        key = parsed;
                    }
                    else
                    {
                        return false;
                    }

                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        virtualKey = (uint)(key.Value & Keys.KeyCode);
        return virtualKey != 0;
    }

    public void Dispose()
    {
        Clear();
        DestroyHandle();
    }
}
