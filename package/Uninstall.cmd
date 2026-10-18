@echo off
rem Removes the SE2VR mod (the %LOCALAPPDATA%\SE2VR folder and the desktop shortcut).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0files\Uninstall-SE2VR.ps1"
echo.
pause
