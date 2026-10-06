param(
  [string]$ProcName = "BCML-WinUI3",
  [int]$Index = 1
)
# 干净验证：导航到模组页 + 测量上移/下移的真实耗时（不依赖任何注释的说法）。
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function Get-Root {
  $p = Get-Process $ProcName -ErrorAction SilentlyContinue |
       Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
  return [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
}

$root = Get-Root

# 1) 找左侧导航里的「模组」项并选中
$all = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::ListItem))))
Write-Output "顶层可选项数=$($all.Count)"

foreach ($i in $all) {
  $n = $i.Current.Name
  if ($n -match "模组|Mods") {
    Write-Output "导航到: [$n]"
    try {
      $i.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    } catch { Write-Output ("  导航失败: " + $_.Exception.Message) }
    break
  }
}

Start-Sleep -Milliseconds 900

# 2) 现在应已在模组页；确认 ModList 存在
$idCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "ModList")
$list = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
if ($list -eq $null) { Write-Output "NO_MODLIST(可能不在模组页)"; exit 1 }

$rows = @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::ListItem))))
Write-Output "模组行数=$($rows.Count)"
for ($k = 0; $k -lt [Math]::Min(6, $rows.Count); $k++) {
  Write-Output ("  [$k] " + $rows[$k].Current.Name)
}

# 3) 选中第 Index 行
$idx = [Math]::Min($Index, $rows.Count - 1)
try {
  $rows[$idx].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Write-Output "已选中第 $idx 行"
} catch { Write-Output ("选中失败: " + $_.Exception.Message) }
Start-Sleep -Milliseconds 700

# 4) 找「上移」/「下移」按钮（中文名，按 ToolTip/AutomationProperties 命名）
$btns = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::Button))))
Write-Output "可见按钮数=$($btns.Count)"
foreach ($b in $btns) {
  $n = $b.Current.Name
  if ($n -match "上移|下移|Move") {
    Write-Output ("  找到排序按钮: [$n]")
  }
}
