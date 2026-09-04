# External tools (external/)

Binaries here are downloaded and versioned by the launcher (auto-update). They are invoked through
`shell`. Paths are relative to the workspace root. I am multimodal, but for multimedia ffmpeg does
zoom/crop/frames cleaner and faster than manual shell hacks — use it.

## ffmpeg / ffprobe — `external/ffmpeg/bin/`

Binaries: `external/ffmpeg/bin/ffmpeg.exe`, `external/ffmpeg/bin/ffprobe.exe`.

### Images
- Zoom/crop: `ffmpeg -i in.jpg -vf "crop=W:H:X:Y" out.jpg` (W/H — size, X/Y — offset).
- Resize: `ffmpeg -i in.jpg -vf "scale=1280:-1" out.jpg`.

### Video
- Frames "in passing" (overview, few tokens): `ffmpeg -i in.mp4 -vf "fps=1" frames/f_%03d.jpg`.
- Found the segment you need → seek and render denser:
  `ffmpeg -ss <start> -to <end> -i in.mp4 -vf "fps=4" seg/s_%03d.jpg`.
- Metadata (duration/resolution):
  `ffprobe -v error -show_entries format=duration:stream=width,height -of default=nw=1 in.mp4`.

Approach: first in passing (few frames), then in detail at the spot you need — do not burn context
on frames in advance.
