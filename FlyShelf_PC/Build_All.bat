@echo off
color 0a
echo ==============================================
echo FlyShelf - Standalone Compiler Pipeline
echo ==============================================

if not exist "FINAL" mkdir "FINAL"

echo.
echo [1/3] Purging heavy uncompiled local Runtime Caches...
FOR /d /r . %%d in (__pycache__) DO @if exist "%%d" rd /s /q "%%d" >nul 2>nul

echo.
echo [2/3] Compiling Desktop C# Executable (Self-Contained + Compressed Single-File Win64, Multi-Core Accelerated)...
dotnet publish FlyShelf.csproj -c Release -r win-x64 --self-contained -m -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o "FINAL"

:: Aggressive cleanup: Nuke any lingering unzipped C++ libraries or collateral output to guarantee 100% single-file purity.
del /Q "FINAL\*.pdb" "FINAL\*.config" "FINAL\*.dll" "FINAL\*.json" >nul 2>nul

echo.
echo [3/3] Initiating Android APK Rebuild pipeline natively (Fast Compile: Uncapped Workers, 16GB JVM, arm64-v8a only)...
call rebuild_apk.bat

echo.
echo ==============================================
echo [SUCCESS] Standalone Compilation Complete!
echo You can find the pure standalone "FlyShelf.exe"
echo and "FlyShelf_Mobile.apk" sitting cleanly inside 
echo the 'FINAL' directory.
echo ==============================================
