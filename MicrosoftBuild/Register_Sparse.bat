@echo off
title FlyShelf — Sparse Package Developer Registration
:: Check for Administrator privileges
net session >nul 2>&1
if %errorLevel% == 0 (
    goto :run
) else (
    echo ======================================================
    echo   Requesting Administrator privileges...
    echo ======================================================
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

:run
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Register_Sparse.ps1"
echo.
echo Press any key to exit...
pause >nul
