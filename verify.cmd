@echo off
REM ============================================================
REM  一键跑完整验证：生成样本库 → 自检（可选真实 AI 链路）
REM  先手动启动 Mock AI 服务器（可选）：
REM     python tools\MockAiServer\server.py --port 8787
REM ============================================================
setlocal
cd /d "%~dp0"

set PY=python
where python >nul 2>nul || set PY=C:\Users\Administrator\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe

echo [1/4] 生成测试素材库 ...
%PY% tools\SampleLibrary\generate.py
if errorlevel 1 goto :fail

echo [2/4] 构建 SelfCheck ...
dotnet build tools\SelfCheck\StjMediaViewer.SelfCheck.csproj -v q --nologo
if errorlevel 1 goto :fail

echo [3/4] 端到端自检 ...
tools\SelfCheck\bin\Debug\net10.0-windows10.0.19041.0\win-x64\StjMediaViewer.SelfCheck.exe

echo [4/4] 自检退出码：%errorlevel%（0 = 全部通过）
exit /b %errorlevel%

:fail
echo 构建失败。
exit /b 1