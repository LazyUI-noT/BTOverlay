using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace BToverlay;

// Modified combinations use RegisterHotKey. A plain key is observed with a
// pass-through keyboard hook in every foreground app, so the app
// still receives its own key press.
public sealed class Hotkeys : IDisposable
{
    const int WmHotkey = 0x312, WhKeyboardLl = 13;
    const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    const uint NoRepeat = 0x4000;
    readonly HwndSource _source = new(new HwndSourceParameters("BToverlay hotkeys") { ParentWindow = new IntPtr(-3) });
    readonly Dictionary<int, Action> _actions = [];
    readonly Dictionary<uint, Action> _plainActions = [];
    readonly HashSet<uint> _down = [];
    readonly KeyboardProc _keyboardProc;
    IntPtr _keyboardHook;
    public Hotkeys() { _source.AddHook(WindowHook); _keyboardProc = KeyboardHook; }

    public List<string> Apply(UserSettings settings, Action spend, Action add, Action resetCounts, Action resetTurn, Action toggle, Action edit)
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear(); _plainActions.Clear(); _down.Clear();
        if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
        List<string> failed = [];
        var entries = new (string Name, string Combo, Action Action)[]
        {
            ("사용 −1", settings.SpendKey, spend), ("추가 +1", settings.AddKey, add),
            ("아이템 초기화", settings.ResetCountsKey, resetCounts), ("차례 초기화", settings.ResetTurnKey, resetTurn),
            ("오버레이 표시", settings.ToggleKey, toggle), ("편집 모드", settings.EditKey, edit)
        };
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (!TryParse(e.Combo, out var mods, out var key)) { failed.Add(e.Name); continue; }
            if (mods == 0)
            {
                if (!_plainActions.TryAdd(key, e.Action)) failed.Add(e.Name);
            }
            else if (RegisterHotKey(_source.Handle, i + 1, mods | NoRepeat, key)) _actions[i + 1] = e.Action;
            else failed.Add(e.Name);
        }
        if (_plainActions.Count > 0)
        {
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, IntPtr.Zero, 0);
            if (_keyboardHook == IntPtr.Zero)
            {
                foreach (var e in entries.Where(e => TryParse(e.Combo, out var mods, out _) && mods == 0))
                    if (!failed.Contains(e.Name)) failed.Add(e.Name);
                _plainActions.Clear();
            }
        }
        return failed;
    }

    public static bool TryParse(string combo, out uint modifiers, out uint key)
    {
        modifiers = key = 0;
        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part == "컨트롤") modifiers |= 2;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase) || part == "알트") modifiers |= 1;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase) || part == "시프트") modifiers |= 4;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part == "윈도우") modifiers |= 8;
            else
            {
                if (key != 0) return false;
                var name = part.Length == 1 && char.IsAsciiDigit(part[0]) ? "D" + part : part;
                if (!Enum.TryParse<Key>(name, true, out var parsed)) return false;
                var vk = KeyInterop.VirtualKeyFromKey(parsed);
                if (vk <= 0) return false;
                key = (uint)vk;
            }
        }
        return key != 0;
    }

    IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action)) { handled = true; action(); }
        return IntPtr.Zero;
    }
    IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var msg = message.ToInt32();
            var key = (uint)Marshal.ReadInt32(data);
            if (msg is WmKeyUp or WmSysKeyUp) _down.Remove(key);
            else if (msg is WmKeyDown or WmSysKeyDown && _plainActions.ContainsKey(key) && _down.Add(key) &&
                !AnyModifierDown())
                _source.Dispatcher.BeginInvoke(_plainActions[key]);
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }
    static bool AnyModifierDown() =>
        IsDown(0x11) || IsDown(0x10) || IsDown(0x12) || IsDown(0x5B) || IsDown(0x5C);
    static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _source.Dispose();
    }
    delegate IntPtr KeyboardProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")] static extern IntPtr SetWindowsHookEx(int idHook, KeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
}
