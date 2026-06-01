@echo off
setlocal enabledelayedexpansion

echo ==============================================
echo FlyShelf - Microsoft Store Build Pipeline
echo ==============================================
echo.
echo This script builds the MSIX package for Microsoft Store submission.
echo It uses the SAME source code as the main build, but with:
echo   - MSIX_STORE flag enabled (hides auto-updater, terminal launches, etc.)
echo   - MSIX packaging (produces .msix instead of standalone .exe)
echo   - Store-compliant configuration
echo ==============================================
echo.

:: ─── Step 0: Kill running instances ───
echo [0/5] Terminating any active FlyShelf processes...
taskkill /f /im FlyShelf.exe >nul 2>&1
timeout /t 1 /nobreak >nul

:: ─── Step 1: Staging MSIX manifest and assets ───
echo [1/5] Staging MSIX manifest, assets, and checking secure agent...

:: Validate presence of cloudflared.exe for Store compliance
if not exist "%~dp0agent\cloudflared.exe" (
    echo.
    echo    ======================================================================
    echo    ⚠️  WARNING: MicrosoftBuild\agent\cloudflared.exe is MISSING!
    echo.
    echo    To comply with Microsoft Store Policy 10.2.1, the secure Cloudflare 
    echo    agent must be bundled inside the package. 
    echo    Please place 'cloudflared.exe' in:
    echo       %~dp0agent\cloudflared.exe
    echo.
    echo    The build will proceed, but Global Sync will be inactive at runtime.
    echo    ======================================================================
    echo.
) else (
    echo    ✓ Secure agent found in agent\cloudflared.exe (will be bundled)
)

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
echo    Manifest + %NUMBER_OF_PROCESSORS% assets staged.

:: ─── Step 2: Build MSIX package ───
echo [2/5] Compiling MSIX package (Release, x64, Store mode)...
echo.

cd /d "%~dp0..\FlyShelf_PC"

dotnet publish FlyShelf.csproj ^
    -c Release ^
    -r win-x64 ^
    -p:StorePublish=true ^
    -p:WindowsPackageType=MSIX ^
    -p:AppxPackageSigningEnabled=false ^
    -p:GenerateAppxPackageOnBuild=true ^
    -p:AppxPackageDir="%~dp0Output\" ^
    -p:SelfContained=true ^
    -p:Platform=x64

if errorlevel 1 (
    echo.
    echo ══════════════════════════════════════════
    echo [FAILED] MSIX build failed! Check errors above.
    echo ══════════════════════════════════════════
    goto :cleanup
)

echo.
echo [3/5] Build succeeded!

:: ─── Step 4: Show output ───
echo [4/5] Locating MSIX package...
echo.
echo    Output directory: %~dp0Output\
dir /B "%~dp0Output\*.msix" 2>nul
dir /B "%~dp0Output\*.msixbundle" 2>nul
dir /B "%~dp0Output\*.appx" 2>nul
echo.

:: ─── Step 5: Cleanup staged files ───
:cleanup
echo [5/5] Cleaning up staged files from project directory...
del /Q "%~dp0..\FlyShelf_PC\Package.appxmanifest" >nul 2>&1
rmdir /S /Q "%~dp0..\FlyShelf_PC\Assets" >nul 2>&1
echo    Cleanup done.

echo.
echo ==============================================
echo [DONE] Microsoft Store build pipeline complete!
echo.
echo NEXT STEPS:
echo   1. Test the MSIX by double-clicking it to install
echo   2. Sign in to Partner Center: https://partner.microsoft.com
echo   3. Create your app submission
echo   4. Upload the .msix/.msixupload file
echo   5. Fill in Store listing (description, screenshots, privacy URL)
echo   6. Submit for certification
echo ==============================================

endlocal
