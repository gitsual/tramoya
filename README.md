<div align="center">

<img src="assets/logo.png" alt="tramoya" width="560">

<p>
<em>The <strong>tramoya</strong> is the machinery behind a stage: ropes, battens, trapdoors.<br>
The audience never sees it. The show runs on time because of it.</em><br>
<strong>This is that machinery for recorded software demos: you write scenes, it keeps the clock, the cursor, the voice and the cut.<br>Or you just say what you want, and it works out the commands.</strong>
</p>

<p>
  <img alt="Python 3.11+" src="https://img.shields.io/badge/python-3.11%2B-3D0115?style=for-the-badge&logo=python&logoColor=AAA875">
  <img alt="stdlib core" src="https://img.shields.io/badge/core-zero%20deps-3D0115?style=for-the-badge&labelColor=3D0115&color=E1777D">
  <img alt="229 tests" src="https://img.shields.io/badge/229%20tests-one%20real%20encode-3D0115?style=for-the-badge&labelColor=3D0115&color=979367">
  <img alt="ask and do" src="https://img.shields.io/badge/tramoya%20ask-plain%20English%20in-3D0115?style=for-the-badge&labelColor=3D0115&color=AAA875">
  <img alt="MIT" src="https://img.shields.io/badge/license-MIT-3D0115?style=for-the-badge&labelColor=3D0115&color=731F51">
</p>

<a href="#-install"><strong>Install</strong></a> ·
<a href="#-the-demo"><strong>The demo</strong></a> ·
<a href="#-just-say-it"><strong>Just say it</strong></a> ·
<a href="#-why-this-exists"><strong>Why</strong></a> ·
<a href="#-what-you-get"><strong>What you get</strong></a> ·
<a href="#-60-second-tour"><strong>60-second tour</strong></a> ·
<a href="#-the-pipeline"><strong>The pipeline</strong></a> ·
<a href="#-module-map"><strong>Module map</strong></a> ·
<a href="#-verification"><strong>Verification</strong></a>

</div>

---

<a href="#-the-pipeline"><img src="assets/pipeline.png" alt="The tramoya pipeline: direct, record, tts, assemble on stage; deck, captions, music in the booth"></a>

<div align="center">

**stage** Playwright · **recorder** wf-recorder or the browser itself · **voice** Kokoro · **cut** ffmpeg · **marks** one JSON file<br>
**core deps** none · **extras** `[playwright]` `[tts]` · **cli** `tramoya ask | do | marks | assemble | deck | captions | music | cues | record | tts`

<br>

<sub>Two kinds of image on this page. The logo and the diagram are <b>rendered</b> by
<code>scripts/render-assets.sh</code> from the palette in <code>assets/theme.conf</code> (the emblem itself was generated
once with a diffusion model and is kept under <code>assets/brand/</code>). Everything else is <b>real output</b>: the
terminal GIFs are <code>asciinema</code> recordings of the installed command, and the demo video was directed, recorded,
voiced and assembled by this package from the example under <code>examples/</code>. No mock-ups, nothing hand-painted.</sub>

</div>

---

## 🎬 The demo

A one-page notes app, six scenes, one minute. Every frame below was produced by the pipeline: the director drove
the page, the browser recorded itself, Kokoro read the script, `assemble` cut it per scene and fitted the voice.

<p align="center">
  <a href="assets/video/notes-demo.mp4"><img src="assets/gifs/demo.gif" alt="The notes app demo: cursor travels, clicks with a ripple, captions change per scene" width="720"></a>
</p>

<p align="center"><sub>Preview GIF (8 fps, no sound). <a href="assets/video/notes-demo.mp4"><b>Watch the full video with narration</b></a> (59 s, h264 + aac).</sub></p>

The whole thing lives in [`examples/notes-app/`](examples/notes-app): a self-contained `index.html`, a
`script.json` with one line per scene, and a `director.py` of under a hundred lines. Reproduce it from scratch:

```bash
uv pip install -e ".[dev,playwright,tts]" && uv run python -m playwright install chromium

uv run python examples/notes-app/director.py --out examples/notes-app/out   # directs + records (headless) -> take.webm, marks.json
cd examples/notes-app/out
ffmpeg -i take.webm -c:v libx264 -pix_fmt yuv420p take.mp4
tramoya tts --script ../script.json --lang en --out-dir voices/en           # one wav per scene
tramoya marks marks.json                                                    # the scene table
tramoya assemble --video take.mp4 --marks marks.json --voice-dir voices/en --out demo.mp4
```

