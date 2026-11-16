@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "C:\OEM\setup-wpf.ps1"
set "RESULT=%ERRORLEVEL%"
exit /b %RESULT%

