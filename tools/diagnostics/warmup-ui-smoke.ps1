# Scroll Settings page to bottom, click the warmup button (AutomationId=WarmupButton), let the app capture a screenshot.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W2 {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public const uint WHEEL = 0x0800;
}
"@

$exe = "D:\UserData\Documents\dsh\stj-m-v\src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe"
$env:STJ_CAPTURE = "D:/UserData/Documents/dsh/stj-m-v/shots/final-warmup.png"
$env:STJ_CAPTURE_DELAY_MS = "16000"
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 7
if ($p.HasExited) { Write-Host "CRASH-AT-STARTUP"; exit }

$p.Refresh()
$hwnd = $p.MainWindowHandle
[void][W2]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 400

# Wheel-scroll down to the cache/warmup section (delta -600 = 4294966696 as uint32)
[void][W2]::SetCursorPos(1200, 600)
Start-Sleep -Milliseconds 200
for ($i = 0; $i -lt 22; $i++) {
    [W2]::mouse_event([W2]::WHEEL, 0, 0, [uint32]4294966696, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 90
}
Start-Sleep -Milliseconds 500

$win = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, "WarmupButton")
$btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($btn) {
    $pattern = $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
    Write-Host "CLICKED-WARMUP-BUTTON"
} else {
    Write-Host "BUTTON-NOT-FOUND"
}
Start-Sleep -Seconds 3
if ($p.HasExited) { Write-Host "CRASH-AFTER-CLICK" } else { Write-Host "ALIVE; waiting for capture" }
Start-Sleep -Seconds 8
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
Write-Host "DONE"