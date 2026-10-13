using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Pollux.App.Interop;
using Pollux.Core.Commands;

namespace Pollux.App.Services;

/// <summary>
/// Opt-in takeover of Windows keys via one low-level keyboard hook (works without admin; none of them can be taken
/// with RegisterHotKey): Win+E, Win+V, and for Snap Win+Shift+S and the Print key. Which key is taken is decided in
/// Core (<see cref="KeyTakeover"/>). Turning all off removes the hook, which fully restores Windows' behaviour.
/// </summary>
internal static unsafe class WinEHook
{
    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_MASK = 0xE8;
    private static nint _hook;
    private static DispatcherQueue? _dispatcher;
    private static Takeovers _on;
    private static Action<TakenKey>? _handler;

    public static bool IsActive => _hook != 0;

    /// <summary>Takes the keys in <paramref name="on"/> and hands them to <paramref name="handler"/> on the UI thread. The hook exists only while one is on.</summary>
    public static void Apply(DispatcherQueue dispatcher, Takeovers on, Action<TakenKey> handler)
    {
        _dispatcher = dispatcher;
        _on = on;
        _handler = handler;
        if (on == Takeovers.None)
        {
            Disable();
        }
        else if (_hook == 0)
        {
            _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, &HookProc, Native.GetModuleHandleW(null), 0);
        }
    }

    public static void Disable()
    {
        _on = Takeovers.None;
        _handler = null;
        if (_hook != 0)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int code, nint wParam, nint lParam)
    {
        var down = wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN;
        var up = wParam == Native.WM_KEYUP || wParam == Native.WM_SYSKEYUP;
        if (code >= 0 && (down || up))
        {
            var k = (Native.KBDLLHOOKSTRUCT*)lParam;
            var taken = KeyTakeover.Decide(k->vkCode, up, new HeldModifiers(WinDown(), Down(0x10), Down(0x11), Down(0x12)), _on);
            if (taken != TakenKey.None)
            {
                if (taken != TakenKey.SwallowOnly)
                {
                    // Tell the shell a non-Win key was pressed, so releasing Win does not open the Start menu.
                    if (WinDown()) MaskWin();
                    var handler = _handler;
                    _dispatcher?.TryEnqueue(() => handler?.Invoke(taken));
                }

                return 1;
            }
        }

        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static void MaskWin()
    {
        var inputs = stackalloc Native.INPUT[2];
        inputs[0] = new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wVk = (ushort)VK_MASK } };
        inputs[1] = new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wVk = (ushort)VK_MASK, dwFlags = 2 } };
        Native.SendInput(2, inputs, sizeof(Native.INPUT));
    }

    // GetAsyncKeyState: the physical state. GetKeyState would read this thread's snapshot, which is stale while Pollux is in the background.
    private static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool WinDown() => Down((int)VK_LWIN) || Down((int)VK_RWIN);
}
