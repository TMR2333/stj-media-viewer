@echo off
REM ============================================================
REM  stj-media-viewer 一键构建 + 运行
REM ============================================================
setlocal
cd /d "%~dp0"

echo [1/3] 构建 Core + App ...
dotnet build src\StjMediaViewer.App\StjMediaViewer.App.csproj -v q --nologo
if errorlevel 1 goto :fail

echo [2/3] 构建命令行工具（可选）...
dotnet build tools\Seed\StjMediaViewer.Seed.csproj -v q --nologo
dotnet build tools\SelfCheck\StjMediaViewer.SelfCheck.csproj -v q --nologo

echo [3/3] 启动 ...
start "" "src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe"
exit /b 0

:fail
echo 构建失败，请检查上面的错误输出。
exit /b 1