param(
  [string]$Out = "shots\shot.png",
  [int]$WaitSec = 3,
  [int]$ScrollTicks = 0,
  [string]$ProcName = "BCML-WinUI3"
)

# 截图指定进程的主窗口。
#
# 为什么不用 tools/shot_default.ps1 直接拍：
#   那个脚本只 SetForegroundWindow，实测在 175% DPI 的多屏环境下，
#   窗口没被真正提到最前时 CopyFromScreen 会拍到桌面。
#   这里补上 ShowWindow(RESTORE) + SetWindowPos(HWND_TOP) 再拍，
#   并显式换算「物理像素」的窗口矩形。
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class ShotWin {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after,
      int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$p = Get-Process $ProcName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
$h = $p.MainWindowHandle

# SW_RESTORE = 9
[void][ShotWin]::ShowWindow($h, 9)
[void][ShotWin]::BringWindowToTop($h)
# HWND_TOP = 0, SWP_NOSIZE=1, SWP_NOMOVE=2
[void][ShotWin]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0003)
[void][ShotWin]::SetForegroundWindow($h)
Start-Sleep -Seconds $WaitSec

# 可选：滚动页面（鼠标滚轮）
if ($ScrollTicks -ne 0) {
  $r0 = New-Object ShotWin+RECT
  [void][ShotWin]::GetWindowRect($h, [ref]$r0)
  $cx = [int](($r0.Left + $r0.Right) / 2)
  $cy = [int](($r0.Top + $r0.Bottom) / 2)
  [void][ShotWin]::SetCursorPos($cx, $cy)
  $dir = -1
  if ($ScrollTicks -lt 0) { $dir = 1 }
  $delta = $dir * 120
  $n = [Math]::Abs($ScrollTicks)
  for ($i = 0; $i -lt $n; $i++) {
    [ShotWin]::mouse_event(0x0800, 0, 0, $delta, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 120
  }
  Start-Sleep -Milliseconds 800
}

$r = New-Object ShotWin+RECT
[void][ShotWin]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$hh = $r.Bottom - $r.Top
if ($w -le 0 -or $hh -le 0) { Write-Output "BAD_RECT $w x $hh"; exit 1 }

# Windows PowerShell 5.1 里 Add-Type -AssemblyName 有时会被后续 Add-Type 重置，
# 真正画图前再确认一次。
[void][System.Reflection.Assembly]::LoadWithPartialName("System.Drawing")
[void][System.Reflection.Assembly]::LoadWithPartialName("System.Windows.Forms")

$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $hh)))
$f = Join-Path (Get-Location) $Out
$bmp.Save($f, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("SAVED $f  ($w x $hh)")
