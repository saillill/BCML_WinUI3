param(
  [string]$ProcName = "BCML-WinUI3",
  [int]$Index = 3
)
# 干净测量：点上移后，列表顺序何时真正变化（轮询 UIA，取到变化耗时）。
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function Get-Root {
  $p = Get-Process $ProcName -ErrorAction SilentlyContinue |
       Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
  return [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
}
$root = Get-Root
$list = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "ModList")))

function Get-Names {
  $rows = @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::ListItem))))
  return ($rows | ForEach-Object { $_.Current.Name })
}

$before = Get-Names
Write-Output "点击前前 6 行:"
0..5 | ForEach-Object { Write-Output ("  [$_] " + $before[$_]) }

# 选中第 Index 行
$rows = @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::ListItem))))
$rows[$Index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 500

# 找「上移」按钮
$btns = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::Button))))
$up = $btns | Where-Object { $_.Current.Name -eq "上移" } | Select-Object -First 1
if ($up -eq $null) { Write-Output "NO_UP_BUTTON"; exit 1 }

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$up.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

# 轮询直到顺序变化
$changedAt = -1
$new = $before
for ($t = 0; $t -lt 200; $t++) {
  Start-Sleep -Milliseconds 50
  $new = Get-Names
  if (($new -join "|") -ne ($before -join "|")) { $changedAt = $sw.ElapsedMilliseconds; break }
}
$sw.Stop()

Write-Output ""
if ($changedAt -ge 0) {
  Write-Output "顺序变化耗时 ≈ $changedAt ms"
} else {
  Write-Output "10 秒内顺序未变化（按钮可能无效）"
}
Write-Output "点击后前 6 行:"
0..5 | ForEach-Object { Write-Output ("  [$_] " + $new[$_]) }
