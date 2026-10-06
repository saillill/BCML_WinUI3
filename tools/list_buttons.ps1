param(
  [string]$ProcName = "BCML-WinUI3",
  [string]$Name = ""
)

# Dump every Button's automation name + enabled state in the main window.
# Used to verify that newly added settings controls actually rendered.
# ASCII-only on purpose: PowerShell 5.1 reads UTF-8-without-BOM as ANSI.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$p = Get-Process $ProcName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

$btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
Write-Output ("buttons=" + $btns.Count)
foreach ($b in $btns) {
    $n = $b.Current.Name
    if ([string]::IsNullOrWhiteSpace($n)) { $n = "(no name)" }
    if ($Name -ne "" -and $n -notlike ("*" + $Name + "*")) { continue }
    Write-Output ("  [{0}] enabled={1}" -f $n, $b.Current.IsEnabled)
}
