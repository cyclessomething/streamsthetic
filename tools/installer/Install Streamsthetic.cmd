@echo off
rem Installs the PC app and asks for administrator approval for the camera component.
rem This testing build is unsigned. For a copy you trust, choose "More info", then "Run anyway" if Windows warns.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
