@echo off
setlocal
echo ===================================================
echo       MP4 Merger Build Tool (Robust Mode)
echo ===================================================
echo.

REM 1. Search for dotnet in PATH
echo [1/3] Searching for dotnet in PATH...
where dotnet >nul 2>nul
if %errorlevel% equ 0 (
    echo Found dotnet in PATH.
    set "DOTNET_CMD=dotnet"
    goto :CHECK_SDK
)

REM 2. Search in standard x64 location
echo Not found in PATH. Checking C:\Program Files\dotnet...
if exist "C:\Program Files\dotnet\dotnet.exe" (
    echo Found x64 dotnet.
    set "DOTNET_CMD="C:\Program Files\dotnet\dotnet.exe""
    goto :CHECK_SDK
)

REM 3. Search in standard x86 location
echo Not found. Checking C:\Program Files (x86)\dotnet...
if exist "C:\Program Files (x86)\dotnet\dotnet.exe" (
    echo Found x86 dotnet.
    set "DOTNET_CMD="C:\Program Files (x86)\dotnet\dotnet.exe""
    goto :CHECK_SDK
)

echo.
echo [ERROR] Could not find 'dotnet.exe' anywhere.
echo Please install the ".NET SDK 8.0 (x64)" from the link below.
echo Link: https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/sdk-8.0.100-windows-x64-installer
pause
exit /b

:CHECK_SDK
echo.
echo [2/3] Checking SDK version...
%DOTNET_CMD% --list-sdks
echo.
echo If the list above is empty, you only have the Runtime installed.
echo You need the *SDK*.

echo.
echo [3/3] Attempting Build (win-x64)...
%DOTNET_CMD% publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./ReleaseBuild

if %errorlevel% neq 0 (
    echo.
    echo [ERROR] Build Failed.
    echo Common causes:
    echo  - You have x86 SDK installed but we are trying to build x64 (Recommend installing x64 SDK).
    echo  - Network issues downloading NuGet packages.
    pause
    exit /b
)

echo.
echo ===================================================
echo   SUCCESS!
echo   App created in: %~dp0ReleaseBuild
echo ===================================================
echo.
pause
