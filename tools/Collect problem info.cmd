@echo off
rem Run on the PC with the problem after finishing active camera sessions.
rem Starts and closes a test app process, runs the video component and checks local network connections.
rem Saves Streamsthetic-problem-report.txt on the Desktop. Review it before sharing; nothing is uploaded.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect_problem_info.ps1"
pause
