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
│   ├── Tools/               # Инструменты (Builtins/: ShellTool, ReadFile, и др.)
│   ├── Mcp/                 # MCP-клиент (McpService, McpClient)
│   ├── Subagents/           # Субагенты (SubagentSpawner)
│   ├── Main/                # Композиционный корень (Main, ChatRuntime)
│   ├── Serialization/       # Атомарная запись (AtomicFile), JSON (PythonStyleJson)
│   ├── Sessions/            # Сессии (SessionController, SessionStore, MessageMetaStore, ContextBackupStore)
│   ├── Settings/            # Настройки (AppSettings, SettingsStore<T>, GenerationOptionsExtensions)
│   ├── Templates/           # Шаблон Qwen (QwenChatTemplate, QwenOutputParser, и др.)
│   └── Tokenizers/          # Декодирование токенов (QwenVocabDecoder)
└── QwenPlayground.App/      # WPF-приложение
    ├── Browser/             # Встроенный браузер (BrowserService, BrowserTools)
    ├── Desktop/             # Инструменты рабочего стола (DesktopService, DesktopTools)
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
├── QwenPlayground.App.Tests/    # Тесты приложения
└── QwenPlayground.Launcher.Tests/ # Тесты лаунчера
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
       [ToolParameter("Описание параметра", Required = true)]
       public string Param1 { get; set; } = string.Empty;
       
       [ToolParameter("Описание параметра")]
       public int? Param2 { get; set; }
       
       public override async Task<string> ExecuteAsync(ToolContext context, CancellationToken ct)
       {
           // Логика инструмента
           return "Result";
       }
   }
   ```
   Имя параметра — имя свойства; другое имя — `[ToolParameter("Описание", Name = "custom_name")]`.

2. Инструмент будет автоматически зарегистрирован через рефлексию `[Tool]`.

3. Если инструмент нужен только в определённом контексте — укажите полку третьим аргументом `[Tool]`: `[Tool("my_tool", "Описание", ToolGroup.CSharp)]`. Полки: `Core` (по умолчанию, всегда в промпте), `Browser`, `CSharp`, `Desktop`, `Mcp`, `Intuition`.

4. Напишите тесты в `tests/QwenPlayground.Core.Tests/` или `tests/QwenPlayground.App.Tests/`.

## Как добавить новую настройку

1. Добавьте поле в `src/QwenPlayground.Core/Settings/SettingsStore.cs`:
   ```csharp
   /// <summary>Описание настройки.</summary>
   public int MyNewSetting { get; set; } = 42;
   ```

2. Если нужно в UI — добавьте «тонкий вид» в `SettingsViewModel` (источник правды — синглтон `AppSettings`; свойство — тонкий вид: чтение напрямую, запись = мутация + INPC + отложенный Save; `RefreshAll()` подхватит новое свойство рефлексией):
   ```csharp
   public int MyNewSetting {
       get => S.MyNewSetting;
       set => Set(S.MyNewSetting, value, (s, v) => s.MyNewSetting = v);
   }
   ```

3. Добавьте поле в XAML (`SettingsView.xaml`).

4. Читайте настройку в точке использования: `AppSettings.Get().MyNewSetting`.

## Как добавить новый сервис

1. Создайте класс, реализующий `IAppService`:
   ```csharp
   public sealed class MyService : IAppService
   {
       public string Name => "MyService";
       public void Start() { /* ... */ }
       public void Shutdown() { /* ... */ }
   }
   ```

2. Зарегистрируйте в конструкторе `MainViewModel` (через `Main.Lifecycle`; для лямбд есть `DelegateAppService` — отдельный класс не нужен):
   ```csharp
   _main.Lifecycle.Register(new MyService());
   // или без класса:
   _main.Lifecycle.Register(new DelegateAppService("my service", start: () => { }, shutdown: () => { }));
   ```

3. Остановка LIFO: сервисы останавливаются в обратном порядке регистрации.

## Тесты

### Запуск

```bash
dotnet test tests/QwenPlayground.Core.Tests -c Release
dotnet test tests/QwenPlayground.App.Tests -c Release
dotnet test tests/QwenPlayground.Launcher.Tests -c Release
```

### Правила

- Тестируйте чистую логику (Core) — без UI, без сети
- Для LLM-вызовов используйте фейки/мок'и (см. `ContextMaintenanceTests`)
- Интеграционные тесты (Roslyn, живые серверы) помечайте `[Trait("Category", "Live")]`
- После любого изменения — прогоните тесты

### Структура тестов

Тесты — плоские файлы в корне тестового проекта (без подпапок), названы по тестируемому классу:

```
tests/
├── QwenPlayground.Core.Tests/      # AgentLoopTests.cs, ChatLogTests.cs, MemoryStoreTests.cs,
│                                   # QwenChatTemplateTests.cs, ContextMaintenanceTests.cs, ...
└── QwenPlayground.App.Tests/       # MessageViewModelStreamingTests.cs, TurnPanelTests.cs, ...
```

## Самосборка

`rebuild_self` — это инструмент, который:
1. Проверяет страховку: GUI-лаунчер запущен (ровно один инстанс) и watchdog жив — без этого ребилд НЕ выполняется (гарантия рестарта — внешний лаунчер: handshake + rollback на старую версию)
2. Снимает снапшот дерева в git (см. «Важно» ниже)
3. Запускает pre-check (валидация XAML-XML + Roslyn-диагностика C#)
4. Запускает полный `dotnet build` (сборка идёт прямо в `run/<id>/`)
5. Запускает тест-гейт (`dotnet test`, при падении один flaky-ретрай)
6. Рестартует приложение через лаунчер (лаунчер обновляет `run/current.txt`)

**Важно:**
- Перед каждым ребилдом дерево снимается в git-снапшот (`refs/snapshots/<ts>`, хранятся последние 10) — точка возврата при неудачной сборке
- Каталог `run/` не трогать руками
- Если сборка падает — агент видит ошибки и может их исправить
- По умолчанию не пушит в git (только если включено `PushOnRebuild`)

## Документация

- `README.md` — быстрый старт
- `docs/CONCEPTS.md` — ключевые концепции
- `docs/LLM-SETUP.md` — настройка LLM-сервера
- `ARCHITECTURE.md` — подробная архитектура
- `refactoring.md` — рабочий журнал проекта (локальный файл, не в git)

## Лицензия

MIT — см. [LICENSE](LICENSE).
