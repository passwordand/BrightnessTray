@echo off
setlocal

rem Launcher for BrightnessTray.
rem This file is intentionally ASCII-only: cmd.exe parses .cmd files using the
rem system ANSI codepage, so non-ASCII comments can break the script.

set "ROOT=%~dp0"
set "EXE=%ROOT%src\bin\Release\net10.0-windows\BrightnessTray.exe"

if not exist "%EXE%" (
    echo [ERROR] Executable not found:
    echo   %EXE%
    echo.
    echo Build it first:
    echo   cd /d "%ROOT%src"
    echo   dotnet build -c Release
    echo.
    pause
    exit /b 1
)

rem No pipe here: tasklist piped into find can block waiting on stdin.
rem The app itself already enforces a single instance via a named mutex,
rem so simply launching it is safe.
start "" "%EXE%"
exit /b 0