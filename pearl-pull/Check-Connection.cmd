@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Check-Connection.ps1" %*
set "checkExitCode=%ERRORLEVEL%"
pause
exit /b %checkExitCode%
