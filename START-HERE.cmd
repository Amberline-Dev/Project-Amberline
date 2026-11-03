@echo off
title Amberline setup
rem Double-click this file. It starts the Amberline setup guide (tools\guide.ps1) in Windows PowerShell.
if not exist "%~dp0tools\guide.ps1" goto missing
set "GUIDE_SHELL=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
rem From a 32-bit window, Sysnative is the way to the 64-bit Windows PowerShell.
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "GUIDE_SHELL=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%GUIDE_SHELL%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\guide.ps1" %*
set "GUIDE_EXIT=%errorlevel%"
echo.
echo Press Enter to close.
pause >nul
exit /b %GUIDE_EXIT%
:missing
echo Some Amberline files are missing. Unzip the whole download, then double-click START-HERE.cmd in that folder.
echo.
echo Press Enter to close.
pause >nul
exit /b 1
