@echo off
setlocal
cd /d "%~dp0"

echo === QwenPlayground: bootstrap ===
echo.

REM ---------- 1. Environment ----------
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet SDK not found in PATH.
    echo Install .NET 10 SDK (10.0.400+): https://dotnet.microsoft.com/download
    pause
    exit /b 1
)
for /f "delims=" %%v in ('dotnet --version') do set "DOTNET_VER=%%v"
echo dotnet SDK: %DOTNET_VER%
echo %DOTNET_VER% | findstr /r "^10\." >nul
if errorlevel 1 (
    echo [ERROR] .NET 10 SDK (10.0.400+) is required, found: %DOTNET_VER%
    pause
    exit /b 1
)

where git >nul 2>nul
if errorlevel 1 (
    echo [WARNING] git not found in PATH - GitHub sync in the launcher will be unavailable.
)

REM ---------- 2. Launcher + watchdog ----------
echo.
echo Building launcher and watchdog into launcher/ ...
dotnet build "tools\QwenPlayground.Launcher\QwenPlayground.Launcher.csproj" -c Release -o "launcher"
if errorlevel 1 (
    echo [ERROR] Launcher build failed.
    pause
    exit /b 1
)
dotnet build "tools\QwenPlayground.Watchdog\QwenPlayground.Watchdog.csproj" -c Release -o "launcher"
if errorlevel 1 (
    echo [ERROR] Watchdog build failed.
    pause
    exit /b 1
)

REM ---------- 3. First app version ----------
if not exist "run\current.txt" (
    echo.
    echo Building first app version into run\first ...
    dotnet build "src\QwenPlayground.App\QwenPlayground.App.csproj" -c Release -o "run\first"
    if errorlevel 1 (
        echo [ERROR] App build failed.
        pause
        exit /b 1
    )
    echo first> "run\current.txt"
    echo Active version: first
) else (
    echo.
    echo run\current.txt already exists - active version left untouched.
)

echo.
echo === Done: baton passed to the launcher ===
echo.
echo Next:
echo   1. Run launcher\QwenPlayground.Launcher.exe
echo   2. In the launcher: Start - launch the app
echo      Rebuild - build + test gate + new version
echo      Download (ffmpeg) - optional, for multimodal tasks
echo.
pause
