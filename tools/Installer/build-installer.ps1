# 打包脚本（ASCII only，PowerShell 5.1 可读）
# 产出：
#   dist\STJMediaViewer-Setup.exe            安装包（单文件，无需管理员）
#   dist\STJMediaViewer-<ver>-portable.zip   绿色版压缩包
#
# 用法： powershell -ExecutionPolicy Bypass -File tools\Installer\build-installer.ps1 [-SkipAppPublish]

param(
    [switch]$SkipAppPublish,
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $repo

$sevenZip = "C:\Program Files\7-Zip\7z.exe"
$appProj = "src\StjMediaViewer.App\StjMediaViewer.App.csproj"
$installerProj = "tools\Installer\Installer.csproj"
$publishDir = "dist\publish"

Write-Host "[1/5] publish app (Release, win-x64, self-contained)"
if (-not $SkipAppPublish) {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish $appProj -c Release -r win-x64 --self-contained true -o $publishDir -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "app publish failed" }
}

Write-Host "[2/5] clean publish output"
Remove-Item (Join-Path $publishDir "StjMediaViewer.exe.WebView2") -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $publishDir -Filter *.pdb -File | Remove-Item -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $publishDir "createdump.exe") -Force -ErrorAction SilentlyContinue

Write-Host "[3/5] build payload.zip"
$payload = "tools\Installer\payload.zip"
if (Test-Path $payload) { Remove-Item $payload -Force }
Push-Location $publishDir
& $sevenZip a -tzip -mx=5 "..\..\$payload" ".\*" | Out-Null
Pop-Location
if (-not (Test-Path $payload)) { throw "payload.zip not created" }

Write-Host "[4/5] build setup exe"
dotnet publish $installerProj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:Version=$Version `
    -o dist -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "installer publish failed" }

Write-Host "[5/5] build portable zip"
$portableZip = "dist\STJMediaViewer-$Version-portable-win-x64.zip"
if (Test-Path $portableZip) { Remove-Item $portableZip -Force }
Push-Location $publishDir
& $sevenZip a -tzip -mx=5 "..\STJMediaViewer-$Version-portable-win-x64.zip" ".\*" | Out-Null
Pop-Location

Write-Host ""
Write-Host "done:"
Get-ChildItem dist -Filter "STJMediaViewer-*" | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize