# tramoya

*Tramoya* is the stage machinery of a theatre: everything behind the curtain
that makes the show run on time. This package is that, for recorded software
demos. It grew out of eleven takes of a real product demo and one desktop
tour, and it holds the parts that never depended on the product being shown.

It gives you:

- **Scene marks**: a tiny timeline written while the take runs, so the
  post-production knows where every scene and every long wait begins and
  ends. Written incrementally, so a crashed take still leaves its marks.
- **Pacing and narration**: one `Pacing` object instead of a pile of globals,
  a `Narrator` that plays voice clips per scene, and a rehearsal mode that
  runs the whole choreography in seconds.
- **A Playwright stage**: a visible cursor, a click ripple and a caption band
  injected into any web page, plus a `Stage` with legible mouse travel.
- **Recording**: a `wf-recorder` driver with the sidecar that lines the
  director's clock up with the recorder's, a null audio sink, and support for
  single-take cue files.
- **Post-production**: marks + recording + voice clips → one video with long
  waits compressed and captioned; slide deck + narration (+ a clip) → one
  video; SRT/ASS captions; a synthesized background bed ducked under the
  voice; Kokoro text-to-speech.

Everything that touches ffmpeg goes through a `Runner` with a `dry_run` mode,
so the entire pipeline can be checked, and tested, without paying an encode.

## Install

```
pip install "tramoya @ git+https://github.com/gitsual/tramoya@v0.1.0"
pip install "tramoya[playwright] @ git+https://github.com/gitsual/tramoya@v0.1.0"   # Stage
pip install "tramoya[tts] @ git+https://github.com/gitsual/tramoya@v0.1.0"          # Kokoro
```

The base package has no Python dependencies. `ffmpeg`, `ffprobe`,
`wf-recorder`, `paplay` and `pactl` are external binaries, looked up at run
time only by the commands that need them.

## The flow

```
record  ──▶  marks.json + take.mp4 (+ take.mp4.offset.json)
   │
   ├── tts        script.json ──▶ voices/<lang>/<scene>.wav
   │
assemble  take + marks + voices ──▶ demo.mp4      (waits compressed, voice fitted per scene)
deck      slides/*.png + voices (+ demo.mp4) ──▶ deck.mp4
captions  cues ──▶ .ass / burn .srt into a video
music     bed ──▶ bed.wav ; mix bed into a video, ducked under the voice
```

### 1. Direct a take

```python
from pathlib import Path

from tramoya.marks import Marks
from tramoya.pacing import Pacing
from tramoya.stage import SceneRegistry, open_stage

pacing = Pacing(pace=1.0, lang="en")
marks = Marks(path=Path("marks.json"))    # flushed after every mark
scenes = SceneRegistry()

@scenes.scene("board", "One console, several projects")
def board(stage):
    stage.click_text("Projects")
    stage.point_at("#board")
    stage.beat(2)

with open_stage("http://127.0.0.1:8765", pacing, marks, video_dir=Path("raw")) as stage:
    scenes.run_all(stage)
```

Outside a browser, the same marks come from a context manager:

```python
with marks.scene("intro", "Opening"):
    narrator.say("intro", fallback=6)
with marks.wait("model thinking"):
    wait_for_the_backend()
```

### 2. Record the screen

```
tramoya record --out take.mp4 --geometry "0,48 1920x1032" --fps 10
```

`Recorder` writes `take.mp4.offset.json` with the offset between the
recorder's start and the director's start (read from `/proc/<pid>/stat`, not
guessed), and `assemble` picks it up automatically.

For a single continuous take annotated with a cue file (`seconds|text` per
line, as the vivac desktop tour does), `tramoya cues marks` turns the cues into
scene marks and `tramoya cues ass` into burn-ready subtitles, recovering the
recorder pre-roll from the file's own duration.

### 3. Voice

```
tramoya tts --script script.json --lang en --out-dir voices/en
```

`script.json` maps a key to its text per language: `{"intro": {"en": "...",
"es": "..."}}`. Requires the `tts` extra (Kokoro, 24 kHz, one wav per key).

### 4. Assemble

```
tramoya assemble --video take.mp4 --marks marks.json --voice-dir voices/en --out demo.mp4 --dry-run
```

Per scene: cut it out, speed up any wait longer than 20 s to about 4 s with a
"3 minutes later" caption, freeze the last frame if the voice runs longer,
pad with silence if it runs shorter, normalize the voice with `loudnorm`, and
concatenate. Drop `--dry-run` to render.

```
tramoya deck --slides slides/ --voice-dir voices/en --clip demo.mp4 --clip-duration 145 --out deck.mp4
```

`slides/index.json` maps page names to PNG stems (`{"pages": {"cover": "001"}}`).
Each page stays on screen for its narration plus a short tail; the clip plays
right after the page named `video` (`--clip-page` to change it).

### 5. Captions and music

```
tramoya captions burn --video deck.mp4 --subtitles deck.srt --out deck-captions.mp4 [--trim 0-120,600-780]
tramoya music bed --out bed.wav
tramoya music mix --video deck.mp4 --bed bed.wav --out deck-music.mp4 [--solo]
```

## Marks file

```json
[
  {"t": 4.31, "key": "scene:opening:start", "label": "One console, several projects"},
  {"t": 16.16, "key": "scene:opening:end", "label": "One console, several projects"},
  {"t": 40.0, "key": "wait:start", "label": "model"},
  {"t": 114.0, "key": "wait:end", "label": "model"}
]
```

Files written by the older Spanish-keyed directors (`escena-N-inicio`,
`espera-fin`, rows named `clave`/`etiqueta`) load unchanged.

## Development

```
uv venv && uv pip install -e ".[dev,playwright]"
uv run ruff check src tests
uv run python -m pytest -q          # fast: nothing is encoded
uv run python -m pytest -q -m slow  # renders 3 s of real video through the pipeline
```

## License

MIT.
