@echo off
cd /d "%~dp0"
echo Building Droppo-Setup.exe. This takes a few minutes the first time (it downloads FFmpeg).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\build.ps1" %*
pause
