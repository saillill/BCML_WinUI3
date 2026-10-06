param([string]$ProcName = "BCML-WinUI3", [int]$Steps = 3, [string]$ButtonName = "")

# Click the wizard "next" button via UI Automation, repeatedly.
# ButtonName defaults to the Chinese label; pass -ButtonName to override.
if ([string]::IsNullOrWhiteSpace($ButtonName)) {
    $ButtonName = [char]0x4E0B + [char]0x4E00 + [char]0x6B65   # "next" in Chinese
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process $ProcName -ErrorAction SilentlyContinue |
     Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "NO_WINDOW"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

function Click-Named([string]$name) {
    $btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($b in $btns) {
        if ($b.Current.Name -eq $name) {
            $pat = $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
            $pat.Invoke()
            return $true
        }
    }
    return $false
}

for ($i = 1; $i -le $Steps; $i++) {
    Start-Sleep -Milliseconds 900
    $ok = Click-Named $ButtonName
    Write-Output ("step {0} clicked={1}" -f $i, $ok)
    if (-not $ok) { break }
}
