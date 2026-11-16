# Slower synthetic drag from an asset card, release back inside the app (no drop target).
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class D3 {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
}
"@

$exe = "D:\UserData\Documents\dsh\stj-m-v\src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe"
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 9
if ($p.HasExited) { Write-Host "CRASH AT STARTUP"; exit }

$p.Refresh()
$hwnd = $p.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "no main window handle"; Stop-Process -Id $p.Id -Force; exit }

$win = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$r = $win.Current.BoundingRectangle
if ($r.Width -le 0) { Write-Host "no bounding rect"; Stop-Process -Id $p.Id -Force; exit }

[void][D3]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 600

$sx = [int]($r.X + 490); $sy = [int]($r.Y + 330)
Write-Host "drag start ($sx,$sy) window=$($r.Width)x$($r.Height)"
[void][D3]::SetCursorPos($sx, $sy)
Start-Sleep -Milliseconds 300
[D3]::mouse_event([D3]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 250
for ($i = 1; $i -le 14; $i++) {
    [void][D3]::SetCursorPos($sx + ($i * 12), $sy + ($i * 3))
    Start-Sleep -Milliseconds 90
}
Start-Sleep -Milliseconds 600
[void][D3]::SetCursorPos([int]($r.X + 210), [int]($r.Y + 420))
Start-Sleep -Milliseconds 300
[D3]::mouse_event([D3]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Seconds 3

if ($p.HasExited) { Write-Host ("CRASH-ON-DRAG 0x{0:X8}" -f $p.ExitCode) } else { Write-Host "alive after drag"; Stop-Process -Id $p.Id -Force }