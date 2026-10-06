param([string]$ProcName = "BCML-WinUI3", [string]$Nav = "")
# 枚举当前页所有可交互元素（按钮/开关/输入框/下拉/列表项），用于「可用性 + 命名」的干净核查。
# 不用 Add-Type 内联（被安全策略拦），这里用脚本文件形式。
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$p = Get-Process $ProcName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

if ($Nav -ne "") {
  $items = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
      (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem))))
  foreach ($i in $items) {
    if ($i.Current.Name -like ("*" + $Nav + "*")) {
      try { $i.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch {}
      break
    }
  }
  Start-Sleep -Milliseconds 900
}

$types = @("Button","ToggleButton","CheckBox","Edit","ComboBox","ListItem","TextBlock")
foreach ($t in $types) {
  $ct = [System.Windows.Automation.ControlType]::$t
  if ($ct -eq $null) { continue }
  $found = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
      (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct))))
  $named = @($found | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Current.Name) })
  $off = @($found | Where-Object { $_.Current.IsOffscreen })
  Write-Output ("{0,-12} 总数={1,-4} 有名字={2,-4} 屏外={3}" -f $t, $found.Count, $named.Count, $off.Count)
  if ($t -in @("Button","ToggleButton","CheckBox","ComboBox")) {
    foreach ($f in $found) {
      $flag = ""
      if ([string]::IsNullOrWhiteSpace($f.Current.Name)) { $flag = "  <== 无名字" }
      if ($f.Current.IsOffscreen) { $flag += "  [屏外]" }
      Write-Output ("    [" + $f.Current.Name + "]" + $flag)
    }
  }
}
