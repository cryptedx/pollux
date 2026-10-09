using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Pollux.App.Interop;

namespace Pollux.App.Services;

/// <summary>
/// Opt-in Win+E and Win+V takeover via one low-level keyboard hook (works without admin; neither can be taken with RegisterHotKey).
/// Turning both off removes the hook, which fully restores Windows' behaviour.
/// </summary>
internal static unsafe class WinEHook
{
    private const uint VK_E = 0x45, VK_V = 0x56, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_MASK = 0xE8;
    private static nint _hook;
    private static DispatcherQueue? _dispatcher;
    private static Action? _onWinE;
    private static Action? _onWinV;

    public static bool IsActive => _hook != 0;

    /// <summary>Sets the handlers; a null handler leaves that key to Windows. The hook exists only while one is set.</summary>
    public static void Apply(DispatcherQueue dispatcher, Action? onWinE, Action? onWinV)
    {
        _dispatcher = dispatcher;
        _onWinE = onWinE;
        _onWinV = onWinV;
        if (onWinE is null && onWinV is null)
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
        _onWinE = null;
        _onWinV = null;
        if (_hook != 0)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN))
        {
            var k = (Native.KBDLLHOOKSTRUCT*)lParam;
            var action = k->vkCode == VK_E ? _onWinE : k->vkCode == VK_V ? _onWinV : null;
            if (action is not null && WinDown() && !OtherModifiersDown())
            {
                // Tell the shell a non-Win key was pressed, so releasing Win does not open the Start menu.
                var inputs = stackalloc Native.INPUT[2];
                inputs[0] = new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wVk = (ushort)VK_MASK } };
                inputs[1] = new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wVk = (ushort)VK_MASK, dwFlags = 2 } };
                Native.SendInput(2, inputs, sizeof(Native.INPUT));
                _dispatcher?.TryEnqueue(() => action());
                return 1;
            }
        }

        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    // GetAsyncKeyState: the physical state. GetKeyState would read this thread's snapshot, which is stale while Pollux is in the background.
    private static bool WinDown() => (Native.GetAsyncKeyState((int)VK_LWIN) & 0x8000) != 0 || (Native.GetAsyncKeyState((int)VK_RWIN) & 0x8000) != 0;

    private static bool OtherModifiersDown() =>
        (Native.GetAsyncKeyState(0x10) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x11) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;
}
