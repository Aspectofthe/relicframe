@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-bot.ps1" %*
exit /b %ERRORLEVEL%
