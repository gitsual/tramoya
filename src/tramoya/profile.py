"""The app profile: `tramoya.toml`.

One file names the application, how to reach it, the narration languages and
the scenes. A scene is a label, its narration per language and a list of
step lines in a tiny verb language (`click #save`, `type #title | Hello`).
Nothing here needs Python from the user: `tramoya direct tramoya.toml` reads
this file and drives the stage from it.

Reading uses the standard library's `tomllib`; writing is done by hand
because the standard library has no TOML writer and the shape is small.
"""

from __future__ import annotations

import tomllib
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

DEFAULT_VIEWPORT = (1280, 720)


@dataclass
class Scene:
    name: str
    label: str
    narration: dict[str, str] = field(default_factory=dict)
    steps: list[str] = field(default_factory=list)


@dataclass
class Profile:
    name: str
    url: str | None = None
    launch: str | None = None
    ready: str | None = None
    viewport: tuple[int, int] = DEFAULT_VIEWPORT
    storage_state: str | None = None
    languages: list[str] = field(default_factory=lambda: ["en"])
    pace: float = 1.0
    scenes: list[Scene] = field(default_factory=list)
    root: Path = field(default_factory=Path)

    def script(self) -> dict[str, dict[str, str]]:
        """The narration script `tramoya tts` reads: key -> {lang: text}."""
        return {f"scene-{s.name}": dict(s.narration) for s in self.scenes}


def load_profile(path: str | Path) -> Profile:
    path = Path(path)
    raw: dict[str, Any] = tomllib.loads(path.read_text(encoding="utf-8"))
    app = raw.get("app", {})
    demo = raw.get("demo", {})
    url, launch = app.get("url"), app.get("launch")
    if not url and not launch:
        raise ValueError(f"{path}: [app] needs a url, or a launch command with a ready url")
    if launch and not (url or app.get("ready")):
        raise ValueError(f"{path}: [app] launch needs a url or a ready url to wait for")
    viewport = app.get("viewport", list(DEFAULT_VIEWPORT))
    scenes = [
        Scene(
            name=str(s["name"]),
            label=str(s.get("label", s["name"])),
            narration={str(k): str(v) for k, v in s.get("narration", {}).items()},
            steps=[str(line) for line in s.get("steps", [])],
        )
        for s in raw.get("scene", [])
    ]
    return Profile(
        name=str(app.get("name", path.parent.name or "app")),
        url=url,
        launch=launch,
        ready=app.get("ready"),
        viewport=(int(viewport[0]), int(viewport[1])),
        storage_state=app.get("storage_state"),
        languages=[str(x) for x in demo.get("languages", ["en"])],
        pace=float(demo.get("pace", 1.0)),
        scenes=scenes,
        root=path.parent,
    )


def _q(text: str) -> str:
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def dump_profile(profile: Profile) -> str:
    lines = ["[app]", f"name = {_q(profile.name)}"]
    if profile.url:
        lines.append(f"url = {_q(profile.url)}")
    if profile.launch:
        lines.append(f"launch = {_q(profile.launch)}")
    if profile.ready:
        lines.append(f"ready = {_q(profile.ready)}")
    lines.append(f"viewport = [{profile.viewport[0]}, {profile.viewport[1]}]")
    if profile.storage_state:
        lines.append(f"storage_state = {_q(profile.storage_state)}")
    lines += ["", "[demo]",
              "languages = [" + ", ".join(_q(x) for x in profile.languages) + "]",
              f"pace = {profile.pace}"]
    for scene in profile.scenes:
        lines += ["", "[[scene]]", f"name = {_q(scene.name)}", f"label = {_q(scene.label)}",
                  "steps = [" + ", ".join(_q(s) for s in scene.steps) + "]"]
        if scene.narration:
            lines += ["", "[scene.narration]"]
            lines += [f"{lang} = {_q(text)}" for lang, text in scene.narration.items()]
    return "\n".join(lines) + "\n"


# --- the step language ----------------------------------------------------


class StepError(ValueError):
    pass


@dataclass
class Step:
    verb: str
    args: list[str]


# verb -> (min args, max args, help)
VERBS: dict[str, tuple[int, int, str]] = {
    "click": (1, 1, "click <selector>"),
    "click_text": (1, 1, "click_text <visible text>"),
    "type": (2, 2, "type <selector> | <text>"),
    "select": (2, 2, "select <selector> | <value>"),
    "clear": (1, 1, "clear <selector>"),
    "point": (1, 1, "point <selector>"),
    "scroll": (1, 1, "scroll <pixels>"),
    "press": (1, 1, "press <key>"),
    "goto": (1, 1, "goto <path or url>"),
    "beat": (1, 1, "beat <seconds>"),
    "say": (0, 1, "say [fallback seconds]  (plays this scene's narration)"),
}


def parse_step(line: str) -> Step:
    text = line.strip()
    verb, _, rest = text.partition(" ")
    if verb not in VERBS:
        known = ", ".join(sorted(VERBS))
        raise StepError(f"unknown step {verb!r} in {line!r}; known: {known}")
    lo, hi, usage = VERBS[verb]
    rest = rest.strip()
    if hi == 2:
        args = [part.strip() for part in rest.split("|", 1)] if rest else []
    else:
        args = [rest] if rest else []
    if not lo <= len(args) <= hi:
        raise StepError(f"{verb} takes {usage}, got {line!r}")
    return Step(verb, args)


def run_steps(
    stage: Any,
    steps: list[str],
    say: Callable[[float], Any],
    base_url: str = "",
) -> None:
    """Drive `stage` (a `tramoya.stage.Stage` or a double) through `steps`.
    `say(fallback)` plays the scene's narration; the caller binds the key."""
    for number, line in enumerate(steps, 1):
        try:
            step = parse_step(line)
        except StepError as exc:
            raise StepError(f"line {number}: {exc}") from exc
        a = step.args
        if step.verb == "click":
            stage.click(a[0])
        elif step.verb == "click_text":
            stage.click_text(a[0])
        elif step.verb == "type":
            stage.type_into(a[0], a[1])
        elif step.verb == "select":
            stage.select(a[0], a[1])
        elif step.verb == "clear":
            stage.page.fill(a[0], "")
            stage.page.dispatch_event(a[0], "input")
        elif step.verb == "point":
            stage.point_at(a[0])
        elif step.verb == "scroll":
            stage.scroll(int(float(a[0])))
        elif step.verb == "press":
            stage.page.keyboard.press(a[0])
        elif step.verb == "goto":
            target = a[0]
            if base_url and not target.startswith(("http://", "https://")):
                target = base_url.rstrip("/") + "/" + target.lstrip("/")
            stage.page.goto(target)
        elif step.verb == "beat":
            stage.beat(float(a[0]))
        elif step.verb == "say":
            say(float(a[0]) if a else 0.0)
