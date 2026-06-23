@echo off
setlocal enabledelayedexpansion

echo ==============================================
echo FlyShelf - Microsoft Store Build Pipeline
echo ==============================================
echo.
echo This script builds the MSIX package for Microsoft Store submission.
echo It uses the SAME source code as the main build, but with:
echo   - MSIX_STORE flag enabled (hides auto-updater, terminal launches, etc.)
echo   - MSIX packaging (produces .msixupload for Store ingestion)
echo   - Store-compliant configuration (UNSIGNED - Microsoft signs it)
echo ==============================================
echo.

:: --- Step 0: Kill running instances ---
echo [0/6] Terminating any active FlyShelf processes...
taskkill /f /im FlyShelf.exe >nul 2>&1
timeout /t 1 /nobreak >nul

:: --- Step 1: Clean old output ---
echo [1/6] Cleaning previous build output...
del /Q "%~dp0Output\*.msix" >nul 2>&1
del /Q "%~dp0Output\*.msixupload" >nul 2>&1
del /Q "%~dp0Output\*.msixbundle" >nul 2>&1
del /Q "%~dp0Output\*.appx" >nul 2>&1
del /Q "%~dp0Output\*.appxupload" >nul 2>&1
echo    Old outputs cleaned.

:: --- Step 2: Staging MSIX manifest and assets ---
echo [2/6] Staging MSIX manifest, assets, and checking secure agent...

:: cloudflared.exe is intentionally EXCLUDED from Store builds.
:: Unsigned native PE files cause Microsoft's signing pipeline to fail.
:: LAN sync works without it. Global sync unavailable in Store version.
echo    NOTE: cloudflared.exe excluded from Store build (unsigned PE)

:: Copy Package.appxmanifest to project root (required by SDK)
copy /Y "%~dp0Package.appxmanifest" "%~dp0..\FlyShelf_PC\Package.appxmanifest" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy Package.appxmanifest
    exit /b 1
)

:: Copy Assets folder
if not exist "%~dp0..\FlyShelf_PC\Assets" mkdir "%~dp0..\FlyShelf_PC\Assets"
xcopy /Y /E /Q "%~dp0Assets\*" "%~dp0..\FlyShelf_PC\Assets\" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy Assets
    exit /b 1
)
echo    Manifest + assets staged.

:: --- Step 3: Build MSIX package ---
echo [3/6] Compiling MSIX package (Release, x64, StoreUpload mode)...
echo.

cd /d "%~dp0..\FlyShelf_PC"

dotnet publish FlyShelf.csproj ^
    -c Release ^
    -r win-x64 ^
    -p:StorePublish=true ^
    -p:WindowsPackageType=MSIX ^
    -p:AppxPackageSigningEnabled=false ^
    -p:GenerateAppxPackageOnBuild=true ^
    -p:AppxBundle=Never ^
    -p:UapAppxPackageBuildMode=StoreUpload ^
    -p:AppxPackageDir="%~dp0Output\" ^
    -p:SelfContained=true ^
    -p:Platform=x64

if errorlevel 1 (
    echo.
    echo ==============================================
    echo [FAILED] MSIX build failed! Check errors above.
    echo ==============================================
    goto :cleanup
)

echo.
echo [4/6] Build succeeded!

:: --- Step 5: Locate output ---
echo [5/6] Locating output...
echo.
echo    Output directory: %~dp0Output\
dir /B "%~dp0Output\*.msix" 2>nul
dir /B "%~dp0Output\*.msixupload" 2>nul
echo.

:: --- Step 6: Cleanup staged files ---
:cleanup
echo [6/6] Cleaning up staged files from project directory...
del /Q "%~dp0..\FlyShelf_PC\Package.appxmanifest" >nul 2>&1
rmdir /S /Q "%~dp0..\FlyShelf_PC\Assets" >nul 2>&1
echo    Cleanup done.

echo.
echo ==============================================
echo [DONE] Microsoft Store build pipeline complete!
echo.
echo NEXT STEPS:
echo   1. Sign in to Partner Center: https://partner.microsoft.com
echo   2. Go to your FlyShelf Clipboard submission
echo   3. Upload the .msix file from Output folder
echo   4. Click "Resubmit for certification"
echo ==============================================

endlocal
