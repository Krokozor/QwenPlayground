@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

echo === QwenPlayground: bootstrap ===
echo.

REM ---------- 1. Окружение ----------
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ОШИБКА] dotnet SDK не найден в PATH.
    echo Установите .NET 10 SDK (10.0.400+): https://dotnet.microsoft.com/download
    pause
    exit /b 1
)
for /f "delims=" %%v in ('dotnet --version') do set "DOTNET_VER=%%v"
echo dotnet SDK: %DOTNET_VER%
echo %DOTNET_VER% | findstr /r "^10\." >nul
if errorlevel 1 (
    echo [ОШИБКА] Нужен .NET 10 SDK (10.0.400+), найден: %DOTNET_VER%
    pause
    exit /b 1
)

where git >nul 2>nul
if errorlevel 1 (
    echo [ВНИМАНИЕ] git не найден в PATH — GitHub sync в лаунчере будет недоступен.
)

REM ---------- 2. Лаунчер + watchdog ----------
echo.
echo Сборка лаунчера и watchdog'а в launcher/ ...
dotnet build "tools\QwenPlayground.Launcher\QwenPlayground.Launcher.csproj" -c Release -o "launcher"
if errorlevel 1 (
    echo [ОШИБКА] Не удалось собрать лаунчер.
    pause
    exit /b 1
)
dotnet build "tools\QwenPlayground.Watchdog\QwenPlayground.Watchdog.csproj" -c Release -o "launcher"
if errorlevel 1 (
    echo [ОШИБКА] Не удалось собрать watchdog.
    pause
    exit /b 1
)

REM ---------- 3. Первая версия приложения ----------
if not exist "run\current.txt" (
    echo.
    echo Сборка первой версии приложения в run\first ...
    dotnet build "src\QwenPlayground.App\QwenPlayground.App.csproj" -c Release -o "run\first"
    if errorlevel 1 (
        echo [ОШИБКА] Не удалось собрать приложение.
        pause
        exit /b 1
    )
    echo first> "run\current.txt"
    echo Активирована версия: first
) else (
    echo.
    echo run\current.txt уже существует — активная версия не трогается.
)

echo.
echo === Готово: эстафета передана лаунчеру ===
echo.
echo Дальше:
echo   1. Запустите launcher\QwenPlayground.Launcher.exe
echo   2. В лаунчере: «Запустить» — старт приложения
echo      «Пересобрать» — сборка + тест-гейт + новая версия
echo      «Скачать» (ffmpeg) — опционально, для мультимодальных задач
echo.
pause
