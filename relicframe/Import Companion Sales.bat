@echo off
setlocal
set "IMPORTER=%~dp0companion_chat_importer.py"
where py >nul 2>nul
if not errorlevel 1 (
    py -3 "%IMPORTER%"
) else (
    python "%IMPORTER%"
)

echo.
pause
endlocal
