# ffmpeg

Invoke directly, no need to look up paths:
- `external/ffmpeg/bin/ffmpeg.exe` — the main tool for images and video (convert, crop, zoom, extract frames, adjust).
- `external/ffmpeg/bin/ffprobe.exe` — inspect media metadata (duration, resolution, codecs) without decoding.

I'm multimodal, but for multimedia a dedicated binary is cleaner and faster than manual shell hacks. ffmpeg in particular is a powerful tool for my vision — use it creatively, not just as a fixed recipe.

If the binary is missing, the owner can install it from the launcher (Инструменты → ffmpeg → Скачать). Don't try to install it yourself; work without ffmpeg and mention it's unavailable only if the task actually needs it.

## Approach

The goal is to make the media easier to perceive, then look only at what matters.

- Image: crop/zoom into the region that matters so the detail is legible. If the image is too dark, washed out, or cluttered, adjust brightness/contrast or crop away the clutter to simplify what you're looking at.
- Video: don't process it frame-by-frame. First slice it into a handful of evenly-spaced frames for a cheap overview of the whole thing; find the segment that looks interesting, then generate denser frames on just that segment. This is search-then-detail — cheap overview first, expensive detail only where it pays off. Don't burn context on frames in advance: extract only what the current step needs, and re-extract at higher density once you've narrowed down.
