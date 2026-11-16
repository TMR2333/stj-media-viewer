param(
    [int]$Width = 1000,
    [int]$Height = 700,
    [string]$Out = 'D:\UserData\Documents\dsh\stj-m-v\shots\resize-test.png'
)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WINRES2 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@

$exe = 'D:\UserData\Documents\dsh\stj-m-v\src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe'
$env:STJ_CAPTURE = $Out
$env:STJ_CAPTURE_DELAY_MS = '12000'
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 5
$p.Refresh()
if ($p.HasExited) { Write-Output "EXITED EARLY code=$($p.ExitCode)"; exit }
[void][WINRES2]::ShowWindow($p.MainWindowHandle, 9)
[void][WINRES2]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 300
[void][WINRES2]::MoveWindow($p.MainWindowHandle, 30, 30, $Width, $Height, $true)
Write-Output ("resized to " + $Width + "x" + $Height)
Start-Sleep -Seconds 10
$p.Refresh()
Write-Output ("exited=" + $p.HasExited)
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
if (Test-Path $Out) { Write-Output ("captured=" + (Get-Item $Out).Length) } else { Write-Output 'NO-CAPTURE' }