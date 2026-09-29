"""The generic director: `tramoya direct tramoya.toml`.

It does what `examples/notes-app/director.py` does by hand, from a profile
instead of Python: bring the app up (or trust a URL), open the stage, run
every scene through the step language, and leave behind the take, the marks,
the narration script and one accessibility snapshot per scene.

Everything that touches a browser or a process is a module-level name
(`open_stage`, `http_ready`, `launch_app`) so tests replace it with a double.
"""

from __future__ import annotations

import shlex
import shutil
import subprocess
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from tramoya.marks import Marks
from tramoya.narration import Narrator, wav_seconds
from tramoya.pacing import Pacing
from tramoya.profile import Profile, run_steps
from tramoya.snapshots import write_snapshot
from tramoya.stage import http_ready, open_stage

READY_TIMEOUT = 30.0


@dataclass
class Result:
    out: Path
    scenes: list[str] = field(default_factory=list)

    @property
    def take(self) -> Path:
        return self.out / "take.webm"

    @property
    def marks(self) -> Path:
        return self.out / "marks.json"


class _SilentClip:
    """The take is headless and silent: a scene only lasts as long as its
    clip would; `assemble` adds the voice afterwards."""

    def __init__(self, seconds: float, pacing: Pacing) -> None:
        self.seconds, self.pacing = seconds, pacing

    def wait(self) -> None:
        self.pacing.sleep(self.seconds)


def launch_app(command: str, cwd: Path) -> Any:
    """Start the app's own command in its project directory."""
    return subprocess.Popen(shlex.split(command), cwd=str(cwd))


def _stop(proc: Any) -> None:
    if proc is None or proc.poll() is not None:
        return
    proc.terminate()
    try:
        proc.wait(timeout=10)
    except Exception:  # noqa: BLE001 -- a stubborn app is killed, not reported
        proc.kill()
        proc.wait(timeout=10)


def describe(profile: Profile) -> str:
    """The plan a dry run prints: where the app is, then each scene's steps."""
    where = profile.url or profile.ready or "?"
    lines = [f"{profile.name}: {len(profile.scenes)} scenes at {where}"]
    if profile.launch:
        lines.append(f"  launch: {profile.launch}")
    for scene in profile.scenes:
        lines.append(f"  {scene.name}: {scene.label}")
        lines.extend(f"      {step}" for step in scene.steps)
    return "\n".join(lines)


def run_profile(
    profile: Profile,
    out: Path,
    pace: float | None = None,
    rehearse: bool = False,
    lang: str | None = None,
    url: str | None = None,
    launch: str | None = None,
    storage_state: str | None = None,
    headless: bool = True,
) -> Result:
    """Run every scene of `profile` and write the take under `out`.

    `url`, `launch` and `storage_state` override the profile's values, so a
    CI job can point the same scenes at a staging deployment. Credentials
    never come through here: `storage_state` is a Playwright session file.
    """
    if not profile.scenes:
        raise ValueError(f"{profile.name}: no scenes to run")
    out = Path(out)
    raw = out / "raw"
    shutil.rmtree(raw, ignore_errors=True)
    out.mkdir(parents=True, exist_ok=True)

    launch = launch if launch is not None else profile.launch
    target = url or profile.url or profile.ready
    ready = profile.ready or target
    state_file = storage_state if storage_state is not None else profile.storage_state
    state_path = (profile.root / state_file) if state_file else None
    if state_path is not None and not state_path.exists():
        raise FileNotFoundError(f"storage_state {state_path} does not exist; "
                                "run `tramoya login` first")

    pacing = Pacing(pace=pace if pace is not None else profile.pace, rehearse=rehearse,
                    lang=lang or profile.languages[0])
    narrator = Narrator(out / "voices", pacing,
                        player=lambda clip: _SilentClip(wav_seconds(clip), pacing))
    marks = Marks(path=out / "marks.json")
    (out / "script.json").write_text(
        _json(profile.script()), encoding="utf-8",
    )

    if launch and ready and http_ready(ready, timeout=0.5):
        raise RuntimeError(f"{ready} already answers before launching {launch!r}; "
                           "something else is on that port (a stale server?). Stop it "
                           "or change the port in the profile")
    proc = launch_app(launch, profile.root) if launch else None
    try:
        if ready and not http_ready(ready, timeout=READY_TIMEOUT):
            raise RuntimeError(f"{profile.name} did not answer at {ready} "
                               f"within {READY_TIMEOUT:.0f}s")
        result = Result(out)
        with open_stage(target, pacing, marks, video_dir=raw, viewport=profile.viewport,
                        headless=headless, storage_state=state_path) as stage:
            marks.t0 = time.time()
            video_path = Path(stage.page.video.path())
            for scene in profile.scenes:
                def say(fallback: float, key: str = f"scene-{scene.name}") -> None:
                    narrator.say(key, fallback=fallback)

                with marks.scene(scene.name, scene.label):
                    stage.caption(scene.label)
                    try:
                        run_steps(stage, scene.steps, say, base_url=target or "")
                        write_snapshot(out / "snapshots", scene.name, stage.snapshot())
                    finally:
                        stage.caption("")
                result.scenes.append(scene.name)
    finally:
        _stop(proc)

    shutil.copyfile(video_path, out / "take.webm")
    return result


def _json(data: Any) -> str:
    import json

    return json.dumps(data, indent=2, ensure_ascii=False) + "\n"
