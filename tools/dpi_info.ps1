Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$s = [System.Windows.Forms.Screen]::PrimaryScreen
"Primary bounds : $($s.Bounds.Width) x $($s.Bounds.Height)"
"WorkingArea    : $($s.WorkingArea.Width) x $($s.WorkingArea.Height)"
$g = [System.Drawing.Graphics]::FromImage((New-Object System.Drawing.Bitmap 1,1))
"DpiX / DpiY    : $($g.DpiX) / $($g.DpiY)"
$g.Dispose()
