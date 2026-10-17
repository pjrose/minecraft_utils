@echo off
title Pearl Pull - Stream Deck Neo
echo Use Setup-StreamDeck.cmd to get the Neo's direct trigger URL.
echo Set the Neo button to System - Website and enable GET request in background.
echo The button artwork is in icons\pearl-pull.png.
echo Keep this window open while playing. You can minimize it.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Sender.ps1" %*
set "neoExitCode=%ERRORLEVEL%"
pause
exit /b %neoExitCode%
