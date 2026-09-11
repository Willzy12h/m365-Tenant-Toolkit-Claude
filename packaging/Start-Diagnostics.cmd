@echo off
setlocal
cd /d "%~dp0"
echo BDIT Tenant Toolkit - diagnostics start.
echo Verbose logging is enabled for this session. This window stays open and shows the start-up log when the toolkit closes.
echo.
if not exist "app\BDIT.TenantToolkit.App.exe" (
  echo The toolkit application was not found. Extract the complete portable ZIP and try again.
  pause
  exit /b 1
)
"app\BDIT.TenantToolkit.App.exe" --diagnostics
echo.
echo Toolkit exited with code %ERRORLEVEL%.
if exist "logs\startup.log" (
  echo ---- logs\startup.log ----
  type "logs\startup.log"
)
echo.
echo Log files in logs\:
dir /b /o-d "logs\*.jsonl" 2>nul
echo.
pause
exit /b 0
