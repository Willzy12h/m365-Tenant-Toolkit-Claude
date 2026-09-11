@echo off
setlocal
cd /d "%~dp0"
if not exist "app\BDIT.TenantToolkit.App.exe" (
  echo.
  echo   The toolkit application was not found next to this launcher.
  echo   Extract the complete portable ZIP, keeping the folder structure intact, then run Start.cmd again.
  echo.
  pause
  exit /b 1
)
start "" "app\BDIT.TenantToolkit.App.exe"
exit /b 0
