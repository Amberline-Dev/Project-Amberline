@echo off
rem NOT RETAIL: the player's entry for the bring-your-own-clients mode. Double-click this file; it runs the companion beside it.
setlocal
cd /d "%~dp0"
"%~dp0Amberline.AirCompanion.exe" --play %*
set "PLAY_EXIT=%ERRORLEVEL%"
if not "%PLAY_EXIT%"=="0" (
    echo.
    echo Play stopped with exit code %PLAY_EXIT%. Read the message above, then press a key to close this window.
    pause >nul
)
exit /b %PLAY_EXIT%
