using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Pollux.App.Interop;

namespace Pollux.App.Services;

/// <summary>
/// Opt-in Win+E takeover via a low-level keyboard hook (works without admin; Win+E cannot be taken with RegisterHotKey).
/// Turning it off removes the hook, which fully restores Explorer's behaviour.
/// </summary>
internal static unsafe class WinEHook
{
    private const uint VK_E = 0x45, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_MASK = 0xE8;
    private static nint _hook;
    private static DispatcherQueue? _dispatcher;
    private static Action? _onWinE;

    public static bool IsActive => _hook != 0;

    public static void Enable(DispatcherQueue dispatcher, Action onWinE)
    {
        _dispatcher = dispatcher;
        _onWinE = onWinE;
        if (_hook == 0)
        {
            _hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, &HookProc, Native.GetModuleHandleW(null), 0);
        }
    }

    public static void Disable()
    {
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
            if (k->vkCode == VK_E && WinDown() && !OtherModifiersDown())
            {
                // Tell the shell a non-Win key was pressed, so releasing Win does not open the Start menu.
                var inputs = stackalloc Native.INPUT[2];
                inputs[0] = new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wVk = (ushort)VK_MASK } };
                inputs[1] = new Native.INPUT { type = 1, ki = new Native.KEYBDINPUT { wVk = (ushort)VK_MASK, dwFlags = 2 } };
                Native.SendInput(2, inputs, sizeof(Native.INPUT));
                var action = _onWinE;
                if (action is not null) _dispatcher?.TryEnqueue(() => action());
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
