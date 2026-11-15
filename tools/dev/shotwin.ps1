# 用 PrintWindow 直接抓应用窗口内容（不受遮挡影响），失败则回退全屏截图
param(
    [string]$Out = "shots/shot.png",
    [int]$Wait = 8,
    [string]$Exe = "src/StjMediaViewer.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/StjMediaViewer.exe"
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@

$repo = (Get-Location).Path
$exePath = Join-Path $repo $Exe
Get-Process StjMediaViewer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400
$p = Start-Process -FilePath $exePath -PassThru
Start-Sleep -Seconds $Wait

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 30; $i++) {
    $proc = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    if ($proc -and $proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; break }
    Start-Sleep -Milliseconds 200
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Error "no window handle"; exit 1 }

[WinCap]::ShowWindow($hwnd, 9) | Out-Null
Start-Sleep -Milliseconds 800

$rect = New-Object WinCap+RECT
[WinCap]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
Write-Host "window rect: $($rect.Left),$($rect.Top) ${w}x${h}"

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
# 2 = PW_RENDERFULLCONTENT（Win10 1803+ 支持 DirectComposition 内容）
$ok = [WinCap]::PrintWindow($hwnd, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()

# 判断是否抓到了内容（采样若干像素，全黑/全透明说明失败）
$nonEmpty = 0
for ($x = 10; $x -lt $w; $x += [Math]::Max(1, [int]($w / 20))) {
    for ($y = 10; $y -lt $h; $y += [Math]::Max(1, [int]($h / 20))) {
        $c = $bmp.GetPixel($x, $y)
        if (($c.R + $c.G + $c.B) -gt 24) { $nonEmpty++ }
    }
}
Write-Host "PrintWindow ok=$ok nonEmptySamples=$nonEmpty"

$outPath = Join-Path $repo $Out
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null
$bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Get-Process StjMediaViewer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Write-Host "saved: $outPath"