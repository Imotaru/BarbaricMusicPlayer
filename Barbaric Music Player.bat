@echo off
rem Launches the player, building it first if publish\ is empty. Run build.bat to pick up new changes.
setlocal
cd /d "%~dp0"

if not exist "publish\Barbaric.App.exe" (
    call build.bat --no-pause
    if errorlevel 1 (
        pause
        exit /b 1
    )
)

start "" "%~dp0publish\Barbaric.App.exe" %*
