@echo off
title Pearl Pull - Stream Deck Neo
echo Configure your Neo button as System - Hotkey: F8 before starting this sender.
echo The button artwork is in icons\pearl-pull.png.
echo Keep this window open while playing. You can minimize it.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Sender.ps1" -FunctionKey 8
pause
