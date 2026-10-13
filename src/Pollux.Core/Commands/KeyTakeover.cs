namespace Pollux.Core.Commands;

/// <summary>Windows keys Pollux may take over with its low-level keyboard hook (each one opt-in).</summary>
[Flags]
public enum Takeovers
{
    None = 0,
    WinE = 1,
    WinV = 2,
    WinShiftS = 4,
    PrintKey = 8,
}

/// <summary>What the hook does with a key: let it through (<see cref="None"/>), run an action, or only swallow it.</summary>
public enum TakenKey { None, WinE, WinV, SnapArea, SnapWindow, SwallowOnly }

/// <summary>Which modifier keys are physically down.</summary>
public readonly record struct HeldModifiers(bool Win, bool Shift, bool Ctrl, bool Alt);

/// <summary>The low-level keyboard hook's decision, kept free of Win32 so it can be tested.</summary>
public static class KeyTakeover
{
    private const uint VkE = 0x45, VkV = 0x56, VkS = 0x53, VkSnapshot = 0x2C;

    /// <summary>
    /// Win+E and Win+V with no other modifier; Win+Shift+S with exactly Win and Shift; Print alone (area) or Alt+Print
    /// (window). Releasing a taken Print key is swallowed too, so Windows never sees half of it.
    /// </summary>
    public static TakenKey Decide(uint vk, bool keyUp, HeldModifiers m, Takeovers on)
    {
        if (vk == VkSnapshot && on.HasFlag(Takeovers.PrintKey) && !m.Ctrl && !m.Shift && !m.Win)
            return keyUp ? TakenKey.SwallowOnly : m.Alt ? TakenKey.SnapWindow : TakenKey.SnapArea;
        if (keyUp || !m.Win || m.Ctrl || m.Alt) return TakenKey.None;
        return vk switch
        {
            VkE when !m.Shift && on.HasFlag(Takeovers.WinE) => TakenKey.WinE,
            VkV when !m.Shift && on.HasFlag(Takeovers.WinV) => TakenKey.WinV,
            VkS when m.Shift && on.HasFlag(Takeovers.WinShiftS) => TakenKey.SnapArea,
            _ => TakenKey.None,
        };
    }
}
