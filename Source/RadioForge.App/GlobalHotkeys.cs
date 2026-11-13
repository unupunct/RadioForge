using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using RadioForge.Core;

namespace RadioForge.App;

/// <summary>System-wide hotkeys via RegisterHotKey for the actions the user marked "global". Failures (key already owned by another app) are reported, never fatal.</summary>
public sealed class GlobalHotkeys : IDisposable
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    private const int WM_HOTKEY = 0x0312;
    private readonly HwndSource _src; private readonly Action<string> _run;
    private readonly Dictionary<int, string> _ids = new();
    public Dictionary<string, string> Failures { get; } = new();

    public GlobalHotkeys(Window w, Action<string> run)
    {
        _run = run; _src = HwndSource.FromHwnd(new WindowInteropHelper(w).Handle)!; _src.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _ids.TryGetValue(wParam.ToInt32(), out var action)) { handled = true; _run(action); }
        return IntPtr.Zero;
    }

    /// <summary>Parses "Ctrl+Shift+B" into modifiers + virtual key. Returns false when the combo cannot be global (no modifier and not an F-key).</summary>
    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0; var parts = combo.Split('+', StringSplitOptions.TrimEntries); if (parts.Length == 0) return false;
        foreach (var p in parts[..^1]) mods |= p.ToLowerInvariant() switch { "ctrl" => 2u, "shift" => 4u, "alt" => 1u, _ => 0u };
        var name = parts[^1]; if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name;
        if (!Enum.TryParse<Key>(name, true, out var key)) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        bool fkey = key >= Key.F1 && key <= Key.F24;
        return vk != 0 && (mods != 0 || fkey);
    }

    public void Refresh()
    {
        foreach (var id in _ids.Keys) UnregisterHotKey(_src.Handle, id);
        _ids.Clear(); Failures.Clear(); int next = 0xB000;
        foreach (var action in App.Settings.GlobalShortcuts)
        {
            if (!App.Settings.Shortcuts.TryGetValue(action, out var combo)) continue;
            if (!TryParse(combo, out var mods, out var vk)) { Failures[action] = "needs a modifier key (Ctrl/Shift/Alt) or an F-key"; continue; }
            int id = next++;
            if (RegisterHotKey(_src.Handle, id, mods | 0x4000 /*MOD_NOREPEAT*/, vk)) { _ids[id] = action; Log.Info("Hotkeys", $"Global hotkey {combo} → {action}"); }
            else { Failures[action] = $"{combo} is already used by another application"; Log.Warn("Hotkeys", Failures[action]); }
        }
    }

    public void Dispose() { foreach (var id in _ids.Keys) UnregisterHotKey(_src.Handle, id); _ids.Clear(); _src.RemoveHook(Hook); }
}
