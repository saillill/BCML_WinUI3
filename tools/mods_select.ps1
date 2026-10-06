param(
  [string]$ProcName = "BCML-WinUI3",
  [int]$Index = 1,
  [string]$Pattern = ""
)

# Select the Nth item in the mods ListView via UI Automation.
# -Index is 0-based. -Pattern optionally filters items whose name contains it.
# Kept ASCII-only: Windows PowerShell 5.1 reads UTF-8-without-BOM as ANSI and
# would mangle any non-ASCII literals here.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$p = Get-Process $ProcName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

# Scope to the mods ListView (x:Name="ModList" -> AutomationId). Without this the
# left navigation rail's own ListItems come first and get selected instead.
$idCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "ModList")
$list = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
if ($list -eq $null) { Write-Output "NO_MODLIST"; exit 1 }

$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)

$items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
Write-Output ("listitems=" + $items.Count)
if ($items.Count -eq 0) { exit 1 }

$target = $null
if (-not [string]::IsNullOrWhiteSpace($Pattern)) {
    foreach ($it in $items) {
        if ($it.Current.Name -like ("*" + $Pattern + "*")) { $target = $it; break }
    }
} else {
    if ($Index -ge $items.Count) { $Index = $items.Count - 1 }
    $target = $items[$Index]
}

if ($target -eq $null) { Write-Output "NOT_FOUND"; exit 1 }
Write-Output ("selecting: " + $target.Current.Name)

try {
    $sel = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $sel.Select()
    Write-Output "selected=True"
} catch {
    try {
        $target.SetFocus()
        Write-Output "focused=True"
    } catch {
        Write-Output ("FAILED: " + $_.Exception.Message)
        exit 1
    }
}
