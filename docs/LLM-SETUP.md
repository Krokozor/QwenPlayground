# Настройка LLM-сервера

QwenPlayground работает с **llama.cpp** через нативный API (не OpenAI-совместимый). Это осознанный выбор: нативный эндпоинт `/completion` принимает объектный prompt с мультимодальными данными, что нужно для работы с изображениями.

## Требования

- **llama.cpp** — сервер инференса (сборка с поддержкой `/completion`, `/tokenize`, `/props`)
- **GGUF-модель** — Qwen3 или совместимая (шаблон Qwen3 в `assets/chat_template.jinja`)
- **Опционально**: vision-проектор (mmproj) для мультимодальности
- **Опционально**: MCP-серверы для внешних инструментов (Blender, HuggingFace, Unity)

## Установка llama.cpp

### Быстрый способ (Windows)

Скачайте готовый бинарник с [GitHub releases](https://github.com/ggml-org/llama.cpp/releases):
- `llama-bXXXX-bin-win-avx2-x64.zip` (или avx512, если ваш CPU поддерживает)
- Распакуйте в удобную папку, например `C:\llama.cpp\`

### Сборка из исходников

```bash
git clone https://github.com/ggml-org/llama.cpp
cd llama.cpp

# CPU-only
cmake -B build
cmake --build build --config Release

# С CUDA (NVIDIA GPU)
cmake -B build -DGGML_CUDA=ON
cmake --build build --config Release

# С Vulkan
cmake -B build -DGGML_VULKAN=ON
cmake --build build --config Release
```

Бинарник будет в `build/bin/Release/llama-server.exe`.

## Запуск сервера

### Минимальный запуск

```bash
llama-server -m path/to/model.gguf --ctx-size 32768 --port 5001
```

### С GPU

```bash
llama-server -m path/to/model.gguf --ctx-size 32768 --port 5001 --gpu-layers 99
```

### С мультимодальностью (vision)

```bash
llama-server -m path/to/model.gguf --mmproj path/to/mmproj.gguf --ctx-size 32768 --port 5001
```

### С KV-кешем (для субагентов)

```bash
llama-server -m path/to/model.gguf --ctx-size 32768 --port 5001 --slot-save-path ./kv-cache
```

**Параметры:**
- `-m` — путь к GGUF-модели
- `--ctx-size` — размер контекста (должен быть >= `ContextSize` в QwenPlayground, по умолчанию 32768)
- `--port` — порт HTTP-сервера (по умолчанию 5001)
- `--gpu-layers` — сколько слоёв перенести на GPU (99 = все)
- `--mmproj` — путь к vision-проектору (для мультимодальности)
- `--slot-save-path` — путь для KV-кеша (для субагентов)

### Проверка

Откройте в браузере:
- `http://127.0.0.1:5001/` — веб-UI llama.cpp
- `http://127.0.0.1:5001/props` — свойства сервера (должен вернуть JSON с `n_ctx`, `media_marker` если есть vision)

## Настройка QwenPlayground

1. Запустите QwenPlayground через лаунчер
2. Вкладка «Настройки»:
   - **Endpoint** — `http://127.0.0.1:5001` (или ваш адрес llama.cpp)
 - **PigeonEndpoint** — адрес pigeon-сервера (опционально, см. ниже «Pigeon-сервер»)
   - **ContextSize** — должен совпадать с `--ctx-size` llama.cpp (по умолчанию 32768)
   - **MaxTokens** — максимальная длина ответа (по умолчанию 2048)
   - **ReasoningEffort** — усилие размышления (XHigh/Medium/Low)
   - **ImageTokensMax** — оценка токенов на изображение для бюджет-гварды (по умолчанию 512)
   - **KvCacheEnabled** — включить KV-кеш (для субагентов, по умолчанию true)
   - **SaveGenerationPrompts** — сохранять промпты генерации (по умолчанию false)
   - **TrafficLogEnabled** — включить трафик-лог (по умолчанию true)
   - **YoloMode** — режим без подтверждений (по умолчанию false, опасно!)
   - **FocusOnConfirmation** — фокус на окне подтверждения (по умолчанию true)
   - **Temperature/TopP/TopK/MinP/RepeatPenalty** — параметры семплирования
   - **Seed** — сид для воспроизводимости (пусто = случайный)

## Параметры модели

QwenPlayground передаёт в llama.cpp следующие параметры:

| Параметр QwenPlayground | Параметр llama.cpp | Описание |
|---|---|---|
| `MaxTokens` | `n_predict` | Максимальная длина генерации |
| `Temperature` | `temperature` | Температура семплирования |
| `TopP` | `top_p` | Нуклеарное семплирование |
| `TopK` | `top_k` | Топ-K семплирование |
| `MinP` | `min_p` | Минимальная вероятность (доля топ-1) |
| `RepeatPenalty` | `repeat_penalty` | Штраф за повторение |
| `Seed` | `seed` | Сид для воспроизводимости (пусто = случайный) |

**Важно:** `ContextSize` в QwenPlayground — это не параметр, который отправляется в llama.cpp. Это локальное ограничение, которое приложение использует для расчёта бюджета контекста. Реальное окно определяет llama.cpp через `--ctx-size`. Если `ContextSize` в QwenPlayground > `n_ctx` в llama.cpp, промпт может не влезть и сервер вернёт ошибку 400.

## Мультимодальность

Для работы с изображениями нужно:

1. **Vision-проектор** (mmproj) для вашей модели:
   - Qwen2-VL: `mmproj-model-f16.gguf`
   - Qwen2.5-VL: `mmproj-model-f16.gguf`
   - Скачайте с Hugging Face вместе с моделью

2. **Запустите llama.cpp с `--mmproj`**:
   ```bash
   llama-server -m qwen2.5-vl-7b.gguf --mmproj mmproj-model-f16.gguf --ctx-size 32768 --port 5001
   ```

3. **Проверьте `/props`**:
   ```bash
   curl http://127.0.0.1:5001/props
   ```
   Должен вернуть `"media_marker": "<something>"` — это маркер, который QwenPlayground использует для вставки изображений в промпт.

Без `media_marker` сервер считается текстовым, и мультимодальные функции (скриншоты, `load_image`) не будут работать.

## Компаньон-модель (опционально)

QwenPlayground может использовать вторую модель ("компаньон") для логит-проб: классификация памяти, дедупликация, rerank, оценка прогресса (sanity_check). Это **опциональная** фича — без неё память работает по тексту.

**Важно: как компаньон сейчас работает только модель серии Gemma4.** Промпты проб зашиты под raw-формат чата Gemma (`<|turn|>`-маркеры, `MemoryClassifier`/`MemoryRecall`) — под другие семейства модели их нужно переписывать.

**Требования к компаньону:**
- llama.cpp-сервер (пробы ходят и в нативный `/completion` с `n_probs`, и в `/v1/chat/completions` с `logprobs` — оба эндпоинта даёт llama.cpp)
- модель серии Gemma4 (например, Gemma4-E4B — маленькая, подходит для фоновых проб)
- рекомендуется на **отдельной машине** — пробы не трогают KV-кеш основной модели, запросы летят параллельно

**Настройка:**
1. Запустите второй llama.cpp-сервер:
   ```bash
   llama-server -m gemma4-e4b.gguf --ctx-size 8192 --port 8001
   ```
2. В QwenPlayground: вкладка «Настройки» → «Память» → «Модель для проб»:
   - **CompanionEndpoint** — адрес второго сервера
   - **CompanionEnabled** — включить/выключить (адрес при выключении сохраняется)

**Что делает компаньон:**
- **Классификация фактов** — модель получает текст и отвечает буквами A-Z (категории: code, build, test, debug, …) и одиночными эмодзи (вайб). Распределение накапливается по топ-N логитам всех сгенерированных позиций.
- **Rerank** — кандидаты реколла помечаются буквами A/B/C…, модель выбирает релевантные текущему диалогу.
- **Дедупликация** — пара фактов оценивается одним токеном 0–9 (похожи/не похожи) + энтропия = уверенность.
- **sanity_check** — оценка прогресса хода (0–9) с энтропией.

**Если компаньон недоступен:** circuit-breaker — после падения пробы дальнейшие запросы fail-fast 60 секунд (без сети), память работает по тексту, sanity_check пишет только в журнал. Без хангов.

## MCP-серверы (опционально)

QwenPlayground поддерживает внешние инструменты через Model Context Protocol (MCP).

**Настройка:**
1. В `settings.json` (вкладка «Настройки» → «MCP») добавьте MCP-серверы:
   ```json
   {
     "McpServers": [
       {
         "Name": "test",
         "Enabled": true,
         "Transport": "stdio",
         "Command": "python",
         "Args": ["mcp_test_server.py"],
         "Description": "Local test server: echo, add, system_info"
       },
       {
         "Name": "blender",
         "Enabled": true,
         "Transport": "stdio",
         "Command": "python",
         "Args": ["-m", "blmcp"],
         "Env": { "BLENDER_MCP_PORT": "10064" },
         "Description": "Blender 3D via MCP bridge (viewport, render, Python exec)"
       },
       {
         "Name": "hf",
         "Enabled": true,
         "Transport": "http",
         "Url": "https://huggingface.co/mcp",
         "Description": "HuggingFace Hub: models, datasets, spaces, papers"
       },
       {
         "Name": "unity",
         "Enabled": true,
         "Transport": "stdio",
         "Command": "dotnet",
         "Args": ["V:\\QwenPlayground\\tools\\unity-mcp-relay\\bin\\UnityMcpRelay.dll", "--project-path", "V:\\QwenPlayground\\FunStuff\\Korovanir Backup 3"],
         "Description": "Unity Editor via Unity MCP bridge (console, camera, scene view, RunCommand)"
       }
     ]
   }
   ```

2. Инструменты регистрируются динамически как `{server}_{tool}` (например, `blender_execute_code`, `hf_search_models`, `unity_console`)

3. При коллизии с built-in инструментом — warn+skip

**Логи:** `logs/mcp/<server>.stderr.log` (ротация 1 МБ)

## Pigeon-сервер (опционально)

Pigeon — мессенджер для Qwen'ов (полное описание — [docs/MESSANGER.md](MESSANGER.md)). Сервер живёт в `tools/pigeon/Pigeon.Server` (HTTP API + веб-UI), клиент-клей — в `Core/Pigeon`.

**Настройка:**
1. Запустить pigeon-сервер: `dotnet run --project tools/pigeon/Pigeon.Server` (по умолчанию `http://127.0.0.1:8199`).
2. В QwenPlayground: «Настройки» → **PigeonEndpoint** — адрес сервера.
3. Подключить сессию: кнопка 🕊 в тулбаре чата или инструмент `pigeon_connect <адрес> [канал]`.

Инструменты на полке `pigeon`: `pigeon_connect`, `pigeon_disconnect`, `pigeon_read`, `pigeon_send`, `pigeon_who`, `pigeon_channels`, `pigeon_status`. Mention (@alias) в сообщении будит heartbeat той сессии, которую упомянули.

## Частые проблемы

### "Сервер не отвечает"

- Проверьте, что llama.cpp запущен и слушает нужный порт
- Проверьте `Endpoint` в настройках QwenPlayground
- Попробуйте открыть `http://127.0.0.1:5001/props` в браузере

### "Промпт не влезает в контекст" (400)

- Увеличьте `--ctx-size` в llama.cpp
- Убедитесь, что `ContextSize` в QwenPlayground <= `n_ctx` в llama.cpp
- Включите компакцию (она автоматическая, но проверьте `CompactKeepRatio`)
- Если 400 приходит из-за изображений (оценка `ImageTokensMax` занизена) — приложение
  автоматически делает принудительную компакцию и повторяет ход один раз;
  поднимите `ImageTokensMax` под ваш `--image-max-tokens`

### "Нет media_marker"

- Запустите llama.cpp с `--mmproj`
- Проверьте, что mmproj-файл совместим с моделью
- Обновите кэш: перезапустите QwenPlayground (маркер кэшируется на 30 секунд)

### "Медленно"

- Перенесите больше слоёв на GPU: `--gpu-layers 99`
- Используйте квантованную модель (Q4_K_M, Q5_K_M)
- Уменьшите `ContextSize` (если не нужен огромный контекст)
- Отключите компаньон-модель, если она на той же машине

### "MCP-сервер не подключается"

- Проверьте, что команда MCP-сервера запускается из терминала
- Проверьте логи: `logs/mcp/<server>.stderr.log`
- Попробуйте `mcp_reload` для перезагрузки MCP-серверов

## Дополнительные ресурсы

- [llama.cpp documentation](https://github.com/ggml-org/llama.cpp/blob/master/docs/)
- [Qwen3 models on Hugging Face](https://huggingface.co/Qwen)
- [MCP specification](https://modelcontextprotocol.io/)
- [docs/CONCEPTS.md](CONCEPTS.md) — как работает агентный цикл
- [ARCHITECTURE.md](../ARCHITECTURE.md) — архитектура приложения