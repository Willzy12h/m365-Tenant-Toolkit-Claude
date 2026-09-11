@echo off
setlocal
cd /d "%~dp0"
echo BDIT Tenant Toolkit - first build.
echo This installs a user-local .NET 8 SDK into .dotnet\ if needed (no admin rights), builds, tests and packages.
echo The first run downloads about 250 MB and takes several minutes.
echo.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Setup-And-Build.ps1" %*
set "BDIT_EXIT=%ERRORLEVEL%"
echo.
if "%BDIT_EXIT%"=="0" (
  echo Build complete. Open the dist\ folder and run Start.cmd inside the BDIT-Tenant-Toolkit-* folder.
) else (
  echo Build failed with exit code %BDIT_EXIT%. The full output is in build\last-build.log - just say "done" and it will be read from there.
)
pause
exit /b %BDIT_EXIT%
