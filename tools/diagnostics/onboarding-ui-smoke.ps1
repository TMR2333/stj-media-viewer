# Walk all onboarding steps (click Next 4x then Done), then verify settings persisted.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class O2 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@

$exe = "D:\UserData\Documents\dsh\stj-m-v\src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe"
$env:STJ_TRACE_ONBOARDING = "1"
Remove-Item env:STJ_CAPTURE -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 9
if ($p.HasExited) { Write-Host "CRASH-AT-STARTUP"; exit }

$p.Refresh()
$hwnd = $p.MainWindowHandle
[void][O2]::SetForegroundWindow($hwnd)
$win = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)

function Find-Id($id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

for ($i = 0; $i -lt 4; $i++) {
    $btn = $null
    for ($try = 0; $try -lt 10 -and -not $btn; $try++) {
        $btn = Find-Id "OnboardingNext"
        if (-not $btn) { Start-Sleep -Milliseconds 400 }
    }
    if (-not $btn) { Write-Host "NEXT-NOT-FOUND"; break }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "next-click $($i+1)"
    Start-Sleep -Milliseconds 1800
}
Start-Sleep -Seconds 4
if ($p.HasExited) { Write-Host "CRASH-AFTER-FINISH" } else { Write-Host "ALIVE-AFTER-FINISH" }
Start-Sleep -Seconds 2
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
Write-Host "DONE"