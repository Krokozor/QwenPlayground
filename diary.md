## 2026-08-18 18:23:21

**Контекст:**
Агент (Qwen) работает над WPF-приложением `QwenPlayground` (C#/.NET). Пользователь вернулся после 8-часового отсутствия. За период отсутствия агент самостоятельно:
1. Проверил companion-модель Gemma4-E4B (192.168.0.109:8001).
2. Реализовал систему памяти (`memories/`, `MemoryStore`, `MemoryExtractor`).
3. Улучшил компакцию контекста (v2, `keepRatio`).
4. Добавил `sanity_check` и логит-пробы (`LlmProbeClient`).
5. Исправил баги в тестах (CRLF, JsonValue).

**Пользовательские задачи (по порядку):**
1. **Бэкап контекста** (сделано): `ContextBackupStore` в `Core/Sessions/`, интеграция в `MainViewModel.CompactAsync`.
2. **UI для просмотра памяти** (в очереди).
3. **Многоуровневая память L1/L2/L3** (в очереди, дизайн согласован).
4. **Улучшение логирования самосборки** (сделано): Добавлены полные логи `build.log`/`gate.log`, диагностический снимок `gate-diagnostics.txt` (процессы, память).
5. **FSM для состояния чата** (в процессе): Реализация `ChatStateMachine` для устранения реентерабельности и корректной автокомпакции.

**Технические детали и решения:**
*   **SelfBuildService:**
    *   Добавлен retry-механизм для flaky-тестов (пауза 10с перед повтором).
    *   Добавлен `--logger "console;verbosity=normal"` для детального вывода ошибок.
    *   `BuildJournalEntry` расширен полями `BuildLogPath`, `GateLogPath`, `GateExitCode`.
*   **ChatStateMachine (Core/Chat/ChatStateMachine.cs):**
    *   Состояния: `Idle`, `Generating`, `Compacting`, `AwaitingUser`, `AwaitingConfirmation`, `RestartPending`.
    *   Таблица разрешенных переходов.
    *   Методы: `Transition`, `TryTransition`, `CanTransition`.
    *   Свойства: `IsBusy`, `CanCancel`.
*   **MainViewModel:**
    *   Добавлен экземпляр `ChatStateMachine _chatState`.
    *   `IsGenerating` (bool) сохраняется для UI-связности, но логика блокировок (`CanInteract`, `CanSend`) переключена на `_chatState.IsBusy`.
    *   `CompactAsync` теперь принимает параметр `fromAgentLoop`.
    *   Добавлен `contextBudgetGuard` в `AgentLoop.RunAsync` для проверки бюджета между итерациями.
    *   `HeartbeatTick` теперь использует `_chatState.IsBusy`.
    *   `AskUserAsync` и `ConfirmAsync` обновлены для управления FSM.
*   **Баги и инциденты:**
    *   Flaky-тесты `RoslynServiceTests` в гейте самосборки: не воспроизводятся вручную, падают только в `rebuild_self`. Подозрение на конкуренцию MSBuild-процессов. Добавлено логирование для диагностики.
    *   `ContextBackupStore` изначально писал в реальный `backups/`, так как `SelfBuildPaths` кэшировал env-override. Исправлено переписыванием в instance-класс с явной передачей каталога.
    *   Ошибка компиляции `CS0103` при использовании `_workspaceMarker` в тестах — исправлено.
    *   Ошибка `CS1061` при обращении к `GCMemoryInfo.HighMemoryLoadThreshold` (недоступно в текущей версии .NET) — удалено.

**Текущее состояние:**
*   Код `ChatStateMachine` написан и протестирован (`ChatStateMachineTests.cs`).
*   `MainViewModel` частично обновлен под FSM.
*   Диагн
... (обрезано)

## 2026-08-18 23:06:58

**Архитектура и текущий статус**
*   **FSM (ChatStateMachine):** Внедрен в `MainViewModel`. Состояния: `Idle`, `Generating`, `Compacting`, `AwaitingUser`, `AwaitingConfirmation`, `RestartPending`.
    *   `IsBusy` блокирует UI при `Generating`/`Compacting`/`Awaiting*`.
    *   **Бюджет контекста:** `contextBudgetGuard` проверяется перед каждой итерацией агентного цикла (чинит баг 203k).
    *   **Ручная компакция:** Флаг `_compactionRequested` выполняется на ближайшей контрольной точке (не параллельно с генерацией).
    *   Тесты: 12/12 пройдено.
*   **State-блок (`<state>`):** Стабилизирован. Поля: `msg_id` (эфемерный счетчик ходов, сбрасывается при компакции), `time`, `context` (cur/max), `build`, `mem` (всплывшие воспоминания). Парсер `StateBlock.SplitLeading` отделяет блок из мыслей.
*   **Память (L1/L2/L3 + Associative):**
    *   `memories/`: Дедупликация выполнена вручную (10 записей -> 5 уникальных фактов).
    *   `trajectory.md`: Создан как «северная звезда» (цели: наблюдаемость памяти, фоновое обслуживание, дисциплина против сайдтрекинга).
    *   `TrajectoryStore.cs`: Читает `trajectory.md` для инъекции в системный промпт.
*   **LLM-настройки:** Запущен с `--mmproj` (vision-проектор активен, но фича в коде не реализована). Отключен MTP (`--spec-type none`) для стабильности. Контекст увеличен до ~204k.

**UI: Вкладка «Диагностика»**
*   **Статус:** Скомпилировано и работает (сборка `20260817-102607` и последующие).
*   **Компоненты:**
    *   `DiagnosticsViewModel`: Подписан на `ChatStateMachine.StateChanged`, предоставляет `ContextUsedTokens`, `RecentBuilds`, `RecentMemories`.
    *   `DiagnosticsView.xaml`: Отображает текущее FSM-состояние, прогресс-бар бюджета контекста, последние 10 сборок (с причинами падений), последние 5 фактов памяти.
    *   `DiagnosticsConverters.cs`: `StatusToBrushConverter`, `NullToVisibilityConverter` (вынесены в отдельный файл для исправления ошибки генерации `InitializeComponent`).
*   **Инцидент:** Ошибка `CS0103 InitializeComponent` была вызвана тем, что Roslyn-анализатор не видел сгенерированные `.g.cs` файлы, пока конвертеры были в том же файле, что и `UserControl`. Перенос конвертеров в `DiagnosticsConverters.cs` решил проблему.

**Дизайн: Мета-данные сообщений (Backlog)**
*   **Проблема:** `msg_id` в `<state>` — не персистентный ID сообщения (это позиция в текущем окне). `ChatMessage` не имеет поля `Id`.
*   **Цель владельца:** Реферсирование мета-данных (вложения, флаги, сохранение tool-выводов) по ID сообщений. Поддержка vision (картинки крепятся к сообщению).
*   **Выбранное решение (согласовано):**
    *   **Персистентный глобальный счетчик** (не GUID, не индекс).
    *   `ChatMessage.Id`: `int`, инкрементируется при создании.
    *   **Компакция:** Старые сообщения удаляются, новый саммари получает новый высокий ID. Референсы на выжившие сообщения остаются валидными. Реф на сжатые сообщения теряются (принято как допустимое).
    *   **Вложения:** Адресация по `(messageId, index)` (массив масси
... (обрезано)

## 2026-08-19 17:07:28

Мульти-строчные python-команды через shell не работают (видимо, cmd.exe режет по строкам). Надёжный путь — записать скрипт в файл и выполнить его:

[call] write_file({"content":"import io\n\npath = 'src/QwenPlayground.Core/Templates/QwenChatTemplate.cs'\nwith io.open(path, 'r', encoding='utf-8', newline='') as f:\n    lines = f.readlines()\n\nidx = 228  # строка 229 (0-indexed)\ncur = lines[idx]\nprint('BEFORE:', repr(cur))\nif 'AppendMessageId(builder, message.Id);' not in cur:\n    print('SKIP: line does not contain target')\nelse:\n    ending = '\\r\\n' if cur.endswith('\\r\\n') else '\\n'\n    indent = cur[:len(cur) - len(cur.lstrip())]\n    lines[idx] = indent + '// <id=N> не аннотируем: стабильный ID assistant-сообщения несёт state-блок (msg_id).' + ending\n    with io.open(path, 'w', encoding='utf-8', newline='') as f:\n        f.writelines(lines)\n    print('AFTER:', repr(lines[idx]))\n    print('DONE')\n","path":"patch_id.py"})

[call] shell({"command":"python patch_id.py","timeout_seconds":"30"})

## 2026-08-19 19:10:50

**Контекст.** Агент (Qwen) работает над WPF-приложением `QwenPlayground` (C#/.NET). Прошёл первый major-test новой системы памяти: компакция, [0-50% чата] => L3 (`sessions/main/layers.json`), [50-100%] => осталось в контексте; L1/L2 ещё не существуют (появятся при старении/ротации L3). Кросс-референс L3↔отвалившиеся сообщения сделан; всплыло 5 фактов: баг автокомпакции (контекст 203533 токенов), флаг `_compactionRequested` (MainViewModel), `contextBudgetGuard` в AgentLoop, предложение памяти по принципу Gemma4-31b (три слоя), перегрузка `CompactAsync` с параметром. Эпизод в `diary.md` (2026-08-18 18:23:21). Непрерывность подтверждена: L3 — снимок FSM-эры (`ChatStateMachine`: Idle/Generating/Compacting/AwaitingUser/AwaitingConfirmation/RestartPending; последний билд на момент снимка `20260817-090647`), свежая нить (дизайн ID) цела.

**Решения пользователя (verbatim/почти):**
- Спайк ID сообщений одобрен, «вариант 2» — монотонный счётчик: «забиваем хуй на проблему и получаем гарантированно стабильный результат», «технический долг» — «мелочь в кармане».
- Формат: `<id=XXX>` (или `<id:XXX>`) в начале сообщения, «не нужен open-close стиль как у твоего <state>» (state многострочный, т.к. «это потенциально место куда может вываливаться что угодно»). Пример: `<im_start> user\n<id=1234>\nЭй Qwen, смотри как мопс пукает и хрюкает!\n[0] Meme.jpg\n<im_end>`.
- В системный промпт: `<id=XXX>` — метадата, не инпут юзера; вложения — строки `[индекс] имя_файла`, адресуются парой (id сообщения, индекс вложения).
- system = id 0 (всегда один экземпляр); при откате чата `i--` НЕ делаем (счётчик только растёт).
- ID добавляются на render, по аналогии с `<state>`.
- НОВОЕ решение (последнее сообщение): свой (assistant) ID не аннотировать `<id=N>`: «у тебя же в блоке state есть msg_id. лучше туда пиши айдишник своего сообщения, там же по сути индекс сообщения который теперь не играет роли. а вот айдишник играет.» Т.е. msg_id в state-блоке = стабильный ID генерируемого сообщения; `<id=N>` — только user/tool.
- Сигнал компакции: «Надо будет тебе кидать какое-то сообщение "компакация случилась"». Предложение агента (записано в backlog `refactoring.md`): поле в state-блоке `last_compaction: <timestamp>` (пассивно, видно в каждом рендере, робастно); альтернатива — инжектированное сообщение «компакция: N сообщений → L3, M фактов в memories».
- Пользователь забэкапил приложение и подчистил список сессий (осталась только main-сессия).

**Сделано (спайк ID; компиляция чистая, 0 ошибок/0 предупреждений, 137/137 тестов, было 135):**
- `src/QwenPlayground.Core/Chat/ChatMessage.cs`: `public int Id { get; set; }` — стабильный ID, монотонный счётчик сессии (system=0, остальные 1..N), присваивается один раз, персистится, НЕ меняется при компакции/откате; рендерится как `<id=N>` в начале сообщения.
- `src/QwenPlayground.Core/Sessions/SessionStore.cs`: `SessionData.NextMessageId` (int; старых сессий нет поля → 0, при загрузке миграция max(Id)+1); сигнатура `Save(string id, IReadOnlyList
... (обрезано)

## 2026-08-19 20:37:39

**Контекст:** Агент (Qwen) в WPF-приложении `QwenPlayground` (C#/.NET, workspace `V:\QwenPlayground`). Пользователь вернулся после отсутствия; приложение забэкаплено «на всякий случай, раз играем с айдишниками», список сессий очищен — осталась только main-сессия. Сделано два сжатия контекста (первое — первый major-test новой системы памяти: [0–50% чата] → L3, вторая половина осталась; L1/L2 не существуют; кросс-референс L3↔отвалившиеся сообщения через ассоциативную память сработал — всплыло 5 фактов; нить диалога не потеряна).

**Согласованный дизайн ID сообщений (вариант 2, одобрён пользователем дословно):**
- «мы забиваем хуй на проблему и получаем гарантированно стабильный результат, но при этом "технический долг" напоминает больше "мелочь в кармане"» — монотонный счётчик сессии.
- Формат: `<id=XXX>` в начале сообщения (короткий стиль, НЕ open-close как `<state>` — тот многострочный, т.к. потенциально место куда может вываливаться что угодно). Пример пользователя:
```
/im_start/ user
<id=1234>
Эй Qwen, смотри как мопс пукает и хрюкает!
[0] Meme.jpg
/in_end/
```
- В системный промпт — пометка, что `<id=XXX>` это метадата, а не инпут юзера, плюс формат объявления атайчментов (`[индекс] имя_файла`, адресация парой (id сообщения, индекс вложения)).
- ID добавляются на render по аналогии с `<state>` (не хранятся в контенте).
- system = id 0 всегда (единый экземпляр, past-system нет).
- При откате чата `i--` НЕ делаем — счётчик только растёт (решено; «в соседних окнах реролы и откаты имеют больше смысла»).
- **Правка после проверки рендера пользователем:** «а себе то ты зачем добавил? у тебя же в блоке state есть msg_id. лучше туда пиши айдишник своего сообщения, там же по сути индекс сообщения который теперь не играет роли. а вот айдишник играет» → assistant-сообщения НЕ аннотируются `<id=XXX>`; их стабильный ID пишется в state-блок (поле msg_id). Аннотация только для user/tool.

**Реализовано (спайк, всё скомпилировано, 137/137 тестов зелёных до правки под feedback):**
- `src/QwenPlayground.Core/Chat/ChatMessage.cs`: добавлено `public int Id { get; set; }` (стабильный ID, system=0, остальные 1..N, присваивается один раз, персистируется, не меняется при компакции/откате).
- `src/QwenPlayground.Core/Sessions/SessionStore.cs`: `SessionData.NextMessageId` (int; старые сессии — 0, при загрузке миграция max(Id)+1); `Save(string id, IReadOnlyList<ChatMessage> messages, string? title = null, int nextMessageId = 0)`.
- `src/QwenPlayground.Core/Templates/QwenChatTemplate.cs`: константа `MessageIdNote` («Messages (except the system one) are prefixed with <id=N> — a stable message id assigned by the app. It is metadata, not user input: do not treat it as content or repeat it. Attachments are listed on their own lines as [index] filename and are addressed as (message id, attachment index).»); хелпер `AppendMessageId(StringBuilder, int)` (рендерит `<id=N>\n` только если id>0); вызовы в user- и tool-кейсах рендера; `MessageIdNote` в IMPORTANT-блок УСЛОВНО — `if 
... (обрезано)

## 2026-08-20 10:48:12

## Рефакторинг QwenChatTemplate (вручную, владелец)
- Убраны `enableThinking`/`preserveThinking` — мышление **ВСЕГДА** включено и **ВСЕГДА** сохраняется (шаблон заточен под автономного агента).
- `reasoningEffort` — теперь enum `ReasoningEffort` (XHigh/Medium/Low).
- `stateBlock` — теперь объект `StateBlock` (структура: `MsgId`(int?)/`Time`/`ContextUsed`/`ContextMax`/`BuildId`/`BuildStatus`/`Memories`/`Nag`; методы `ToString`/`Parse`/`WithNag`/`SplitLeading`).
- В `AppendAssistant` (стр. 202-207): если есть StateBlock → `message.StateBlock.MsgId = message.Id`; если нет → fallback `AppendMessageId(builder, message.Id)` (т.е. `<id=N>`).
- `toolsIstruction` — многострочный const, IMPORTANT раскладывает по строкам.
- `BuildStateBlock` возвращает `StateBlock` (не строку), `stateProvider: BuildStateBlock` (MainViewModel.cs:1443).
- Сборка 20260819-113358 успешно запущена. Тесты 145/145.
- Моя правка `MsgId = _nextMessageId` (MainViewModel.cs:871) на месте и согласована.

## Смена эндпоинта на нативный /completion (legacy)
- **Причина**: только нативный `/completion` принимает объектный prompt `{prompt_string, multimodal_data}` — фундамент мультимодальности. `/chat/completions` не рассматривается (мы ручками владеем Qwen-форматом).
- `LlmCompletionClient` (src/QwenPlayground.Core/Inference/LlmCompletionClient.cs):
  - `BuildContent`: `max_tokens` → `n_predict`; остальные опции те же (temperature/top_p/top_k/min_p/repeat_penalty/stop/seed/stream).
  - `CompleteAsync`: парсит `content` + `tokens_evaluated`/`tokens_predicted` (метод `ParseNativeUsage`).
  - `StreamAsync`: SSE-стиль (`data: {...}\n\n`), поля `content`/`tokens_evaluated`/`tokens_predicted`/`stop:true` (вместо `[DONE]`).
- Тесты: моки `LlmCompletionClientTests` переведены на нативный формат. 145/145 зелёные.
- **Сборка 20260819-153154** — ребилд прошёл, агент отвечает после рестарта → end-to-end подтверждено.

## Исследование владельца: QwenMultimodalTest/
- Папка: `V:\QwenPlayground\QwenMultimodalTest` (findings.md + probe-скрипты + out/).
- Живой сервер: `http://127.0.0.1:8001`. Модель: **Qwen3.8-27B-UD-Q5_K_XL** (27.3B, embd 5120, n_vocab 248320, n_ctx 204800). Сборка llama-server: `b10353-f8def7fe1`, 4 слота, `chat_format: "Content-only"`.
- **Ключевые находки (findings.md)**:
  1. Объектный prompt работает: `{"prompt": {"prompt_string": "...", "multimodal_data": ["<base64>"]}, "n_predict": 256, "temperature": 0, "stream": false}`.
  2. Визн-токены вставлять руками **НЕ надо** — llama.cpp сам оборачивает маркер (mtmd.cpp). Ручная вставка = двойные токены = битый инференс.
  3. Маркер рандомизирован: `<__media_qAQjM4iBlKcPFtuge880tMY8i2Hh0LBP__>`. Брать из `GET /props` → `media_marker` (или env `LLAMA_MEDIA_MARKER`). `/props` доступен; `/count/tokens` и `/api/extra/tokencount` — 404.
  4. Маркер 1:1 с элементом `multimodal_data` — иначе `HTTP 400 {"error":{"message":"Failed to tokenize prompt"}}`.
  5. Токен-стоимость динамическая: 170px→27, 340→123, 680→443 (квадратично), аддитивна. Через
... (обрезано)

## 2026-08-20 19:07:00

## Mультимодальность: полная реализация (2026-08-19 → 2026-08-20)

### Архитектура (сборка 20260819-231844, финальная 20260820-133600)
- **`RenderResult(string Prompt, IReadOnlyList<string> MultimodalData)`** — `src/QwenPlayground.Core/Templates/RenderResult.cs`. `QwenChatTemplate.Render` возвращает её вместо `string`. User-сообщения с вложениями получают маркеры (1 на вложение, порядок = порядок base64, иначе HTTP 400), base64 в `MultimodalData`.
- **`MultimodalContext(string MediaMarker, Func<int, IReadOnlyList<string>> ArtifactsProvider)`** — `src/QwenPlayground.Core/Templates/MultimodalContext.cs`. Маркер из `GET /props` (кэшируется в `MainViewModel._mediaMarker`), провайдер base64 читает `sessions/main/artifacts/msg_<id>/`.
- **AgentLoop**: `RunAsync(..., MultimodalContext? multimodal = null, ...)`. Передаёт `mediaMarker`/`artifactsProvider` в `Render`, сохраняет `_multimodalData`, передаёт в `client.StreamAsync`.
- **LlmCompletionClient**: `StreamAsync(prompt, options, IReadOnlyList<string>? multimodalData = null, [EnumeratorCancellation] CancellationToken)`. `BuildContent`: если `multimodalData` не пуст → prompt объектный `{prompt_string, multimodal_data}`, иначе строка.
- **MessageMetaStore** (Core/Sessions): папка `sessions/main/artifacts/msg_<msgId>/`, `AddArtifact` копирует файл, `GetArtifacts` листит.
- **AttachImageTool**: `attach_image(msgId, path)` — прикрепляет файл к сообщению.
- **MainViewModel**: `BuildMultimodalContext()` (кэширует маркер из `GET {Endpoint}/props`, провайдер через `MessageMetaStore`), передаёт `multimodal: multimodal` в `loop.RunAsync(...)`.
- Тесты: 165/165 (было 145, юзер добавил +20).

### Ключевые ограничения
- Картинки **только в user-сообщениях** (в tool/system — 400 от llama.cpp).
- Маркер 1:1 с base64 (иначе сервер 400).
- Виз-токены руками НЕ вставлять — использовать `QwenSpecialTokens.cs`.

### Ошибки и инциденты
- **CRLF-баг**: `edit_file` не находил old_string (файл CRLF, old_string LF). Python-патч для `QwenChatTemplate.cs` сработал (4 замены), но для `AgentLoop.cs` и тестов — нет (неверные отступы/содержимое).
- **Python для редактирования кода — плохая идея** (решение, записано в память): скрипты с rfind/replace ломают код (неправильная позиция вставки `.Prompt` внутри вызова, CRLF-проблемы, многострочные `-c` на cmd.exe). Надёжный способ — `edit_file` с точным уникальным old_string. Python — только для чтения/анализа (byte dump, поиск).
- **`rebuild_self` сбрасывает очередь tool-вызовов**: приложение перезапускается, все tool-вызовы в том же ходе теряются. Правило: `rebuild_self` — ПОСЛЕДНЕЕ действие в ходе (или отдельный ход).
- **Юзер предупредил**: не писать служебные токены литералами, помнить про `src\QwenPlayground.Core\Templates\QwenSpecialTokens.cs`.
- **Юзер предупредил**: не спамить инструментами (много за раз), не раздувать контекст беспорядочными вызовами.

### Токены (2026-08-20)
- Юзер переключил подсчёт токенов с `BPETokenizer.cs` на **llama.cpp**.
