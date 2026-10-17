@echo off
title Pearl Pull - F8 detection test (no clicks)
echo Close the normal sender before this test. Only one app can register this hotkey.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Sender.ps1" -HotkeyTest %*
set "hotkeyTestExitCode=%ERRORLEVEL%"
pause
exit /b %hotkeyTestExitCode%
