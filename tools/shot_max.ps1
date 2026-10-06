param(
  [string]$ProcName = "BCML-WinUI3",
  [string]$Out = "shots/max.png",
  [int]$WaitSec = 3
)

# Maximize the target window (fully on-screen), then crop-free full-window capture.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class MaxWin {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr SetWindowPos(IntPtr h, IntPtr a,
      int x, int y, int cx, int cy, uint f);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

if ([string]::IsNullOrWhiteSpace($ProcName)) { $ProcName = "BCML-WinUI3" }
$p = Get-Process $ProcName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
$h = $p.MainWindowHandle

[void][MaxWin]::ShowWindow($h, 9)   # SW_RESTORE first (un-maximize if needed)
Start-Sleep -Milliseconds 300
[void][MaxWin]::ShowWindow($h, 3)   # SW_MAXIMIZE
[void][MaxWin]::BringWindowToTop($h)
[void][MaxWin]::SetForegroundWindow($h)
Start-Sleep -Seconds $WaitSec

[void][System.Reflection.Assembly]::LoadWithPartialName("System.Drawing")
[void][System.Reflection.Assembly]::LoadWithPartialName("System.Windows.Forms")

$r = New-Object MaxWin+RECT
[void][MaxWin]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$hh = $r.Bottom - $r.Top
if ($w -le 0 -or $hh -le 0) { Write-Output "BAD_RECT"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $hh)))
$f = Join-Path (Get-Location) $Out
$bmp.Save($f, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("SAVED $f ($w x $hh)")
