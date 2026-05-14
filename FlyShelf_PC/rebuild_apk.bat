@echo off

cd ..\FlyShelf_Android

echo Clearing previous builds and generating native code...
set "CI=true"
call npx expo prebuild --platform android --clean

echo.
echo Restoring SDK Routes...
cd android
echo sdk.dir=C\:\\Users\\Shivendra\\AppData\\Local\\Android\\Sdk>local.properties

echo.
echo Compiling Android APK natively...
call gradlew assembleRelease

echo.
echo Re-packaging...
cd ..
if not exist "..\FlyShelf_PC\FINAL" mkdir "..\FlyShelf_PC\FINAL"
copy /Y "android\app\build\outputs\apk\release\app-release.apk" "..\FlyShelf_PC\FINAL\FlyShelf_Mobile.apk" >nul

cd ..\FlyShelf_PC
echo ==============================================
echo DONE! The updated APK is in 'FlyShelf_PC\FINAL'.
echo ==============================================