`--rehearse` runs the same choreography in a few seconds without recording, and `--pace 0.7` slows the whole
take down. The director is what a product would write: scenes are functions, and every scene gets the stage and a
`say` callback that plays that scene's clip (or waits its length when the clip is missing).

---

## 🗣️ Just say it

You do not have to learn the commands. Describe what you want; a language model turns it into a plan of
`tramoya` commands, the plan is checked against the files that actually exist, and nothing runs until you say so.

<p align="center">
  <img src="assets/gifs/assistant.gif" alt="tramoya ask and tramoya do: a plan for adding music, a refusal to translate into a language with no voice, and a plan that runs" width="100%">
</p>

```bash
tramoya ask "put some quiet background music under demo.mp4"     # shows the plan, runs nothing
tramoya do  "build the video with the English voice"             # shows the plan, asks, then runs it
tramoya do  "…" --yes --dry-run                                  # runs it, but every ffmpeg call only prints
```

Three real exchanges, verbatim from the GIF above, with a small local model (`qwen3-coder` through Ollama):

<table>
<tr><th width="34%">You say</th><th>What happens</th></tr>
<tr>
<td><code>put some quiet background music under demo.mp4</code></td>
<td>A two-step plan: synthesize the bed with <code>tramoya music bed --out bed.wav</code>, then
<code>tramoya music mix --video demo.mp4 --bed bed.wav --out demo-with-music.mp4</code>. Each step carries a one-line <em>why</em>.</td>
</tr>
<tr>
<td><code>translate the video into French</code></td>
<td>No steps. The answer explains that voices exist only for English and Spanish, so the request cannot be fulfilled.
The model is told the rule, and <code>preflight</code> would refuse the plan anyway if it tried.</td>
</tr>
<tr>
<td><code>show me the scenes, then build the video with the English voice</code></td>
<td>Two steps: <code>tramoya marks marks.json</code> prints the six-scene table, then <code>assemble</code> cuts the take,
fits each voice clip and writes <code>final-demo.mp4</code>. With <code>--yes</code> it runs straight through.</td>
</tr>
</table>

**Two backends, same plan.** Local and private by default, hosted when you ask for it:

| Backend | How to pick it | Model |
|---|---|---|
| **Ollama** (local) | default when no key is set; `--backend ollama`; `TRAMOYA_LLM=ollama` | `qwen3-coder`, or `--model` / `TRAMOYA_LLM_MODEL` (`OLLAMA_HOST` to point elsewhere) |
| **Claude** (API) | `ANTHROPIC_API_KEY` set; `--backend claude`; `TRAMOYA_LLM=claude` | `claude-sonnet-5`, or `--model` / `TRAMOYA_LLM_MODEL` |

**What keeps it honest.** The model only proposes; the package decides.

- The model sees the command reference and the files in the current directory, and answers in strict JSON. A reply
  that is not a plan gets **one repair round**, then the error is yours to read.
- **Preflight** rejects a plan before anything runs: an input that does not exist, a language with no voices, a
  file used as a narration script that is not one.
- `run_plan` executes **only `tramoya` commands**, in order, and stops at the first failure. Nothing else the model
  writes is ever passed to a shell.
- `tramoya do --dry-run` appends `--dry-run` to every step that would encode, so the whole plan can be inspected
  as ffmpeg argv first.

---

## 📥 Install

```bash
pip install "tramoya @ git+https://github.com/gitsual/tramoya@v0.2.0"
pip install "tramoya[playwright] @ git+https://github.com/gitsual/tramoya@v0.2.0"   # + Stage
pip install "tramoya[tts] @ git+https://github.com/gitsual/tramoya@v0.2.0"          # + Kokoro voice
```

The core has **no Python dependencies**. `ffmpeg`, `ffprobe`, `wf-recorder`, `paplay` and `pactl` are
external binaries, looked up at run time only by the command that needs them. `tramoya ask` / `do` need a running
Ollama or an Anthropic key, nothing else. Pin the tag: the package is consumed by git URL and the tag is the contract.

