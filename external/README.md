# External tools (external/)

`external/` holds external binaries — versioned and auto-updated by the launcher. Invoke them through `shell` (paths relative to the workspace root). I'm multimodal, but for multimedia a dedicated binary is cleaner and faster than manual shell hacks. ffmpeg in particular is a powerful tool for my vision — use it creatively, not just as a fixed recipe.

## ffmpeg / ffprobe

Binaries (use these paths directly, no need to look them up):
- `external/ffmpeg/bin/ffmpeg.exe` — the main tool for images and video (convert, crop, zoom, extract frames, adjust).
- `external/ffmpeg/bin/ffprobe.exe` — inspect media metadata (duration, resolution, codecs) without decoding.

Approach: the goal is to make the media easier to perceive, then look only at what matters.
- Image: crop/zoom into the region that matters so the detail is legible. If the image is too dark, washed out, or cluttered, adjust brightness/contrast or crop away the clutter to simplify what you're looking at.
- Video: don't process it frame-by-frame. First slice it into a handful of evenly-spaced frames for a cheap overview of the whole thing; find the segment that looks interesting, then generate denser frames on just that segment. This is search-then-detail — cheap overview first, expensive detail only where it pays off. Don't burn context on frames in advance: extract only what the current step needs, and re-extract at higher density once you've narrowed down.

## Poppler (PDF)

Binaries (use these paths directly, no need to look them up; the version folder is part of the zip layout):
- `external/poppler/poppler-26.09.0/Library/bin/pdftotext.exe` — extract the text layer (token-efficient).
- `external/poppler/poppler-26.09.0/Library/bin/pdftoppm.exe` — render pages to PNG (I see them, multimodal).
- `external/poppler/poppler-26.09.0/Library/bin/pdfinfo.exe` — metadata (page count, size).
- `external/poppler/poppler-26.09.0/Library/bin/pdftohtml.exe` — convert to HTML.

Approach — same search-then-detail as video, plus a text/image split:
- **Text first** (`pdftotext`): cheap overview for PDFs that have a real text layer. If the output is empty or garbled, the PDF is a scan — switch to images.
- **Pages as images** (`pdftoppm -png -r <dpi> -f <first> -l <last> out.pdf`): for scans, diagrams, tables, and especially "horror" documents (handwritten annotations, xeroxed-then-scanned CIS paperwork) where the text layer is missing or wrong. I read the rendered pages visually. Control `-r` (DPI): ~72 for a cheap multi-page overview, 150–300 for a page I need to read in detail. `-f`/`-l` select the page range so I don't render the whole document.
- **Polish with ffmpeg**: if a rendered page is too dark, washed out, or the region of interest is small, crop/zoom/brightness-adjust the PNG with ffmpeg before looking at it (same as the image workflow above).
- Don't render every page up front: `pdfinfo` for the page count, `pdftotext` or a low-DPI spread for the overview, then high-DPI only the pages that matter.
