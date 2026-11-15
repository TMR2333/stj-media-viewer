@echo off
setlocal
cd /d "%~dp0"

set "SELFCHECK=tools\SelfCheck\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.SelfCheck.exe"

echo [1/4] generate sample library ...
where python >nul 2>nul
if errorlevel 1 (
    echo [WARN] python not found in PATH, skip sample generation.
) else (
    python tools\SampleLibrary\generate.py
)

echo [2/4] build SelfCheck ...
dotnet build tools\SelfCheck\StjMediaViewer.SelfCheck.csproj -v q --nologo
if errorlevel 1 goto fail

echo [3/4] run end-to-end self check ...
"%SELFCHECK%"
set "RC=%errorlevel%"

echo.
echo [4/4] exit code: %RC%   (0 = all checks passed)
if not "%RC%"=="0" pause
exit /b %RC%

:fail
echo.
echo [ERROR] build failed.
pause
exit /b 1