@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Manage-Startup.ps1" %*
set "startupExitCode=%ERRORLEVEL%"
pause
exit /b %startupExitCode%
