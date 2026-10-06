param([string]$Out = "shots\scroll.png", [int]$Ticks = 12, [int]$WaitSec = 20)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class W4 {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, IntPtr e);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
$deadline = (Get-Date).AddSeconds(60)
$h = [IntPtr]::Zero
while ((Get-Date) -lt $deadline) {
  $p = Get-Process BCML-WinUI3 -ErrorAction SilentlyContinue |
       Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) { $h = $p.MainWindowHandle; break }
  Start-Sleep -Milliseconds 400
}
if ($h -eq [IntPtr]::Zero) { Write-Output "NO_WINDOW"; exit 1 }
Start-Sleep -Seconds $WaitSec
[void][W4]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 700

$r = New-Object W4+RECT
[void][W4]::GetWindowRect($h, [ref]$r)
$cx = [int](($r.Left + $r.Right) / 2)
$cy = [int]($r.Top + ($r.Bottom - $r.Top) * 0.72)
[void][W4]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 300
for ($i = 0; $i -lt $Ticks; $i++) {
  [W4]::mouse_event(0x0800, 0, 0, -120, [IntPtr]::Zero)   # MOUSEEVENTF_WHEEL
  Start-Sleep -Milliseconds 120
}
Start-Sleep -Milliseconds 1400

$bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size(($r.Right - $r.Left), ($r.Bottom - $r.Top))))
$f = Join-Path (Get-Location) $Out
$bmp.Save($f, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("SAVED $f")
