@echo off
setlocal
cd /d "%~dp0"

set "APP=src\StjMediaViewer.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.exe"

if exist "%APP%" (
    start "" "%APP%"
    exit /b 0
)

echo ============================================================
echo  STJ Media Viewer - first run, building (about 30-60s)...
echo ============================================================
where dotnet >nul 2>nul
if errorlevel 1 goto nodotnet

dotnet build src\StjMediaViewer.App\StjMediaViewer.App.csproj -v q --nologo
if errorlevel 1 goto fail
if not exist "%APP%" goto fail

start "" "%APP%"
exit /b 0

:nodotnet
echo.
echo [ERROR] dotnet SDK not found in PATH.
echo         Install .NET 10 SDK: https://dotnet.microsoft.com/download
goto fail

:fail
echo.
echo [ERROR] Build failed or app not produced. Check the messages above.
pause
exit /b 1