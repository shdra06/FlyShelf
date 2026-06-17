@echo off
color 0a
echo ==============================================
echo FlyShelf - Desktop App Compiler Pipeline (PC Only)
echo ==============================================

echo.
echo [0/4] Terminating any active FlyShelf PC processes to unlock paths...
taskkill /f /im FlyShelf.exe >nul 2>nul
ping 127.0.0.1 -n 2 >nul

if not exist "FINAL" mkdir "FINAL"

echo.
echo [1/4] Purging heavy uncompiled local Runtime Caches...
FOR /d /r . %%d in (__pycache__) DO @if exist "%%d" rd /s /q "%%d" >nul 2>nul

echo.
echo [2/4] Compiling Desktop C# Executable (Self-Contained + Compressed Single-File Win64, Multi-Core Accelerated)...
dotnet publish FlyShelf.csproj -c Release -r win-x64 --self-contained -m -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o "FINAL"

if %errorlevel% neq 0 (
    echo.
    echo ==============================================
    echo [ERROR] PC Compilation FAILED!
    echo ==============================================
    exit /b %errorlevel%
)

:: Aggressive cleanup: Nuke any lingering unzipped C++ libraries or collateral output to guarantee 100% single-file purity.
del /Q "FINAL\*.pdb" "FINAL\*.config" "FINAL\*.dll" "FINAL\*.json" >nul 2>nul

echo.
echo [3/4] Computing SHA-256 hash of compiled FlyShelf.exe...

:: Compute SHA-256 using PowerShell (built-in on all modern Windows)
for /f "delims=" %%H in ('powershell -NoProfile -Command "(Get-FileHash -Path 'FINAL\FlyShelf.exe' -Algorithm SHA256).Hash.ToLower()"') do set "NEWHASH=%%H"

if "%NEWHASH%"=="" (
    echo [WARNING] Could not compute SHA-256 hash! Skipping hash update.
    goto :launch
)

echo    Hash: %NEWHASH%

:: Write the .sha256 file (upload this alongside FlyShelf.exe on GitHub Releases)
echo %NEWHASH%  FlyShelf.exe> "FINAL\FlyShelf.exe.sha256"
echo    Saved: FINAL\FlyShelf.exe.sha256

:: Read version from .csproj so version.json is always in sync with the compiled binary
for /f "delims=" %%V in ('powershell -NoProfile -Command "([xml](Get-Content 'FlyShelf.csproj')).Project.PropertyGroup[0].Version"') do set "APPVER=%%V"

if "%APPVER%"=="" (
    echo [WARNING] Could not read version from .csproj! Skipping version.json update.
    goto :launch
)

echo    Version: %APPVER%

echo.
echo [4/4] Updating version.json with new version, download URL, and hash...

powershell -NoProfile -Command "$json = Get-Content '..\version.json' -Raw | ConvertFrom-Json; $json.pc_version = '%APPVER%'; $json.pc_download = 'https://github.com/shdra06/FlyShelf/releases/download/v%APPVER%/FlyShelf.exe'; $json.pc_sha256 = '%NEWHASH%'; $json | ConvertTo-Json -Depth 10 | Set-Content '..\version.json' -Encoding UTF8; Write-Host '    Updated version.json:'; Write-Host '      pc_version  = %APPVER%'; Write-Host '      pc_download = https://github.com/shdra06/FlyShelf/releases/download/v%APPVER%/FlyShelf.exe'; Write-Host '      pc_sha256   = %NEWHASH%'"

echo.
echo ==============================================
echo [SUCCESS] PC Compilation Complete!
echo.
echo   EXE:     FINAL\FlyShelf.exe
echo   VERSION: %APPVER%
echo   HASH:    %NEWHASH%
echo   SHA256:  FINAL\FlyShelf.exe.sha256
echo.
echo RELEASE CHECKLIST:
echo   1. Upload FINAL\FlyShelf.exe to GitHub Release (tag: v%APPVER%)
echo   2. Upload FINAL\FlyShelf.exe.sha256 to GitHub Release
echo   3. Commit and push updated version.json
echo ==============================================

:launch
echo.
echo Launching the freshly compiled FlyShelf.exe...
start "" "FINAL\FlyShelf.exe"

