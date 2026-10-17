@echo off
if not exist "%LOCALAPPDATA%\MinecraftUtils\logs" (
    echo No logs yet. Run the updated sender, receiver, or hotkey test first.
    pause
    exit /b 1
)
explorer.exe "%LOCALAPPDATA%\MinecraftUtils\logs"
