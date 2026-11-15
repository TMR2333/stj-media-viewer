# Dev helper: launch the app, bring it to front, capture a screenshot for visual verification.
# Usage: pwsh -File tools/dev/shot.ps1 -Out shots/library.png -Wait 8
param(
    [string]$Out = "shots/shot.png",
    [int]$Wait = 8,
    [string]$Exe = "src/StjMediaViewer.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/StjMediaViewer.exe",
    [switch]$KeepRunning
)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Focus {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
"@

$repo = (Get-Location).Path
$exePath = Join-Path $repo $Exe
if (-not (Test-Path $exePath)) { Write-Error "exe not found: $exePath"; exit 1 }

Get-Process StjMediaViewer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$p = Start-Process -FilePath $exePath -PassThru
Start-Sleep -Seconds $Wait

$exited = $p.HasExited
if ($exited) { Write-Host ("process exited, code=" + $p.ExitCode) }

# 把应用窗口拉到最前（否则截图会拍到别的窗口）
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 25; $i++) {
    $proc = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    if ($proc -and $proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; break }
    Start-Sleep -Milliseconds 200
}
if ($hwnd -ne [IntPtr]::Zero) {
    $HWND_TOPMOST = [IntPtr](-1)
    $HWND_NOTOPMOST = [IntPtr](-2)
    $SWP_NOSIZE = 0x0001; $SWP_NOMOVE = 0x0002; $SWP_SHOWWINDOW = 0x0040
    [Win32Focus]::ShowWindow($hwnd, 9) | Out-Null          # SW_RESTORE
    [Win32Focus]::SetWindowPos($hwnd, $HWND_TOPMOST, 0, 0, 0, 0, ($SWP_NOSIZE -bor $SWP_NOMOVE -bor $SWP_SHOWWINDOW)) | Out-Null
    [Win32Focus]::BringWindowToTop($hwnd) | Out-Null
    [Win32Focus]::SetForegroundWindow($hwnd) | Out-Null
    [Win32Focus]::SetActiveWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 1500
    # 截图前把窗口置顶，保证不会被别的窗口挡住（Windows 会阻止后台进程抢焦点）
    [Win32Focus]::SetWindowPos($hwnd, $HWND_TOPMOST, 0, 0, 0, 0, ($SWP_NOSIZE -bor $SWP_NOMOVE)) | Out-Null
    Start-Sleep -Milliseconds 400
} else {
    Write-Host "warning: main window handle not found"
}

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)

$outPath = Join-Path $repo $Out
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null
$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

if (-not $KeepRunning) { Get-Process StjMediaViewer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
Write-Host ("saved: " + $outPath + " exited=" + $exited)