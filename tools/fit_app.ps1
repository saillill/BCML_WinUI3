param(
  [string]$ProcName = "BCML-WinUI3",
  [int]$X = 20,
  [int]$Y = 20,
  [int]$W = 1200,
  [int]$H = 820
)

# Move + resize the target window fully inside the primary work area.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class WinFit {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr SetWindowPos(IntPtr h, IntPtr a,
      int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y,
      int w, int hh, bool repaint);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

if ([string]::IsNullOrWhiteSpace($ProcName)) { $ProcName = "BCML-WinUI3" }
$p = Get-Process $ProcName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
$h = $p.MainWindowHandle

[void][WinFit]::ShowWindow($h, 9)   # SW_RESTORE
Start-Sleep -Milliseconds 300

# MoveWindow 比 SetWindowPos 更能确保 WM_SIZE 走一遍，强制 WinUI 重排
[void][WinFit]::MoveWindow($h, $X, $Y, $W, $H, $true)
[void][WinFit]::BringWindowToTop($h)
[void][WinFit]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 1500

$r = New-Object WinFit+RECT
[void][WinFit]::GetWindowRect($h, [ref]$r)
Write-Output ("window rect=({0},{1})-({2},{3}) size={4}x{5}" -f `
    $r.Left, $r.Top, $r.Right, $r.Bottom, ($r.Right - $r.Left), ($r.Bottom - $r.Top))
