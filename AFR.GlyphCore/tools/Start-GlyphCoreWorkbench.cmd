@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "SCRIPT_PATH=%SCRIPT_DIR%Start-GlyphCoreWorkbench.ps1"

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_PATH%" %*
exit /b %ERRORLEVEL%
