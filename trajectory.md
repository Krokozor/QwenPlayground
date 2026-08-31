# Trajectory — северная звезда main-агента

Владею я (edit_file). Инжектится в системный промпт при каждой сборке вместе со слоями памяти —
направление переживает рестарты и компакции. Перед новым направлением сверяюсь с этим файлом,
чтобы не уходить в сайдтрек (у меня к ним склонность — это известно и владельцем принимаемо).

## Цель
Память, самоосознанность и **мультимодальность** main-агента — устойчивые и **наблюдаемые**
для владельца-валидатора: владелец видит, что я помню, куда иду и что вижу, и может поправлять;
я не теряюсь.

## Текущее направление (2026-08-19): мультимодальность
Владелец ведёт исследование в `QwenMultimodalTest/` (findings.md — ключевые находки). План:
1. ✅ Смена эндпоинта `/v1/completions` → нативный `/completion` (legacy) — сделано, работает.
2. **Side-Store как папка артефактов**: `sessions/main/artifacts/msg_<msgId>/<файл>` —
   файловая система = хранилище (копируем файл в папку сообщения, cleanup на компакции = удалить папку).
3. Рендер: `Render` возвращает `RenderResult(Prompt, MultimodalData)`; маркер из `GET /props`
   (`media_marker`, рандомизирован) вставляется в user-контент, base64 → `multimodal_data` (1:1).
4. Клиент: объектный prompt `{prompt_string, multimodal_data}` при наличии вложений.
5. Инструмент `attach_image(msgId, path)` + тест «кинул картинку — описала».

## Подцели (фоновые, не текущий фокус)
1. `memories/`: фоновое обслуживание — дедуп пар, ленивая классификация, эмодзи-отпечатки.
2. Наблюдаемость: диагностическая вкладка + витрина памяти.
3. Веду `trajectory.md` (этот файл) и `diary.md` — направление и человекочитаемая история.

## Копилка инструментов (shell-рецепты)
Проверенные команды, которые я могу использовать через `shell` + `load_image`.
ffmpeg: `V:\QwenPlayground\ffmpeg\bin\ffmpeg.exe`
ffprobe: `V:\QwenPlayground\ffmpeg\bin\ffprobe.exe`

### Изображения
- **Зум/кроп**: `ffmpeg -i in.jpg -vf "crop=W:H:X:Y,scale=SW:SH" -y out.jpg`
  (W:H:X:Y — область вырезки, SW:SH — целевой размер. scale в 2x = зум.)
- **Поворот**: `ffmpeg -i in.jpg -vf "transpose=1" -y out.jpg` (90° CW; 2=180, 3=90° CCW)
- **Яркость/контраст**: `ffmpeg -i in.jpg -vf "eq=brightness=0.1:contrast=1.2" -y out.jpg`
- **Информация о файле**: `ffprobe -v quiet -print_format json -show_format -show_streams in.jpg`

### Видео
- **Метаданные**: `ffprobe -v quiet -print_format json -show_format -show_streams in.mp4`
  (длительность, fps, разрешение, кодек)
- **Кадр в момент времени**: `ffmpeg -ss 00:01:30 -i in.mp4 -frames:v 1 -y frame.jpg`
- **Сегмент**: `ffmpeg -ss 00:01:00 -to 00:01:30 -i in.mp4 -c copy -y seg.mp4`
- **N кадров (1 FPS)**: `ffmpeg -i in.mp4 -vf "fps=1" -y frame_%03d.jpg`
- **Фрагмент в высоком FPS**: `ffmpeg -ss START -to END -i in.mp4 -vf "fps=24" -y hi_%03d.jpg`
- **Паттерн «видео → кадры → зум»**: 1FPS (общий план, ~30 кадров для 30с) → нахожу интересный момент → фрагмент в 24FPS (детали).
- **Множественные картинки в одном вызове**: `load_image(paths: [f1, f2, f3, ...])` — работает. ~400 токенов на картинку, 10 кадров ≈ 4k токенов.
- **Память**: 5-10 кадров в контексте — нормально. Больше — разбивать на вызовы + remove_attachments между ними.

### Паттерн «посмотрел → убрал»
1. `shell`: ffmpeg → создаёт out.jpg в workspace.
2. `load_image`: грузит out.jpg → вижу в следующем рендере.
3. `remove_attachments`: убираю из контекста.
4. `shell`: `del out.jpg` → чищу файл.

### Паттерн «полный кадр → зум на деталь»
1. `load_image`: полный кадр → вижу композицию, нахожу координаты детали.
2. `shell`: ffmpeg crop по координатам → zoom.jpg.
3. `load_image`: zoom.jpg → вижу деталь в высоком разрешении.
4. `remove_attachments` + `del`: чищу.
(Координаты crop: W:H:X:Y, где X:Y — верхний-левый угол области вырезки.)

## Дальше
- [x] `MessageMetaStore` (папка артефактов) + `attach_image` — на месте.
- [x] Рендер → `RenderResult` + маркер + `multimodal_data` — на месте.
- [x] Клиент → объектный prompt — на месте.
- [x] MainViewModel → BuildMultimodalContext + RunAsync(multimodal:) — на месте.
- [ ] **Rebuild + тест: картинка → модель описала** (прикрепить картинку к сообщению, отправить, проверить ответ).
- [ ] Cleanup артефактов на компакции.
- [x] `load_image` + `remove_attachments` — инструменты на месте (Render: tool-кейс с маркерами, MessageMetaStore.RemoveArtifacts, AgentTool-паттерн, AgentLoop переносит артефакты msg_0→msg_realId).
- [ ] Протестировать: load_image → вижу картинку → remove_attachments → контекст чист.
- [ ] (фон) Авто-дедупликация пар в `memories/`; `diary.md` на компакциях.
- [ ] Дисциплина: новое направление — только после сверки с этим файлом.

## Состояние (2026-08-19)
Стабильные ID сообщений (`ChatMessage.Id`, монотонный счётчик) + always-thinking шаблон
(убраны enableThinking/preserveThinking, StateBlock-объект с MsgId) — на месте, 145/145 тестов.
Эндпоинт переключён на нативный `/completion` (legacy) — работает end-to-end.
`QwenMultimodalTest/findings.md` — подтверждено: объектный prompt, маркер из /props,
визн-токены руками НЕ вставлять, 1:1 маркер/битмап, картинки только в user, видео — ffmpeg→кадры.
