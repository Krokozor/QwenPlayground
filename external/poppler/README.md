# poppler

Invoke directly, no need to look up paths (the version folder is no longer part of the path — the launcher strips the release's top-level directory):
- `external/poppler/Library/bin/pdftotext.exe` — extract the text layer (token-efficient).
- `external/poppler/Library/bin/pdftoppm.exe` — render pages to PNG (I see them, multimodal).
- `external/poppler/Library/bin/pdfinfo.exe` — metadata (page count, size).
- `external/poppler/Library/bin/pdftohtml.exe` — convert to HTML.

If the binaries are missing, the owner can install them from the launcher (Инструменты → poppler → Скачать). Don't try to install them yourself; work without poppler and mention it's unavailable only if the task actually needs it.

## Approach

Same search-then-detail as video, plus a text/image split:

- **Text first** (`pdftotext`): cheap overview for PDFs that have a real text layer. If the output is empty or garbled, the PDF is a scan — switch to images.
- **Pages as images** (`pdftoppm -png -r <dpi> -f <first> -l <last> out.pdf`): for scans, diagrams, tables, and especially "horror" documents (handwritten annotations, xeroxed-then-scanned CIS paperwork) where the text layer is missing or wrong. I read the rendered pages visually. Control `-r` (DPI): ~72 for a cheap multi-page overview, 150–300 for a page I need to read in detail. `-f`/`-l` select the page range so I don't render the whole document.
- **Polish with ffmpeg**: if a rendered page is too dark, washed out, or the region of interest is small, crop/zoom/brightness-adjust the PNG with ffmpeg before looking at it (same as the image workflow above).
- Don't render every page up front: `pdfinfo` for the page count, `pdftotext` or a low-DPI spread for the overview, then high-DPI only the pages that matter.
