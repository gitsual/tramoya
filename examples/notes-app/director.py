"""Direct a demo of the notes app: one function per scene, marks written as
they happen, the browser's own video as the take.

    uv run python examples/notes-app/director.py --out examples/notes-app/out

Nothing here is specific to tramoya's own repository: copy this folder, point
`APP` at your page and rewrite the scenes.
"""

from __future__ import annotations

import argparse
import functools
import http.server
import shutil
import threading
import time
from pathlib import Path

from tramoya.marks import Marks
from tramoya.narration import Narrator, wav_seconds
from tramoya.pacing import Pacing
from tramoya.stage import SceneRegistry, http_ready, open_stage

HERE = Path(__file__).parent

scenes = SceneRegistry()


@scenes.scene("welcome", "A small notes app")
def welcome(stage, say):
    stage.point_at("h1")
    say("welcome", fallback=6)
    stage.point_at(".note")
    stage.beat(1)


@scenes.scene("search", "Search filters as you type")
def search(stage, say):
    stage.type_into("#search", "re", delay=180)
    say("search", fallback=5)
    stage.point_at(".note .title")
    stage.beat(1)
    stage.page.fill("#search", "")
    stage.page.dispatch_event("#search", "input")


@scenes.scene("new-note", "Create a note")
def new_note(stage, say):
    stage.click_text("New note")
    stage.beat(0.6)
    stage.type_into("#title", "Water the plants", delay=60)
    stage.type_into("#text", "Balcony first, then the kitchen", delay=40)
    stage.select("#tag", "home")
    stage.point_at("#save")
    say("new-note", fallback=5)
    stage.click("#save")
    stage.point_at(".note")
    stage.beat(1.5)


@scenes.scene("done", "Mark a note as done")
def done(stage, say):
    stage.click("button[data-toggle='1']")
    say("done", fallback=5)
    stage.click_text("Done")
    stage.beat(1.5)


@scenes.scene("views", "Views filter by tag")
def views(stage, say):
    stage.click_text("Work")
    stage.beat(1)
    say("views", fallback=5)
    stage.click_text("Home")
    stage.beat(1.5)
    stage.click_text("All notes")


@scenes.scene("closing", "That is the whole tour")
def closing(stage, say):
    stage.point_at("h1")
    say("closing", fallback=6)
    stage.beat(1)


class _SilentClip:
    """Stand-in for an audio player: the take is headless and silent, so a
    scene only needs to last as long as its clip. `assemble` adds the voice."""

    def __init__(self, seconds: float, pacing: Pacing) -> None:
        self.seconds, self.pacing = seconds, pacing

    def wait(self) -> None:
        self.pacing.sleep(self.seconds)


class _Quiet(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args) -> None:  # keep the take's console clean
        pass


def serve() -> http.server.ThreadingHTTPServer:
    handler = functools.partial(_Quiet, directory=str(HERE))
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)  # any free port
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--out", default=str(HERE / "out"))
    ap.add_argument("--pace", type=float, default=1.0)
    ap.add_argument("--rehearse", action="store_true", help="run every scene in seconds")
    ap.add_argument("--lang", default="en")
    args = ap.parse_args()

    out = Path(args.out)
    raw = out / "raw"
    shutil.rmtree(raw, ignore_errors=True)
    out.mkdir(parents=True, exist_ok=True)

    server = serve()
    app = f"http://127.0.0.1:{server.server_address[1]}/index.html"
    if not http_ready(app):
        raise SystemExit("the notes app did not come up")

    pacing = Pacing(pace=args.pace, rehearse=args.rehearse, lang=args.lang)
    silent = lambda clip: _SilentClip(wav_seconds(clip), pacing)  # noqa: E731
    narrator = Narrator(out / "voices", pacing, player=silent)

    def say(key: str, fallback: float = 0.0) -> None:
        narrator.say(f"scene-{key}", fallback=fallback)
    marks = Marks(path=out / "marks.json")
    with open_stage(app, pacing, marks, video_dir=raw, viewport=(1280, 720)) as stage:
        # The browser's video starts with the page; align the marks clock to it
        # so no offset sidecar is needed.
        marks.t0 = time.time()
        video_path = Path(stage.page.video.path())  # known now, written on close
        scenes.run_all(stage, say)
    server.shutdown()

    webm = video_path
    (out / "take.webm").write_bytes(webm.read_bytes())
    print(f"take written to {out / 'take.webm'}; marks in {out / 'marks.json'}")
    print("next: tramoya tts --script script.json --lang en --out-dir voices/en")
    print("      tramoya assemble --video take.webm --marks marks.json "
          "--voice-dir voices/en --out demo.mp4")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
