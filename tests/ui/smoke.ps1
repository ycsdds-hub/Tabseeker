# UI Automation smoke test for Tabseeker: buttons, navigation, tabs, search
#   powershell -File tests/ui/smoke.ps1 [-Exe publish\Tabseeker.exe]
# Starts the app on this repo's src folder (so the test does not depend on saved tabs), runs the checks, closes it.
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\publish\Tabseeker.exe'))
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Get-Process Tabseeker -ErrorAction SilentlyContinue | Stop-Process
$srcDir = (Resolve-Path (Join-Path $PSScriptRoot '..\..\src')).Path
$proc = Start-Process $Exe -ArgumentList "`"$srcDir`"" -PassThru
Start-Sleep 7
$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
function Win { $p = Get-Process Tabseeker | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1; $A::FromHandle($p.MainWindowHandle) }
function ById($id) {
    # the view is rebuilt after navigation, so retry briefly
    for ($i = 0; $i -lt 20; $i++) {
        $e = (Win).FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition ($A::AutomationIdProperty, $id)))
        if ($e) { return $e }
        Start-Sleep -Milliseconds 150
    }
    $null
}
function Invoke($id) { (ById $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 700 }
function Val($id) { $e = ById $id; if ($e) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } else { '<none>' } }
function SetVal($id, $v) { (ById $id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v); Start-Sleep -Milliseconds 1200 }
function Title { (Win).Current.Name }
function Rows { $l = ById 'List'; if ($l) { $l.FindAll($TS::Children, (New-Object System.Windows.Automation.PropertyCondition ($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem))).Count } else { -1 } }
$fail = 0
function Check($name, $cond) { if ($cond) { "PASS  $name" } else { "FAIL  $name"; $script:fail++ } }

$start = Val 'Address'
"start: $start  title: $(Title)  rows: $(Rows)"
Invoke 'UpBtn'
$up = Val 'Address'
Check "Up goes to parent ($up)" ($up -eq (Split-Path $start))
Invoke 'BackBtn'
Check "Back returns ($(Val 'Address'))" ((Val 'Address') -eq $start)
Invoke 'FwdBtn'
Check "Forward ($(Val 'Address'))" ((Val 'Address') -eq $up)
Invoke 'NewTabBtn'
Check "New tab opens same folder, title '$(Title)'" ((Title) -like "$(Split-Path $up -Leaf)*")
SetVal 'Filter' 'src'
Check "Filter narrows rows ($(Rows))" ((Rows) -ge 1)
SetVal 'Filter' ''
SetVal 'SearchBox' 'hanyoung-fix folder:'
Check "Search tab: title '$(Title)', rows $(Rows)" (((Title) -like '*hanyoung-fix folder:*') -and (Rows) -ge 1)
Check "Search box keeps text '$(Val 'SearchBox')'" ((Val 'SearchBox') -eq 'hanyoung-fix folder:')
SetVal 'SearchBox' 'zzqqxx_nothing_matches'
Check "No-match search shows 0 rows ($(Rows))" ((Rows) -eq 0)
"--- $fail failure(s)"
$proc.CloseMainWindow() | Out-Null
exit $fail
