@echo off
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo .NET SDK not found. In Terminal run:  winget install Microsoft.DotNet.SDK.8 & pause & exit /b 1)
echo Building and starting Droppo. Keep this window open while you use it.
dotnet run --project src\Droppo -c Release
if errorlevel 1 pause
