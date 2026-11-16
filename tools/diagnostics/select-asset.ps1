# Select the first asset card (to load the inspector) and check the app stays alive.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SEL1 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@

$exe = 'D:\UserData\Documents\dsh\stj-m-v\src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe'
$env:STJ_CAPTURE = 'D:/UserData/Documents/dsh/stj-m-v/shots/check-inspector-custom.png'
$env:STJ_CAPTURE_DELAY_MS = '14000'
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 9
$p.Refresh()
[void][SEL1]::SetForegroundWindow($p.MainWindowHandle)
$win = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)
$items = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
Write-Output ("list items=" + $items.Count)
if ($items.Count -gt 0) {
    $item = $items[0]
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output 'selected first item'
}
Start-Sleep -Seconds 6
if ($p.HasExited) { Write-Output ("CRASH code=" + $p.ExitCode) } else { Write-Output 'ALIVE'; Stop-Process -Id $p.Id -Force }