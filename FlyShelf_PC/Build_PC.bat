@echo off
color 0a
echo ==============================================
echo FlyShelf - Desktop App Compiler Pipeline (PC Only)
echo ==============================================

echo.
echo [0/2] Terminating any active FlyShelf PC processes to unlock paths...
taskkill /f /im FlyShelf.exe >nul 2>nul
ping 127.0.0.1 -n 2 >nul

if not exist "FINAL" mkdir "FINAL"

echo.
echo [1/2] Purging heavy uncompiled local Runtime Caches...
FOR /d /r . %%d in (__pycache__) DO @if exist "%%d" rd /s /q "%%d" >nul 2>nul

echo.
echo [2/2] Compiling Desktop C# Executable (Self-Contained + Compressed Single-File Win64, Multi-Core Accelerated)...
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
echo ==============================================
echo [SUCCESS] PC Compilation Complete!
echo You can find the pure standalone "FlyShelf.exe"
echo sitting cleanly inside the 'FINAL' directory.
echo.
echo Launching the freshly compiled FlyShelf.exe...
echo ==============================================
start "" "FINAL\FlyShelf.exe"
