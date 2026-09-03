# Настройка LLM-сервера

QwenPlayground работает с **llama.cpp** через нативный API (не OpenAI-совместимый). Это осознанный выбор: нативный эндпоинт `/completion` принимает объектный prompt с мультимодальными данными, что нужно для работы с изображениями.

## Требования

- **llama.cpp** — сервер инференса (сборка с поддержкой `/completion`, `/tokenize`, `/props`)
- **GGUF-модель** — Qwen3 или совместимая (шаблон Qwen3 в `assets/chat_template.jinja`)
- **Опционально**: vision-проектор (mmproj) для мультимодальности

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

**Параметры:**
- `-m` — путь к GGUF-модели
- `--ctx-size` — размер контекста (должен быть >= `ContextSize` в QwenPlayground, по умолчанию 32768)
- `--port` — порт HTTP-сервера (по умолчанию 5001)
- `--gpu-layers` — сколько слоёв перенести на GPU (99 = все)
- `--mmproj` — путь к vision-проектору (для мультимодальности)

### Проверка

Откройте в браузере:
- `http://127.0.0.1:5001/` — веб-UI llama.cpp
- `http://127.0.0.1:5001/props` — свойства сервера (должен вернуть JSON с `n_ctx`, `media_marker` если есть vision)

## Настройка QwenPlayground

1. Запустите QwenPlayground через лаунчер
2. Вкладка «Настройки»:
   - **Endpoint** — `http://127.0.0.1:5001` (или ваш адрес llama.cpp)
   - **ContextSize** — должен совпадать с `--ctx-size` llama.cpp (по умолчанию 32768)
   - **MaxTokens** — максимальная длина ответа (по умолчанию 2048)
   - **ReasoningEffort** — усилие размышления (XHigh/Medium/Low)
   - **Temperature/TopP/TopK/MinP/RepeatPenalty** — параметры семплирования

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

## Частые проблемы

### "Сервер не отвечает"

- Проверьте, что llama.cpp запущен и слушает нужный порт
- Проверьте `Endpoint` в настройках QwenPlayground
- Попробуйте открыть `http://127.0.0.1:5001/props` в браузере

### "Промпт не влезает в контекст" (400)

- Увеличьте `--ctx-size` в llama.cpp
- Убедитесь, что `ContextSize` в QwenPlayground <= `n_ctx` в llama.cpp
- Включите компакцию (она автоматическая, но проверьте `CompactKeepRatio`)

### "Нет media_marker"

- Запустите llama.cpp с `--mmproj`
- Проверьте, что mmproj-файл совместим с моделью
- Обновите кэш: перезапустите QwenPlayground (маркер кэшируется на 30 секунд)

### "Медленно"

- Перенесите больше слоёв на GPU: `--gpu-layers 99`
- Используйте квантованную модель (Q4_K_M, Q5_K_M)
- Уменьшите `ContextSize` (если не нужен огромный контекст)
- Отключите компаньон-модель, если она на той же машине

## Дополнительные ресурсы

- [llama.cpp documentation](https://github.com/ggml-org/llama.cpp/blob/master/docs/)
- [Qwen3 models on Hugging Face](https://huggingface.co/Qwen)
- [docs/CONCEPTS.md](CONCEPTS.md) — как работает агентный цикл
- [ARCHITECTURE.md](../ARCHITECTURE.md) — архитектура приложения
