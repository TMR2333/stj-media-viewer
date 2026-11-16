# Real mouse click on the video facet row via UI Automation geometry (ASCII only).
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
}
"@

$exe = "D:\UserData\Documents\dsh\stj-m-v\src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe"
Remove-Item env:STJ_DEMO_FILTER -ErrorAction SilentlyContinue

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
if ($p.HasExited) { "CRASH AT STARTUP 0x{0:X8}" -f $p.ExitCode; exit }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$procCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCond)
if (-not $win) { "window not found"; Stop-Process -Id $p.Id -Force; exit }

$hwnd = [IntPtr]$win.Current.NativeWindowHandle
[void][W]::ShowWindow($hwnd, 9)
[void][W]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 500

$videoLabel = [string]([char]0x89C6) + [string]([char]0x9891)
$nameCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $videoLabel)
$el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
if (-not $el) { "video facet row not found"; Stop-Process -Id $p.Id -Force; exit }

$r = $el.Current.BoundingRectangle
$x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
"clicking video facet row at ($x,$y) rect=$($r.Width)x$($r.Height)"
[void][W]::SetCursorPos($x, $y)
Start-Sleep -Milliseconds 200
[W]::mouse_event([W]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 60
[W]::mouse_event([W]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)

Start-Sleep -Seconds 20
if ($p.HasExited) { "CLICK-CRASH 0x{0:X8}" -f $p.ExitCode } else { "alive 20s after real click"; Stop-Process -Id $p.Id -Force }