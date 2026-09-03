# Внешние инструменты (external/)

Бинарники здесь скачивает и версионирует лаунчер (авто-обновление). Вызываются через
`shell`. Пути — относительно корня воркспейса. Я мультимодальный, но для мультимедиа
ffmpeg делает зум/кроп/кадры чище и быстрее, чем ручные shell-хаки — используй его.

## ffmpeg / ffprobe — `external/ffmpeg/bin/`

Бинарники: `external/ffmpeg/bin/ffmpeg.exe`, `external/ffmpeg/bin/ffprobe.exe`.

### Изображения
- Зум/кроп: `ffmpeg -i in.jpg -vf "crop=Ш:В:Х:У" out.jpg` (Ш/В — размер, Х/У — смещение).
- Ресайз: `ffmpeg -i in.jpg -vf "scale=1280:-1" out.jpg`.

### Видео
- Кадры «вскользь» (общий план, мало токенов): `ffmpeg -i in.mp4 -vf "fps=1" frames/f_%03d.jpg`.
- Нашёл нужный сегмент → отмотать и зарендерить плотнее:
  `ffmpeg -ss <старт> -to <конец> -i in.mp4 -vf "fps=4" seg/s_%03d.jpg`.
- Метаданные (длительность/разрешение):
  `ffprobe -v error -show_entries format=duration:stream=width,height -of default=nw=1 in.mp4`.

Подход: сначала вскользь (мало кадров), потом детально в нужном месте — не жри контекст
кадрами заранее.
