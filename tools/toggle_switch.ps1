param(
  [string]$ProcName = "BCML-WinUI3",
  [int]$Index = 0,
  [switch]$List
)

# List / toggle the ToggleSwitch controls in the mods list.
#
# Why a separate script: ToggleSwitch isn't a Button, and the automation peer for it
# only implements the Toggle pattern. Querying it as ControlType.Button (what
# invoke_button.ps1 does) either misses it or fails with "unsupported pattern".
#
# Kept ASCII-only: Windows PowerShell 5.1 reads UTF-8-without-BOM as ANSI.

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$p = Get-Process $ProcName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::IsEnabledProperty, $true)
$all = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))

$switches = @()
foreach ($el in $all) {
    try {
        if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$null)) {
            $switches += $el
        }
    } catch { }
}

# The toolbar toggles are also TogglePattern; the list rows' switches sit below them.
# Sort by screen Y so index 0 is the topmost (first row in the list).
$switches = $switches | Sort-Object { $_.Current.BoundingRectangle.Y }, { $_.Current.BoundingRectangle.X }

if ($List) {
    Write-Output ("toggles=" + $switches.Count)
    $i = 0
    foreach ($s in $switches) {
        $r = $s.Current.BoundingRectangle
        $state = ($s.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState
        # 虚拟化未实现的行坐标是 infinity，直接 [int] 会抛 InvalidArgument，先兜一下
        $rx = if ([double]::IsInfinity($r.X)) { -1 } else { [int]$r.X }
        $ry = if ([double]::IsInfinity($r.Y)) { -1 } else { [int]$r.Y }
        Write-Output ("  [$i] state=$state x=" + $rx + " y=" + $ry +
                      " w=" + [int]$r.Width + " h=" + [int]$r.Height)
        $i++
    }
    exit 0
}

if ($Index -lt 0 -or $Index -ge $switches.Count) {
    Write-Output ("BAD_INDEX: " + $Index + " of " + $switches.Count)
    exit 2
}

$target = $switches[$Index]
$pat = $target.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$before = $pat.Current.ToggleState
$pat.Toggle()
$after = $pat.Current.ToggleState
$r = $target.Current.BoundingRectangle
Write-Output ("TOGGLED idx=" + $Index + " " + $before + " -> " + $after + " at y=" + [int]$r.Y)