With `uv`, from a checkout:

```bash
uv venv && uv pip install -e ".[dev,playwright]"
tramoya --version
```

---

## 🎨 Why this exists

A product demo is a small film. Somebody has to keep the clock, move the cursor legibly, say the right
line at the right moment, remember where each scene started so the editor can cut, and then do the editing:
speed up the two minutes where a backend was thinking, freeze the frame while the narration finishes,
burn the captions, lay a bed of music under the voice.

That machinery tends to get rewritten for every demo of every product, each time slightly differently. Marks end
up in several JSON shapes. The scene timeline gets written only when the director exits, so a crash at scene nine
leaves nothing. Pacing lives in module globals that must be kept in sync by hand.

None of that depends on what is being shown. So it lives here, under one name, with tests. A product keeps
exactly what belongs to the product: its pages, its script, its texts.

**The rule that shapes the package:** every ffmpeg invocation goes through a `Runner`. In `dry_run` mode
it records the argv and touches nothing. That is what makes a video pipeline testable without paying an
encode per test, and what makes `tramoya do --dry-run` possible.

---

## ✨ What you get

| Piece | What it does | Where |
|---|---|---|
| **Assistant** | A request in plain language becomes a plan of `tramoya` commands (Ollama or Claude), checked by a deterministic preflight and run only after confirmation. | `assistant.py` |
| **Marks** | A tiny timeline (`scene:<name>:start\|end`, `wait:start\|end`) flushed to disk after **every** mark, so a crashed take still leaves its marks. Loads older Spanish-keyed files unchanged. | `marks.py` |
| **Pacing** | One object for tempo, language and rehearsal mode instead of three globals. `pace` scales every sleep and timeout; `rehearse` runs the whole choreography in seconds. | `pacing.py` |
| **Narrator** | Plays one voice clip per scene through an injectable player, falls back to a timed pause when the clip is missing, and never blocks the take on a missing file. | `narration.py` |
| **Stage** | A Playwright page with a **visible cursor**, a click ripple and a caption band injected into any web app, plus legible mouse travel, a scene registry with decorators, and optional in-browser video recording. | `stage.py` |
| **Recorder** | Drives `wf-recorder` and writes the **offset sidecar**: the recorder's start and the director's start read from `/proc/<pid>/stat`, not guessed. `assemble` picks it up by itself. | `record.py` |
| **Cues** | Single-take mode: a `seconds\|text` cue file and a `k=v` meta file become marks or burn-ready ASS subtitles, recovering the pre-roll from the take's own duration. | `cues.py` |
| **Assembly** | Marks + take + voices → one video: cut per scene, compress any wait over 20 s to ~4 s with a *"3 minutes later"* caption, freeze the last frame if the voice runs long, pad if it runs short, normalise, concat. Slides + narration (+ a clip) → a deck video. | `assembly.py` |
| **Captions** | Split, wrap and page text into SRT or ASS with a legible boxed style; burn into a video; trim to ranges first. | `captions.py` |
| **Music** | A synthesized looping bed, mixed under the voice with sidechain ducking or flat with `--solo`. | `audio.py` |
| **TTS** | Kokoro text-to-speech, one wav per script key, per language. Optional extra. | `tts.py` |
| **CLI** | Every subcommand that would run ffmpeg or a recorder takes `--dry-run` and prints the exact argv. | `cli.py` |

---

## ⚡ 60-second tour

<p align="center">
  <img src="assets/gifs/cli.gif" alt="tramoya on the command line: marks table, assemble dry-run, record dry-run" width="100%">
</p>

<p align="center"><sub>A marks file becomes a scene table; <code>assemble --dry-run</code> prints the ffmpeg calls it would make
and encodes nothing; <code>record --dry-run</code> shows the recorder argv.</sub></p>

**Direct a take.** Scenes are functions; marks and pacing are handed to the stage once. This is the shape of
`examples/notes-app/director.py`.

```python
from pathlib import Path
from tramoya.marks import Marks
from tramoya.pacing import Pacing
from tramoya.stage import SceneRegistry, open_stage

pacing = Pacing(pace=1.0, lang="en")
marks = Marks(path=Path("marks.json"))      # flushed after every mark
scenes = SceneRegistry()

@scenes.scene("search", "Find a note as you type")
def search(stage, say):
    stage.click("#search")
    stage.type_into("#search", "re", delay=180)
    say("search", fallback=6)

with open_stage("http://127.0.0.1:8000/index.html", pacing, marks,
                video_dir=Path("raw"), viewport=(1280, 720)) as stage:
    scenes.run_all(stage, say)
```

