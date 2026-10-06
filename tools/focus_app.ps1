param(
  [string]$ProcName = "BCML-WinUI3",
  [int]$X = 40,
  [int]$Y = 30,
  [int]$W = 1180,
  [int]$H = 760
)

# Focus + resize the target process main window, then leave it fully on-screen.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class FitWin {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SetWindowPos(IntPtr h, IntPtr a,
      int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

if ([string]::IsNullOrWhiteSpace($ProcName)) { $ProcName = "BCML-WinUI3" }
$p = Get-Process $ProcName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
$h = $p.MainWindowHandle

[void][FitWin]::ShowWindow($h, 9)   # SW_RESTORE
# SWP_SHOWWINDOW(0x40) | NOZORDER off -> pass HWND_TOP; move + size
[void][FitWin]::SetWindowPos($h, [IntPtr]::Zero, $X, $Y, $W, $H, 0x0040)
[void][FitWin]::BringWindowToTop($h)
[void][FitWin]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 1200

$r = New-Object FitWin+RECT
[void][FitWin]::GetWindowRect($h, [ref]$r)
Write-Output ("focused h=$h rect=({0},{1})-({2},{3}) size={4}x{5}" -f `
    $r.Left, $r.Top, $r.Right, $r.Bottom, ($r.Right - $r.Left), ($r.Bottom - $r.Top))
