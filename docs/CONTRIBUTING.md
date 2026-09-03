# Вклад в проект

Спасибо за интерес к QwenPlayground! Это руководство объяснит, как устроен код и как вносить изменения.

## Структура кода

```
src/
├── QwenPlayground.Core/     # Ядро без UI
│   ├── Agent/               # Агентный цикл (AgentLoop, AgentEvent, AgentLoopRequest)
│   ├── Chat/                # Модели чата (ChatMessage, ChatLog, ChatStateMachine, ToolCall)
│   ├── Compaction/          # Компакция контекста (ContextCompactor, MemoryLayerPipeline)
│   ├── Crash/               # Диагностика крашей (CrashLogCore, WatchdogMonitor)
│   ├── Heartbeat/           # Wake-сигналы (WakeSignalStore)
│   ├── Inference/           # LLM-клиент (LlmCompletionClient, ICompletionSource, ServerProps)
│   ├── Memory/              # Память (MemoryStore, MemoryClassifier, MemoryRecall, и др.)
│   ├── MetaInfo/            # State-блок (StateBlock)
│   ├── Probes/              # Логит-пробы (LlmProbeClient)
│   ├── Roslyn/              # Roslyn-инструменты (CSharp*Tool, RoslynService)
│   ├── Runtime/             # Профили чата (ChatProfiles, SamplerProfile, и др.)
│   ├── SelfBuild/           # Самосборка (SelfBuildService, RebuildSelfTool, SelfBuildPaths)
│   ├── Serialization/       # Атомарная запись (AtomicFile), JSON (PythonStyleJson)
│   ├── Sessions/            # Сессии (SessionStore, MessageMetaStore, ContextBackupStore)
│   ├── Settings/            # Настройки (AppSettings, SettingsStore<T>, GenerationOptionsExtensions)
│   └── Templates/           # Шаблон Qwen (QwenChatTemplate, QwenOutputParser, и др.)
└── QwenPlayground.App/      # WPF-приложение
    ├── Browser/             # Встроенный браузер (BrowserService, BrowserTools)
    ├── Tools/               # UI-инструменты (ScreenshotTool, AppWindowTool, и др.)
    ├── ViewModels/          # ViewModel'и (MainViewModel, MemoryViewModel, и др.)
    ├── Views/               # XAML-виды (ChatView, SettingsView, и др.)
    └── Themes/              # Тёмная тема (Dark.xaml)

tools/
├── QwenPlayground.Launcher/ # Лаунчер (запуск, сборка, GitHub sync)
├── QwenPlayground.Watchdog/ # Страж процесса (нативные краши)
└── QwenPlayground.Harness/  # Тестовый harness (headless-агент)

tests/
├── QwenPlayground.Core.Tests/   # Тесты ядра
└── QwenPlayground.App.Tests/    # Тесты приложения
```

## Правила кода

### 1. Класс = файл

Если класс просит собственный файл или разделение — не лениться. Один класс на файл (кроме мелких record'ов и helper'ов).

### 2. Комментировать несамоочевидное

Шапка файла или комментарии в функциях. Мысли о развитии проекта сохранять в `refactoring.md` или в коде.

### 3. Расширяемость > микрооптимизации

Не разменивать гибкость архитектуры на экономию тактов. Если оптимизация усложняет код — не делать.

### 4. Не коммитить без явной просьбы

Коммиты и пуши делает владелец или агент явно. `rebuild_self` не пушит по умолчанию (только если включено `PushOnRebuild`).

## Как добавить новый инструмент

1. Создайте класс в `src/QwenPlayground.Core/Tools/` или `src/QwenPlayground.App/Tools/`:
   ```csharp
   [Tool("my_tool", "Описание инструмента")]
   public sealed class MyTool : AgentTool
   {
       [ToolParameter("param1", "Описание параметра")]
       public string Param1 { get; set; }
       
       [ToolParameter("param2", "Описание параметра", Required = false)]
       public int? Param2 { get; set; }
       
       public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
       {
           // Логика инструмента
           return "Result";
       }
   }
   ```

2. Инструмент будет автоматически зарегистрирован через рефлексию `[Tool]`.

3. Если инструмент нужен только в определённом контексте — добавьте `[ToolGroup(ToolGroup.CSharp)]` или `[ToolGroup(ToolGroup.Browser)]`.

4. Напишите тесты в `tests/QwenPlayground.Core.Tests/` или `tests/QwenPlayground.App.Tests/`.

## Как добавить новую настройку

1. Добавьте поле в `src/QwenPlayground.Core/Settings/SettingsStore.cs`:
   ```csharp
   /// <summary>Описание настройки.</summary>
   public int MyNewSetting { get; set; } = 42;
   ```

2. Если нужно в UI — добавьте observable-свойство в `MainViewModel`:
   ```csharp
   [ObservableProperty]
   private int myNewSetting = AppSettings.Get().MyNewSetting;
   
   partial void OnMyNewSettingChanged(int value)
   {
       AppSettings.Get().MyNewSetting = value;
       ScheduleSettingsSave();
   }
   ```

3. Добавьте поле в XAML (`SettingsView.xaml`).

4. Читайте настройку в точке использования: `AppSettings.Get().MyNewSetting`.

## Как добавить новый сервис

1. Создайте класс, реализующий `IAppService`:
   ```csharp
   public sealed class MyService : IAppService
   {
       public void Start() { /* ... */ }
       public void Shutdown() { /* ... */ }
   }
   ```

2. Зарегистрируйте в `App.xaml.cs`:
   ```csharp
   _lifecycle.Register(new MyService());
   ```

3. Остановка LIFO: сервисы останавливаются в обратном порядке регистрации.

## Тесты

### Запуск

```bash
dotnet test tests/QwenPlayground.Core.Tests -c Release
dotnet test tests/QwenPlayground.App.Tests -c Release
```

### Правила

- Тестируйте чистую логику (Core) — без UI, без сети
- Для LLM-вызовов используйте фейки/мок'и (см. `ContextMaintenanceTests`)
- Интеграционные тесты (Roslyn, живые серверы) помечайте `[Trait("Category", "Live")]`
- После любого изменения — прогоните тесты

### Структура тестов

```
tests/
├── QwenPlayground.Core.Tests/
│   ├── Agent/           # Тесты агентного цикла
│   ├── Chat/            # Тесты моделей чата
│   ├── Memory/          # Тесты памяти
│   ├── Templates/       # Тесты шаблона Qwen
│   └── ...
└── QwenPlayground.App.Tests/
    ├── ViewModels/      # Тесты ViewModel'ей
    └── ...
```

## Самосборка

`rebuild_self` — это инструмент, который:
1. Запускает `dotnet build` (с Roslyn-гейтом)
2. Запускает тесты
3. Копирует сборку в `run/<id>/`
4. Обновляет `run/current.txt`
5. Рестартует приложение через лаунчер

**Важно:**
- Каталог `run/` не трогать руками
- Если сборка падает — агент видит ошибки и может их исправить
- По умолчанию не пушит в git (только если включено `PushOnRebuild`)

## Документация

- `README.md` — быстрый старт
- `docs/CONCEPTS.md` — ключевые концепции
- `docs/LLM-SETUP.md` — настройка LLM-сервера
- `ARCHITECTURE.md` — подробная архитектура
- `refactoring.md` — рабочий журнал проекта

## Лицензия

MIT — см. [LICENSE](LICENSE).
