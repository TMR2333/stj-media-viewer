# Scroll the library filter sidebar (left panel) to the bottom and let the app capture a screenshot.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SC3 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@

$exe = 'D:\UserData\Documents\dsh\stj-m-v\src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe'
$env:STJ_CAPTURE = 'D:/UserData/Documents/dsh/stj-m-v/shots/check-filter-fixed.png'
$env:STJ_CAPTURE_DELAY_MS = '16000'
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8
$p.Refresh()
[void][SC3]::SetForegroundWindow($p.MainWindowHandle)
$win = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)
$scrollables = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$target = $null
foreach ($el in $scrollables) {
    $r = $el.Current.BoundingRectangle
    if ($r.Width -gt 40 -and $r.X -lt ($win.Current.BoundingRectangle.X + 420)) { $target = $el; break }
}
if (-not $target) { Write-Output 'NO-SCROLLER'; Stop-Process -Id $p.Id -Force; exit }

$pattern = $target.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
for ($i = 0; $i -lt 8; $i++) {
    $pattern.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount,
                    [System.Windows.Automation.ScrollAmount]::LargeIncrement)
    Start-Sleep -Milliseconds 250
}
Write-Output ('scrolled; percent=' + $pattern.Current.VerticalScrollPercent)
Start-Sleep -Seconds 7
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
Write-Output 'done'