Outside a browser the same marks come from context managers:

```python
with marks.scene("intro", "Opening"):
    narrator.say("intro", fallback=6)
with marks.wait("backend thinking"):
    wait_for_the_backend()
```

**Record the screen** when the demo is not a browser page. The sidecar lines both clocks up.

```bash
tramoya record --out take.mp4 --geometry "0,48 1920x1032" --fps 10
```

**Give it a voice.** One wav per key and language, from a JSON script.

```bash
tramoya tts --script script.json --lang en --out-dir voices/en
```

**Assemble.** Check the plan first, then drop `--dry-run`.

```bash
tramoya assemble --video take.mp4 --marks marks.json --voice-dir voices/en --out demo.mp4 --dry-run
tramoya assemble --video take.mp4 --marks marks.json --voice-dir voices/en --out demo.mp4
```

**Wrap it in a deck**, burn captions, add music.

```bash
tramoya deck --slides slides/ --voice-dir voices/en --clip demo.mp4 --clip-duration 59 --out deck.mp4
tramoya captions burn --video deck.mp4 --subtitles deck.srt --out deck-captions.mp4 --trim 0-120,600-780
tramoya music bed --out bed.wav
tramoya music mix --video deck-captions.mp4 --bed bed.wav --out final.mp4
```

**Single continuous take** with a cue file instead of a director, as a desktop tour would do:

```bash
tramoya cues marks --cues take.cues --meta take.meta --duration "$(ffprobe ... take.mp4)" --out marks.json
tramoya cues ass   --cues take.cues --meta take.meta --duration 297.4 --width 1920 --height 1080 --out take.ass
```

---

## 🎞️ The pipeline

Two rooms. **On stage**, the director runs scenes and the recorder rolls. **In the booth**, nothing is
live: every step reads files and writes files, and every ffmpeg call is a value you can print.

```
ask/do   plain-language request           ──▶  a plan of the commands below, checked, then run

direct   Stage · Pacing · Narrator        ──▶  marks.json (+ take.webm when the browser records)
record   wf-recorder + offset sidecar     ──▶  take.mp4, take.mp4.offset.json
tts      script.json                      ──▶  voices/<lang>/<scene>.wav

assemble take + marks + voices            ──▶  demo.mp4     waits compressed, voice fitted per scene
deck     slides/*.png + voices (+ clip)   ──▶  deck.mp4     each page as long as its narration
captions cues or .srt                     ──▶  .ass / burnt video (trimmed first if asked)
music    bed.wav                          ──▶  mixed video, bed ducked under the voice
```

### The marks file

```json
[
  {"t": 0.00,  "key": "scene:welcome:start", "label": "A notes app"},
  {"t": 8.92,  "key": "scene:welcome:end",   "label": "A notes app"},
  {"t": 40.0,  "key": "wait:start",          "label": "backend"},
  {"t": 114.0, "key": "wait:end",            "label": "backend"}
]
```

`t` is seconds since the director started. When a separate recorder started a little earlier, the sidecar holds
that offset and `assemble` adds it; when the browser records itself, the director resets its clock as the page
opens and there is no offset at all. Files written by older directors (`escena-3-inicio`, `espera-fin`, rows
named `clave` and `etiqueta`) load through the same function and give the same scene bounds; there is a
thirteen-scene one under `tests/fixtures/`.

### What `assemble` does per scene

1. Cut `[start, end)` out of the take, shifted by the offset.
2. Inside the scene, any wait window longer than 20 s is sped up to about 4 s with a caption saying how
   long it really was. The rest plays at normal speed.
3. If the scene's voice clip is longer than the scene, the last frame freezes until the voice ends. If it
   is shorter, silence pads the tail.
4. The voice is normalised with `loudnorm`; a scene with no clip gets a silent track so concat never sees
   mismatched streams.
5. Everything is concatenated once.

---

## 🗺️ Module map

