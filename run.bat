@echo off
set "PROJECT=E:\exeapps\FlyShelf\FlyShelf_PC"
taskkill /IM FlyShelf.exe /F >nul 2>&1
timeout /t 1 /nobreak >nul
echo   Building...
dotnet build "%PROJECT%\FlyShelf.csproj" -c Debug -v:q
if %errorlevel% neq 0 (
    echo   BUILD FAILED
    pause
    exit /b 1
)
echo   Launching...
start "" "%PROJECT%\bin\Debug\net10.0-windows10.0.19041.0\FlyShelf.exe"
