@echo off
setlocal enabledelayedexpansion
color 0b
echo ==============================================
echo FlyShelf - Android Compiler Pipeline (Real Device - arm64-v8a Only)
echo ==============================================

cd /d "%~dp0..\FlyShelf_Android"

echo.
echo [1/6] Releasing background locks and stopping daemons...
if exist "android\gradlew.bat" (
    pushd "android"
    call gradlew.bat --stop >nul 2>&1
    popd
)

:: Terminate lingering Java / Gradle workers holding file locks on build caches
taskkill /F /IM java.exe /T >nul 2>&1
taskkill /F /IM javaw.exe /T >nul 2>&1
timeout /t 1 /nobreak >nul

:: Force remove any leftover locked build folders
if exist "android\app\build" (
    echo Cleaning locked build caches...
    rmdir /s /q "android\app\build" >nul 2>&1
)
if exist "android\.gradle" (
    rmdir /s /q "android\.gradle" >nul 2>&1
)

echo.
echo [2/6] Generating clean native Android project (Expo Prebuild)...
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
echo [3/6] Configuring Android SDK and Performance Profiles...
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
echo [4/6] Compiling Android APK natively (arm64-v8a Real Device)...
call gradlew.bat assembleRelease -PreactNativeArchitectures=arm64-v8a
set "BUILD_EXIT_CODE=%ERRORLEVEL%"

:: Stop gradle daemon immediately after compile to free RAM and release all file locks
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
echo [5/6] Verifying and packaging APK...
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
echo [SUCCESS] Real Device APK Compilation Complete!
echo Latest APK created: FlyShelf_Mobile_Device.apk
echo Location: %DEST_APK%
echo ==============================================
