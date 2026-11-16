# Resize the running app window; print whether the process survives.
param(
    [int]$Width = 1000,
    [int]$Height = 700,
    [switch]$SkipRestore
)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WINRES3 {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
$proc = Get-Process StjMediaViewer -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output 'NO-APP'; exit 1 }
$h = $proc.MainWindowHandle
Write-Output ("target pid=" + $proc.Id + " handle=" + $h)
if (-not $SkipRestore) { [void][WINRES3]::ShowWindow($h, 9) }
[void][WINRES3]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 300
[void][WINRES3]::MoveWindow($h, 30, 30, $Width, $Height, $true)
Write-Output ("moved to " + $Width + "x" + $Height)
Start-Sleep -Seconds 3
$alive = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
if ($alive) { Write-Output 'ALIVE' } else { Write-Output 'DEAD' }