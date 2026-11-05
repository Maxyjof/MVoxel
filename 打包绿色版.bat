@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\package-portable.ps1" -KeepArchives 5
if errorlevel 1 (
    echo Packaging failed. See the message above.
    pause
    exit /b 1
)
pause
