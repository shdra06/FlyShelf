@echo off
color 0b
echo ==============================================
echo FlyShelf - Android Compiler Pipeline (Real Device - arm64-v8a Only)
echo ==============================================

cd /d "%~dp0..\FlyShelf_Android"

echo Clearing previous builds and generating native code...
set "CI=true"
call npx expo prebuild --platform android --clean

echo.
echo Restoring SDK Routes...
cd android
echo sdk.dir=C\:\\Users\\Shivendra\\AppData\\Local\\Android\\Sdk>local.properties

echo.
echo Appending performance configs to gradle.properties...
echo org.gradle.jvmargs=-Xmx6g -XX:MaxMetaspaceSize=1g -XX:+UseParallelGC>> gradle.properties
echo org.gradle.parallel=true>> gradle.properties
echo org.gradle.configureondemand=true>> gradle.properties
echo org.gradle.caching=true>> gradle.properties
echo org.gradle.workers.max=4>> gradle.properties

echo.
echo Compiling Android APK natively (Stable Speed: 4 Workers, 6GB JVM Heap, arm64-v8a Only)...
call gradlew.bat assembleRelease -PreactNativeArchitectures=arm64-v8a

echo.
echo Re-packaging...
cd ..
if exist "%~dp0..\FlyShelf_Mobile_Device.apk" (
    echo Deleting previous APK from root...
    del /F /Q "%~dp0..\FlyShelf_Mobile_Device.apk"
)
copy /Y "android\app\build\outputs\apk\release\app-release.apk" "%~dp0..\FlyShelf_Mobile_Device.apk" >nul

cd /d "%~dp0"
echo ==============================================
echo [SUCCESS] Real Device APK Compilation Complete!
echo You can find the latest "FlyShelf_Mobile_Device.apk"
echo sitting cleanly inside the root FlyShelf directory.
echo ==============================================