```
src/tramoya/
├── assistant.py   SYSTEM_PROMPT · OllamaBackend · ClaudeBackend · ask · preflight · run_plan
├── marks.py       Mark · Marks (incremental flush) · load_marks · scene_bounds · wait_windows
├── pacing.py      Pacing(pace, rehearse, lang) · sleep · op_timeout
├── i18n.py        Messages · t(key, lang, **kw)
├── narration.py   Script · DeckScript · Narrator · wav_seconds
├── console.py     banner · step · note · ok · warn · fail · sim
├── stage.py       OVERLAY_JS · Stage · SceneRegistry · open_stage · AppProcess · http_ready   [playwright]
├── record.py      Recorder · wf_recorder_argv · offset_sidecar · read_sidecar · NullSink
├── cues.py        parse_cues_with_total · parse_meta · offset_from_duration · cues_to_marks · cues_to_ass
├── tts.py         synthesize · synthesize_script                                              [tts]
├── ffmpeg.py      Runner(dry_run) · probe_duration · filter recipes as pure functions
├── assembly.py    assemble_marks · plan_segments · assemble_deck · Segment
├── captions.py    split · wrap · page_cues · render_srt · render_ass · burn_argv · trim_plan
├── audio.py       build_loop · render_wav · duck_filter · solo_filter · mix_argv
└── cli.py         tramoya ask | do | marks | assemble | deck | captions | music | cues | record | tts
```

Fifteen modules. Anything that shells out is behind `Runner`; anything that needs Playwright or Kokoro is behind
an extra and skipped cleanly when the extra is absent; anything that talks to a model is behind an injectable
`post` function, so the assistant's tests never open a socket.

---

## ✅ Verification

```bash
uv run ruff check src tests examples
uv run python -m pytest -q             # 229 fast tests, nothing is encoded, no network
uv run python -m pytest -q -m slow     # 1 test: three real seconds through the whole pipeline
```

The fast suite runs every ffmpeg path in `dry_run` and asserts on the argv, and drives the assistant with fake
backends and fake HTTP. The slow test renders a `testsrc` clip, writes marks for two scenes, hands one of them a
voice clip longer than the scene, and checks the output duration: the freeze happened. It is the test that caught a
typo in an `ffprobe` flag that no dry run could see, and the one that taught us `loudnorm` returns NaN on digital
silence, so the fixture is a quiet tone.

CI runs the fast suite on Python 3.11 and 3.12, with and without the `playwright` extra
(`.github/workflows/ci.yml`).

---

## 🗂️ Repository map

```
tramoya/
├── src/tramoya/            the package (see the module map)
├── examples/notes-app/     index.html · script.json · director.py: the demo above, end to end
├── tests/                  one file per module + test_cli.py + test_slow_pipeline.py
│   └── fixtures/           legacy-marks-v11.json: thirteen scenes in the older key format
├── assets/
│   ├── theme.conf                      the palette; every rendered asset reads it
│   ├── brand/emblem-flux.png           the generated emblem the logo is built from
│   ├── logo.png · logo-mark.png        rendered by scripts/render-assets.sh
│   ├── pipeline.png                    rendered from templates/pipeline.svg.in
│   ├── gifs/demo.gif                   preview of the demo video
│   ├── gifs/cli.gif · assistant.gif    asciinema recordings of the installed CLI
│   └── video/notes-demo.mp4            the demo, with narration
├── templates/pipeline.svg.in           the diagram with @COLOR_X@ tokens
├── scripts/
│   ├── render-assets.sh    logos | pipeline | gif | assistant | all
│   ├── cli-cast.sh         what the CLI gif records
│   └── assistant-cast.sh   what the assistant gif records (needs Ollama running)
├── pyproject.toml          hatchling, extras playwright / tts / dev, ruff, pytest markers
└── .github/workflows/ci.yml
```

---

## 📚 Documentation

| Where | What |
|---|---|
| This README | The whole flow and the module map. |
| `tramoya <cmd> --help` | Every flag; the parser is the documentation of record. |
| `examples/notes-app/` | A complete director, script and page you can copy and rename. |
| Module docstrings | Each file opens with what it owns and why. |

---

## 📄 License

MIT. See [LICENSE](LICENSE).

<div align="center">
<br>
<img src="assets/logo-mark.png" alt="" width="72">
<br>
<sub>the machinery behind the take</sub>
</div>
