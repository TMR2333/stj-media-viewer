# Stress test: cold thumbnail cache + video filter + scroll the grid (many concurrent thumbnail decodes).
# ASCII only (powershell 5.1 reads GBK for non-BOM files).
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class M {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public const uint WHEEL = 0x0800;
}
"@

$exe = "D:\UserData\Documents\dsh\stj-m-v\src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe"
$env:STJ_DEMO_FILTER = ([string]([char]0x89C6) + [string]([char]0x9891))   # video

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 7
if ($p.HasExited) { "CRASH BEFORE CLICK 0x{0:X8}" -f $p.ExitCode; exit }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$procCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $procCond)
if (-not $win) { "window not found"; Stop-Process -Id $p.Id -Force; exit }
$r = $win.Current.BoundingRectangle
[void][M]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 400

# grid center (right of the filter panel)
$cx = [int]($r.X + $r.Width * 0.45)
$cy = [int]($r.Y + $r.Height * 0.55)
[void][M]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 300

"scrolling grid at ($cx,$cy) ..."
for ($i = 0; $i -lt 45; $i++) {
    [M]::mouse_event([M]::WHEEL, 0, 0, -120, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 220
    if ($p.HasExited) { break }
}
Start-Sleep -Seconds 3
if ($p.HasExited) { "CRASH-ON-SCROLL 0x{0:X8}" -f $p.ExitCode } else { "survived 45 wheel steps"; Stop-Process -Id $p.Id -Force }
Remove-Item env:STJ_DEMO_FILTER