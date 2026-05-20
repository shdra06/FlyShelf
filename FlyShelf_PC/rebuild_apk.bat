@echo off

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
echo org.gradle.jvmargs=-Xmx16g -XX:MaxMetaspaceSize=2g -XX:+UseParallelGC >> gradle.properties
echo org.gradle.parallel=true >> gradle.properties
echo org.gradle.configureondemand=true >> gradle.properties
echo org.gradle.caching=true >> gradle.properties

echo.
echo Compiling Android APK natively (Maximum Speed: Uncapped Workers, 16GB JVM Heap, arm64-v8a + x86_64 emulators)...
call gradlew assembleRelease -PreactNativeArchitectures=arm64-v8a,x86_64

echo.
echo Re-packaging...
cd ..
if not exist "%~dp0FINAL" mkdir "%~dp0FINAL"
copy /Y "android\app\build\outputs\apk\release\app-release.apk" "%~dp0FINAL\FlyShelf_Mobile.apk" >nul

cd /d "%~dp0"
echo ==============================================
echo DONE! The updated APK is in 'FlyShelf_PC\FINAL'.
echo ==============================================

