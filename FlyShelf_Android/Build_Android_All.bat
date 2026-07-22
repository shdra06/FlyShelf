@echo off
color 0b
echo ==============================================
echo FlyShelf - Android Compiler Pipeline (Emulator + Real Device - Dual Arch)
echo ==============================================

cd /d "%~dp0..\FlyShelf_Android"

echo Clearing previous builds and generating native code...
set "CI=true"
call npx expo prebuild --platform android --clean

echo.
echo Restoring SDK Routes...
cd android
echo sdk.dir=%LOCALAPPDATA:\=\\%\\Android\\Sdk>local.properties

echo.
echo Appending performance configs to gradle.properties...
echo org.gradle.jvmargs=-Xmx6g -XX:MaxMetaspaceSize=1g -XX:+UseParallelGC>> gradle.properties
echo org.gradle.parallel=true>> gradle.properties
echo org.gradle.configureondemand=true>> gradle.properties
echo org.gradle.caching=true>> gradle.properties
echo org.gradle.workers.max=4>> gradle.properties

echo.
echo Compiling Android APK natively (Stable Speed: 4 Workers, 6GB JVM Heap, arm64-v8a + x86_64 emulators)...
call gradlew assembleRelease -PreactNativeArchitectures=arm64-v8a,x86_64

echo.
echo Re-packaging...
cd ..
if exist "%~dp0..\FlyShelf_Mobile_All.apk" (
    echo Deleting previous APK from root...
    del /F /Q "%~dp0..\FlyShelf_Mobile_All.apk"
)
copy /Y "android\app\build\outputs\apk\release\app-release.apk" "%~dp0..\FlyShelf_Mobile_All.apk" >nul

cd /d "%~dp0"
echo ==============================================
echo [SUCCESS] Dual-Architecture APK Compilation Complete!
echo You can find the latest "FlyShelf_Mobile_All.apk"
echo sitting cleanly inside the root FlyShelf directory.
echo ==============================================
