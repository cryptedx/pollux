<#
.SYNOPSIS
  In the desktop session (via session.ps1): saves a PNG of the whole desktop, or with -Dir of the clone's Pollux
  window only. The window is captured with PrintWindow, so other windows in front of it (the VM is shared) do not
  show up in the picture.
#>
param(
    [Parameter(Mandatory)] [string]$Log,
    [Parameter(Mandatory)] [string]$Rc,
    [Parameter(Mandatory)] [string]$Out,
    [string]$Dir
)

$code = 1
function Say([string]$Message) { Add-Content -Path $Log -Value $Message }
try {
    $ErrorActionPreference = 'Stop'
    Add-Type -AssemblyName System.Drawing
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PolluxVmShot {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
    # Per-monitor aware: otherwise a scaled display is captured at the wrong size.
    [PolluxVmShot]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null

    if ($Dir) {
        $prefix = (Join-Path $Dir '').ToLowerInvariant()
        $p = Get-Process Pollux -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.ToLowerInvariant().StartsWith($prefix) -and $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
        if (-not $p) { throw 'The clone''s Pollux has no window. Run: scripts/vm.sh run' }
        $hwnd = $p.MainWindowHandle
        $win = New-Object PolluxVmShot+RECT
        $frame = New-Object PolluxVmShot+RECT
        [PolluxVmShot]::GetWindowRect($hwnd, [ref]$win) | Out-Null
        # DWMWA_EXTENDED_FRAME_BOUNDS: the visible frame without the invisible resize border.
        [PolluxVmShot]::DwmGetWindowAttribute($hwnd, 9, [ref]$frame, 16) | Out-Null
        $full = New-Object System.Drawing.Bitmap ($win.Right - $win.Left), ($win.Bottom - $win.Top)
        $fg = [System.Drawing.Graphics]::FromImage($full)
        $hdc = $fg.GetHdc()
        # PW_RENDERFULLCONTENT: also captures DirectComposition content (WinUI, WebView2).
        $ok = [PolluxVmShot]::PrintWindow($hwnd, $hdc, 2)
        $fg.ReleaseHdc($hdc); $fg.Dispose()
        if (-not $ok) { throw 'PrintWindow failed.' }
        $crop = New-Object System.Drawing.Rectangle ($frame.Left - $win.Left), ($frame.Top - $win.Top), ($frame.Right - $frame.Left), ($frame.Bottom - $frame.Top)
        $bmp = $full.Clone($crop, $full.PixelFormat)
        $full.Dispose()
    } else {
        $x, $y = [PolluxVmShot]::GetSystemMetrics(76), [PolluxVmShot]::GetSystemMetrics(77)
        $w, $h = [PolluxVmShot]::GetSystemMetrics(78), [PolluxVmShot]::GetSystemMetrics(79)
        if ($w -le 0 -or $h -le 0) { throw "Nothing to capture ($w x $h)." }
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
        $g.Dispose()
    }
    New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    Say "Saved $($bmp.Width) x $($bmp.Height) to $Out"
    $bmp.Dispose()
    $code = 0
} catch {
    Say "ERROR: $($_.Exception.Message)"
} finally {
    Set-Content $Rc $code
}
