param(
  [Parameter(Mandatory=$true)][string]$Name,
  [string]$ProcName = "BCML-WinUI3",
  [switch]$List
)

# Invoke a button by its UI Automation name.
#
# Why UIA instead of clicking pixels: pixel clicks need the window foreground
# and exact DPI math; a single wrong guess silently does nothing. UIA calls the
# button's Invoke pattern directly, which is what "clicking" means to the app.
#
# Kept ASCII-only: Windows PowerShell 5.1 reads UTF-8-without-BOM as ANSI and
# would mangle non-ASCII literals. Match by substring so callers can pass an
# ASCII fragment (e.g. -Name "Asc" won't work for Chinese labels, so pass the
# full name or use -List to discover it).

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$p = Get-Process $ProcName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

$btns = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))

if ($List) {
    foreach ($b in $btns) {
        $r = $b.Current.BoundingRectangle
        # 虚拟化未实现/屏外项的坐标是 infinity 或 NaN，直接 [int] 会抛 InvalidArgument
        # 把整段输出打断（见 toggle_switch.ps1 里同样的兜底）。
        $rx = if ([double]::IsInfinity($r.X) -or [double]::IsNaN($r.X)) { -1 } else { [int]$r.X }
        $ry = if ([double]::IsInfinity($r.Y) -or [double]::IsNaN($r.Y)) { -1 } else { [int]$r.Y }
        $rw = if ([double]::IsInfinity($r.Width) -or [double]::IsNaN($r.Width)) { -1 } else { [int]$r.Width }
        $rh = if ([double]::IsInfinity($r.Height) -or [double]::IsNaN($r.Height)) { -1 } else { [int]$r.Height }
        $off = if ($b.Current.IsOffscreen) { "  [offscreen]" } else { "" }
        Write-Output ("  [" + $b.Current.Name + "]  x=" + $rx + " y=" + $ry +
                      " w=" + $rw + " h=" + $rh + $off)
    }
    exit 0
}

$hit = $btns | Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1
if (-not $hit) {
    $hit = $btns | Where-Object { $_.Current.Name -like ("*" + $Name + "*") } | Select-Object -First 1
}
if (-not $hit) {
    Write-Output ("NOT_FOUND: " + $Name)
    exit 2
}

try {
    $pat = $hit.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke()
    Write-Output ("INVOKED: " + $hit.Current.Name)
} catch {
    # AppBarToggleButton / ToggleButton 这类控件**不实现 Invoke**，只实现 Toggle ——
    # 对它们用 InvokePattern 会抛「不支持的模式」。退一步试 Toggle，别直接判失败。
    try {
        $tp = $hit.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
        $tp.Toggle()
        Write-Output ("TOGGLED: " + $hit.Current.Name)
    } catch {
        Write-Output ("INVOKE_FAILED: " + $_.Exception.Message)
        exit 3
    }
}
