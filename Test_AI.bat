@echo off
title FlyShelf — Local AI Inference Test
echo =====================================================================
echo                FlyShelf Local AI Inference Diagnostic Test
echo =====================================================================
echo.
echo  This script will test if your PC's RTX 4060 GPU and Windows 11
echo  system components are ready to run local AI models (Phi Silica) offline.
echo.
echo  What this test does:
echo  1. Checks if the FlyShelf Sparse Package registration is active.
echo  2. If not registered, extracts the MSIX assets and imports the developer
echo     certificate to LocalMachine\TrustedPeople (triggers a one-time UAC prompt).
echo  3. Registers the Flyshelf.FlyShelfSparse package to your local Windows OS.
echo  4. Launches FlyShelf in diagnostic mode to invoke Windows Copilot Runtime.
echo  5. Sends a live translation prompt to your GPU: 
echo     "Translate 'Hello, how are you?' into French in 3 words."
echo  6. Outputs the compatibility status and offline model response directly here.
echo.
echo ---------------------------------------------------------------------
echo  Starting test execution...
echo ---------------------------------------------------------------------
echo.

cd /d "%~dp0"
FlyShelf_PC\bin\Debug\net10.0-windows10.0.19041.0\FlyShelf.exe --test-ai

echo.
echo ---------------------------------------------------------------------
echo  Diagnostic run complete.
echo  - Look for "RESULT: YES, COMPATIBLE" or "RESULT: NO, NOT COMPATIBLE" above.
echo  - If it failed, check the error details above or check:
echo    %%APPDATA%%\FlyShelf\Logs\activity_log.txt.
echo ---------------------------------------------------------------------
echo.
pause
