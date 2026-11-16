# Dump the filter sidebar's UI automation tree to see whether theme option rows exist.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SC2 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@

$exe = 'D:\UserData\Documents\dsh\stj-m-v\src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe'
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8
$p.Refresh()
[void][SC2]::SetForegroundWindow($p.MainWindowHandle)
$win = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)

# 枚举所有按钮/文本的名字（只看左半边：侧栏）
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)
$names = @()
foreach ($el in $all) {
    try {
        $r = $el.Current.BoundingRectangle
        if ($r.Width -le 0) { continue }
        if ($r.X -gt ($win.Current.BoundingRectangle.X + 400)) { continue }
        $n = $el.Current.Name
        if ($n) { $names += ("{0} @ {1},{2} {3}x{4}" -f $n, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height) }
    } catch { }
}
$names | Select-Object -Unique | ForEach-Object { Write-Output $_ }
Stop-Process -Id $p.Id -Force