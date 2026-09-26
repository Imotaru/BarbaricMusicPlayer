@echo off
rem Builds a Release copy of the player (web UI included) into publish\.
setlocal
cd /d "%~dp0"

tasklist /fi "imagename eq Barbaric.App.exe" | findstr /i "Barbaric.App.exe" >nul
if not errorlevel 1 (
    echo Barbaric Music Player is running. Close it first, then run this again.
    if "%~1" neq "--no-pause" pause
    exit /b 1
)

dotnet publish src\Barbaric.App -c Release -r win-x64 --self-contained false -o publish
if errorlevel 1 (
    echo.
    echo Build failed.
    if "%~1" neq "--no-pause" pause
    exit /b 1
)

echo.
echo Built publish\Barbaric.App.exe
