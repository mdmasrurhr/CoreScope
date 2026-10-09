@echo off
setlocal
cd /d "%~dp0"
title CoreScope build

echo.
echo  ==============================
echo    CoreScope - build ^& launch
echo  ==============================
echo.

where dotnet >nul 2>nul
if errorlevel 1 goto :nosdk
dotnet --list-sdks | findstr /b /c:"10." >nul
if errorlevel 1 goto :nosdk

tasklist /fi "imagename eq CoreScope.exe" | find /i "CoreScope.exe" >nul
if not errorlevel 1 (
    echo  CoreScope is still running. Please close it first, then press any key.
    pause >nul
)

echo  Building... first build downloads packages and takes about a minute.
dotnet publish "src\CoreScope\CoreScope.csproj" -c Release -r win-x64 --self-contained false -o app -nologo > build.log 2>&1
if errorlevel 1 goto :failed

echo  Build succeeded. Starting CoreScope (Windows will ask for administrator permission)...
start "" "%~dp0app\CoreScope.exe"
exit /b 0

:failed
echo.
echo  BUILD FAILED. The errors are below and in build.log:
echo.
findstr /i /c:"error" build.log
echo.
echo  Tell Claude "the build failed" - it can read build.log directly.
pause
exit /b 1

:nosdk
echo  The .NET 10 SDK is not installed (it's free, about 200 MB).
echo.
echo  Option 1: run this in a terminal:   winget install Microsoft.DotNet.SDK.10
echo  Option 2: download it from the page that is opening now.
echo.
echo  After installing, close this window and double-click build.bat again.
start "" "https://dotnet.microsoft.com/download/dotnet/10.0"
pause
exit /b 1
