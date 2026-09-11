@echo off
setlocal
cd /d "%~dp0.."
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-Portable.ps1" %*
set "BDIT_EXIT=%ERRORLEVEL%"
if not "%BDIT_EXIT%"=="0" echo Build failed with exit code %BDIT_EXIT%.
exit /b %BDIT_EXIT%
