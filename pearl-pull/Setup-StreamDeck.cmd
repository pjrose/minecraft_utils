@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Sender.ps1" -ShowStreamDeckUrl %*
set "neoSetupExitCode=%ERRORLEVEL%"
pause
exit /b %neoSetupExitCode%
