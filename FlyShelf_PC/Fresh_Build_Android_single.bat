@echo off
setlocal enabledelayedexpansion
color 0e
echo ==============================================
echo FlyShelf - FRESH Android Build (Nuclear Clean)
echo ==============================================
echo.
echo This script performs a FULL clean rebuild:
echo   - Kills all Java/Gradle daemons
echo   - Deletes android/ folder entirely
echo   - Clears Metro bundler cache
echo   - Clears Expo cache
echo   - Runs expo prebuild --clean
echo   - Builds fresh APK (arm64-v8a)
echo.
echo ==============================================
echo.

cd /d "%~dp0..\FlyShelf_Android"

echo [1/8] Killing ALL background processes...
taskkill /F /IM java.exe /T >nul 2>&1
taskkill /F /IM javaw.exe /T >nul 2>&1
timeout /t 2 /nobreak >nul

echo.
echo [2/8] Nuking native android/ folder (full clean)...
if exist "android" (
    rmdir /s /q "android" >nul 2>&1
    if exist "android" (
        echo Retrying android folder removal...
        timeout /t 3 /nobreak >nul
        rmdir /s /q "android" >nul 2>&1
    )
)
if exist "android" (
    echo [WARN] Could not fully remove android/ folder. Some files may be locked.
    echo        Close any editors/terminals accessing android/ and retry.
)

echo.
echo [3/8] Clearing Metro bundler cache...
if exist "%TEMP%\metro-cache" rmdir /s /q "%TEMP%\metro-cache" >nul 2>&1
for /d %%D in ("%TEMP%\metro-*") do rmdir /s /q "%%D" >nul 2>&1
for /d %%D in ("%TEMP%\haste-map-*") do rmdir /s /q "%%D" >nul 2>&1

echo.
echo [4/8] Clearing Expo cache...
if exist ".expo" rmdir /s /q ".expo" >nul 2>&1

echo.
echo [5/8] Generating fresh native Android project (Expo Prebuild --clean)...
set "CI=true"
call npx expo prebuild --platform android --clean
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ==============================================
    echo [ERROR] Expo prebuild failed!
    echo Could not generate native Android project.
    echo ==============================================
    pause
    exit /b 1
)

if not exist "android\gradlew.bat" (
    echo.
    echo ==============================================
    echo [ERROR] Native android folder or gradlew.bat was not created!
    echo ==============================================
    pause
    exit /b 1
)

echo.
echo [6/8] Configuring Android SDK and Performance Profiles...
cd android

:: Detect or set SDK path
set "FOUND_SDK="
if defined ANDROID_HOME if exist "%ANDROID_HOME%\platforms" set "FOUND_SDK=%ANDROID_HOME%"
if not defined FOUND_SDK if defined ANDROID_SDK_ROOT if exist "%ANDROID_SDK_ROOT%\platforms" set "FOUND_SDK=%ANDROID_SDK_ROOT%"
if not defined FOUND_SDK if exist "%LOCALAPPDATA%\Android\Sdk\platforms" set "FOUND_SDK=%LOCALAPPDATA%\Android\Sdk"
if not defined FOUND_SDK if exist "C:\Users\Shivendra\AppData\Local\Android\Sdk\platforms" set "FOUND_SDK=C:\Users\Shivendra\AppData\Local\Android\Sdk"

if defined FOUND_SDK (
    set "FORMATTED_SDK=!FOUND_SDK:\=\\!"
    set "FORMATTED_SDK=!FORMATTED_SDK::=\:!"
    echo sdk.dir=!FORMATTED_SDK!> local.properties
    echo Configured Android SDK from: !FOUND_SDK!
) else (
    echo sdk.dir=C\:\\Users\\Shivendra\\AppData\\Local\\Android\\Sdk> local.properties
    echo Falling back to default SDK path.
)

:: Clear Gradle caches for this project
echo.
echo [6.5/8] Cleaning Gradle caches for fresh compile...
if exist ".gradle" rmdir /s /q ".gradle" >nul 2>&1
if exist "app\build" rmdir /s /q "app\build" >nul 2>&1
if exist "build" rmdir /s /q "build" >nul 2>&1

:: Append JVM and Gradle performance flags if not already present
findstr /C:"org.gradle.jvmargs" gradle.properties >nul 2>&1
if %ERRORLEVEL% NEQ 0 echo org.gradle.jvmargs=-Xmx6g -XX:MaxMetaspaceSize=1g -XX:+UseParallelGC>> gradle.properties

findstr /C:"org.gradle.parallel" gradle.properties >nul 2>&1
if %ERRORLEVEL% NEQ 0 echo org.gradle.parallel=true>> gradle.properties

findstr /C:"org.gradle.configureondemand" gradle.properties >nul 2>&1
if %ERRORLEVEL% NEQ 0 echo org.gradle.configureondemand=true>> gradle.properties

findstr /C:"org.gradle.caching" gradle.properties >nul 2>&1
if %ERRORLEVEL% NEQ 0 echo org.gradle.caching=true>> gradle.properties

findstr /C:"org.gradle.workers.max" gradle.properties >nul 2>&1
if %ERRORLEVEL% NEQ 0 echo org.gradle.workers.max=4>> gradle.properties

echo.
echo [7/8] Compiling Android APK (arm64-v8a Real Device)...
echo      This may take 10-20 minutes for a fresh build...
call gradlew.bat assembleRelease -PreactNativeArchitectures=arm64-v8a --no-build-cache
set "BUILD_EXIT_CODE=%ERRORLEVEL%"

:: Stop gradle daemon immediately after compile to free RAM
call gradlew.bat --stop >nul 2>&1

if %BUILD_EXIT_CODE% NEQ 0 (
    echo.
    echo ==============================================
    echo [ERROR] Gradle build failed with exit code %BUILD_EXIT_CODE%!
    echo Please review the build errors above.
    echo ==============================================
    cd ..
    pause
    exit /b %BUILD_EXIT_CODE%
)

echo.
echo [8/8] Verifying and packaging APK...
cd ..
set "TARGET_APK=android\app\build\outputs\apk\release\app-release.apk"
set "DEST_APK=%~dp0..\FlyShelf_Mobile_Device.apk"

if not exist "!TARGET_APK!" (
    echo.
    echo ==============================================
    echo [ERROR] Compiled APK not found at:
    echo !TARGET_APK!
    echo Build may have completed without generating the release binary.
    echo ==============================================
    pause
    exit /b 1
)

if exist "!DEST_APK!" (
    del /F /Q "!DEST_APK!" >nul 2>&1
)

copy /Y "!TARGET_APK!" "!DEST_APK!" >nul
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Failed to copy APK to destination root!
    pause
    exit /b 1
)

cd /d "%~dp0"
echo.
echo ==============================================
echo [SUCCESS] FRESH APK Build Complete!
echo Latest APK created: FlyShelf_Mobile_Device.apk
echo Location: %DEST_APK%
echo.
echo All caches were cleared. This is a 100%% clean build.
echo ==============================================
pause
