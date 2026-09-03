# QwenPlayground

AI-компаньон для разработки: автономный агент, который работает над вашим проектом, модифицирует собственный код и пересобирает себя.

## Возможности

- **Агентный цикл** — Qwen-модель (через llama.cpp) с инструментами: файлы, shell, Roslyn, браузер, память
- **Самомодификация** — `rebuild_self`: агент правит свой код, пересобирает и рестартует себя
- **Долговременная память** — факты, ассоциативный реколл, heartbeat-процессы
- **Мультимодальность** — скриншоты, изображения, диагностика UI
- **Roslyn-инструменты** — outline, references, callers, diagnostics без полной сборки
- **Встроенный браузер** — WebView2 для веб-интеракций
- **Оркестрация** — изолированные агенты с собственными скоупами

## Структура

```
QwenPlayground/
├── src/
│   ├── QwenPlayground.Core/    # Ядро: агент, инструменты, память, Roslyn, self-build
│   └── QwenPlayground.App/     # WPF-приложение: UI, чат, настройки, браузер
├── tools/
│   ├── QwenPlayground.Launcher/ # Лаунчер: запуск, сборка, GitHub sync, инструменты
│   └── QwenPlayground.Harness/  # Тестовый harness (headless-агент)
├── tests/
│   ├── QwenPlayground.Core.Tests/
│   └── QwenPlayground.App.Tests/
├── run/                        # Развёрнутые версии приложения
├── launcher/                   # Собранный лаунчер
├── sessions/                   # Сессии агента (диалоги, артефакты)
├── memories/                   # Долговременная память
└── settings.json               # Настройки приложения
```

## Требования

- **.NET 10 SDK** (10.0.400+)
- **Windows 10/11** (WPF, WebView2)
- **llama.cpp** сервер (OpenAI-совместимый API)
- **Git** (для самосборки и GitHub sync)
- **ffmpeg** (опционально, для мультимодальных задач)

## Быстрый старт

### 1. Клонировать

```bash
git clone https://github.com/Krokozor/QwenPlayground.git
cd QwenPlayground
```

### 2. Настроить

Отредактируйте `settings.json`:

```json
{
  "Endpoint": "http://127.0.0.1:5001",
  "MaxTokens": 2048,
  "ContextSize": 32768
}
```

`Endpoint` — адрес llama.cpp сервера (OpenAI-compatible).

### 3. Bootstrap (одноразово)

Запустите `bootstrap.bat` в корне проекта. Он:

- проверяет окружение (.NET 10 SDK, git)
- собирает лаунчер и watchdog в `launcher/`
- собирает первую версию приложения в `run/first` и активирует её (`run/current.txt`)

Повторный запуск безопасен: активная версия не затрагивается, пересобирается только лаунчер.

### 4. Запустить

Двойной клик по `launcher/QwenPlayground.Launcher.exe`. В лаунчере:

- **Запустить** — старт активной версии приложения
- **Пересобрать** — сборка + тест-гейт + деплой новой версии в `run/<id>`
- **Pull** — git pull из GitHub
- **Скачать** (ffmpeg) — опционально, для мультимодальных задач

После bootstrap эстафета у лаунчера: все последующие сборки/обновления делаются из его GUI.

## Лаунчер

Лаунчер — точка организации проекта:

- **Запуск** активной версии приложения
- **Пересборка** (build + test gate + deploy)
- **GitHub sync** (pull, статус)
- **Инструменты** (ffmpeg: установка, проверка обновлений)
- **Окружение** (dotnet, git, llama.cpp)
- **Настройки** (пути, репозиторий, доп. папки)

Конфиг: `launcher.json` в корне проекта.

## Архитектура путей

```
launcher.json → QWENPLAYGROUND_ROOT (env) → SelfBuildPaths.WorkspaceRoot
                                                        ↓
                                              все инструменты (Roslyn, rebuild)
                                                        ↓
                                              AdditionalWorkspaces (внешние папки)
```

Лаунчер ставит `QWENPLAYGROUND_ROOT` при запуске приложения. Приложение всегда знает где живёт (self-modification), но может работать и с другими папками.

## Тесты

```bash
dotnet test tests/QwenPlayground.Core.Tests -c Release
dotnet test tests/QwenPlayground.App.Tests -c Release
```

## Лицензия

MIT — см. [LICENSE](LICENSE).
