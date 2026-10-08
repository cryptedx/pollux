using Pollux.App.Interop;
using Pollux.Core.Commands;
using Windows.System;

namespace Pollux.App.Services;

/// <summary>Translates between Windows virtual keys and Pollux key names, respecting the active keyboard layout (German "&lt;", "ö", …).</summary>
internal static class KeyInput
{
    public static KeyChord? ToChord(VirtualKey key)
    {
        var name = NameOf(key);
        if (name is null) return null;
        var mods = KeyModifiers.None;
        if (IsDown(VirtualKey.Control)) mods |= KeyModifiers.Control;
        if (IsDown(VirtualKey.Shift)) mods |= KeyModifiers.Shift;
        if (IsDown(VirtualKey.Menu)) mods |= KeyModifiers.Alt;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= KeyModifiers.Windows;
        // AltGr arrives as Ctrl+Alt; treat it as plain typing.
        if (mods.HasFlag(KeyModifiers.Control) && mods.HasFlag(KeyModifiers.Alt) && IsDown((VirtualKey)0xA5)) return null;
        return new KeyChord(mods, name);
    }

    public static bool IsDown(VirtualKey key) => (Native.GetKeyState((int)key) & 0x8000) != 0;

    public static string? NameOf(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows
                or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftShift or VirtualKey.RightShift
                or VirtualKey.LeftMenu or VirtualKey.RightMenu:
                return null;
            case >= VirtualKey.A and <= VirtualKey.Z:
                return ((char)('a' + (key - VirtualKey.A))).ToString();
            case >= VirtualKey.Number0 and <= VirtualKey.Number9:
                return ((char)('0' + (key - VirtualKey.Number0))).ToString();
            case >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9:
                return ((char)('0' + (key - VirtualKey.NumberPad0))).ToString();
            case >= VirtualKey.F1 and <= VirtualKey.F24:
                return "f" + (1 + (key - VirtualKey.F1));
        }

        var named = key switch
        {
            VirtualKey.Escape => "escape",
            VirtualKey.Enter => "enter",
            VirtualKey.Tab => "tab",
            VirtualKey.Space => "space",
            VirtualKey.Back => "backspace",
            VirtualKey.Delete => "delete",
            VirtualKey.Insert => "insert",
            VirtualKey.Home => "home",
            VirtualKey.End => "end",
            VirtualKey.PageUp => "pageup",
            VirtualKey.PageDown => "pagedown",
            VirtualKey.Left => "left",
            VirtualKey.Right => "right",
            VirtualKey.Up => "up",
            VirtualKey.Down => "down",
            VirtualKey.Add => "+",
            VirtualKey.Subtract => "-",
            _ => null,
        };
        if (named is not null) return named;

        // OEM keys: ask the layout which character the key produces unshifted.
        var ch = Native.MapVirtualKeyW((uint)key, Native.MAPVK_VK_TO_CHAR) & 0x7FFF;
        return ch == 0 ? null : char.ToLowerInvariant((char)ch).ToString();
    }

    public static uint VirtualKeyFor(string name)
    {
        if (name.Length == 1)
        {
            var c = name[0];
            if (c is >= 'a' and <= 'z') return (uint)char.ToUpperInvariant(c);
            if (c is >= '0' and <= '9') return c;
            var scan = Native.VkKeyScanW(c);
            return scan == -1 ? 0 : (uint)(scan & 0xFF);
        }

        if (name.StartsWith('f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) return (uint)(VirtualKey.F1 + f - 1);
        return name switch
        {
            "space" => (uint)VirtualKey.Space,
            "enter" => (uint)VirtualKey.Enter,
            "escape" => (uint)VirtualKey.Escape,
            "tab" => (uint)VirtualKey.Tab,
            _ => 0,
        };
    }
}
