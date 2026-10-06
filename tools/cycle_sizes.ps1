param(
  [string]$Out = "shots\cycle.png",
  [int[]]$Widths = @(1280, 1600, 1150, 1000, 860, 700),
  [int]$Height = 900
)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class W2 {
  [DllImport("user32.dll")] public static extern IntPtr FindWindowW(string cls, string name);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
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
Start-Sleep -Seconds 16

$base = Join-Path (Get-Location) "shots"
if (-not (Test-Path $base)) { New-Item -ItemType Directory -Path $base | Out-Null }

foreach ($w in $Widths) {
  [void][W2]::MoveWindow($h, 20, 20, $w, $Height, $true)
  [void][W2]::SetForegroundWindow($h)
  Start-Sleep -Milliseconds 2200
  $r = New-Object W2+RECT
  [void][W2]::GetWindowRect($h, [ref]$r)
  $ww = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
  $bmp = New-Object System.Drawing.Bitmap $ww, $hh
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($ww, $hh)))
  $f = Join-Path $base ("w{0}.png" -f $w)
  $bmp.Save($f, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Output ("SAVED w=$w  rect=$ww x $hh")
}
