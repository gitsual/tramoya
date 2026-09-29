"""`tramoya init`: the questions that turn an app into a profile.

The interview is linear and every question has a matching flag, so the same
run works on a terminal (`TtyAnswers`), from flags in CI (`FlagAnswers`) and
in tests (`ScriptedAnswers`). The wizard never asks for a password: a demo
behind a sign-in is handled by `tramoya login`, which saves a browser
session file the profile then points at.
"""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Protocol

from tramoya.profile import Profile, Scene, dump_profile

SESSION_FILE = ".tramoya/auth.json"


class MissingAnswer(ValueError):
    """A non-interactive run had no answer for a question; names the flag."""


class Answers(Protocol):
    def ask(self, key: str, question: str, default: str = "") -> str: ...
    def warn(self, text: str) -> None: ...


class ScriptedAnswers:
    """Answers in order, for tests; records warnings instead of printing."""

    def __init__(self, replies: list[str]) -> None:
        self._replies = list(replies)
        self.warnings: list[str] = []

    def ask(self, key: str, question: str, default: str = "") -> str:
        if not self._replies:
            raise MissingAnswer(f"no scripted answer for {key}")
        reply = self._replies.pop(0)
        return reply if reply != "" or key == "scene" else default

    def warn(self, text: str) -> None:
        self.warnings.append(text)

    @property
    def exhausted(self) -> bool:
        return not self._replies


class TtyAnswers:
    """Ask on the terminal; a blank reply takes the default."""

    def ask(self, key: str, question: str, default: str = "") -> str:
        hint = f" [{default}]" if default else ""
        reply = input(f"{question}{hint}: ").strip()
        return reply or default

    def warn(self, text: str) -> None:
        print(f"  ! {text}")


_FLAG_OF = {"name": "--name", "reach": "--url or --launch", "url": "--url or --launch",
            "launch": "--launch", "ready": "--url", "login": "--login",
            "languages": "--lang", "scene": "--scene"}


class FlagAnswers:
    """Answers taken from flags; a question with no flag is an error, not a
    prompt, so CI fails naming what to pass."""

    def __init__(
        self,
        name: str | None = None,
        url: str | None = None,
        launch: str | None = None,
        login: bool = False,
        languages: str | None = None,
        scenes: list[str] | None = None,
    ) -> None:
        self.values: dict[str, str] = {}
        if name:
            self.values["name"] = name
        if launch:
            self.values["reach"], self.values["launch"] = "launch", launch
            if url:
                self.values["ready"] = url
        elif url:
            self.values["reach"], self.values["url"] = "url", url
        self.values["login"] = "y" if login else "n"
        if languages:
            self.values["languages"] = languages
        self._scenes = list(scenes or [])
        self._asked_scene = False
        self.warnings: list[str] = []

    def ask(self, key: str, question: str, default: str = "") -> str:
        if key == "scene":
            if self._scenes:
                self._asked_scene = True
                return self._scenes.pop(0)
            if not self._asked_scene:
                raise MissingAnswer("non-interactive init needs at least one --scene "
                                    '"name: label"')
            return ""
        if key in self.values:
            return self.values[key]
        if default:
            return default
        raise MissingAnswer(f"non-interactive init needs {_FLAG_OF.get(key, key)}")

    def warn(self, text: str) -> None:
        self.warnings.append(text)


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-") or "scene"


def _parse_scene(reply: str) -> tuple[str, str]:
    name, sep, label = reply.partition(":")
    label = label.strip() if sep else name.strip()
    return slug(name), label or name.strip()


def interview(answers: Answers) -> Profile:
    name = answers.ask("name", "What is the app called")
    reach = answers.ask("reach", "Is the app already running (url) or should tramoya "
                                 "launch it (launch)", "url")
    launch = url = None
    if reach.strip().lower().startswith("l"):
        launch = answers.ask("launch", "Command that starts the app, run in this folder")
        url = answers.ask("ready", "URL to open once it answers")
    else:
        url = answers.ask("url", "URL of the page to open")
    login = answers.ask("login", "Does the demo need to be signed in (y/n)", "n")
    storage_state = SESSION_FILE if login.strip().lower().startswith("y") else None
    langs = answers.ask("languages", "Narration languages, comma separated", "en")
    languages = [x.strip() for x in langs.split(",") if x.strip()] or ["en"]

    scenes: list[Scene] = []
    seen: set[str] = set()
    while True:
        reply = answers.ask("scene", f"Scene {len(scenes) + 1} as \"name: what it shows\" "
                                     "(blank to finish)")
        if not reply.strip():
            break
        scene_name, label = _parse_scene(reply)
        if scene_name in seen:
            answers.warn(f"scene {scene_name!r} already exists; skipped")
            continue
        seen.add(scene_name)
        scenes.append(Scene(scene_name, label, dict.fromkeys(languages, ""), ["say"]))
    if not scenes:
        raise MissingAnswer("a demo needs at least one scene (--scene \"name: label\")")
    return Profile(name=name, url=url, launch=launch, storage_state=storage_state,
                   languages=languages, scenes=scenes)


MAKEFILE = """\
# Generated by `tramoya init`. Three targets, one video.
OUT ?= out
LANG ?= {lang}

.PHONY: take voices video all
all: video

take:
\ttramoya direct tramoya.toml --out $(OUT)

voices: take
{voices}
video: voices
\ttramoya assemble --video $(OUT)/take.webm --marks $(OUT)/marks.json \\
\t  --voice-dir $(OUT)/voices/$(LANG) --out $(OUT)/{slug}-$(LANG).mp4 --lang $(LANG)
"""


def scaffold(profile: Profile, directory: Path, force: bool = False) -> list[Path]:
    """Write tramoya.toml, script.json and a Makefile into `directory`."""
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    targets = {
        "tramoya.toml": dump_profile(profile),
        "script.json": json.dumps(profile.script(), indent=2, ensure_ascii=False) + "\n",
        "Makefile": MAKEFILE.format(
            lang=profile.languages[0], slug=slug(profile.name),
            voices="\n".join(
                f"\ttramoya tts --script $(OUT)/script.json --lang {lang} "
                f"--out-dir $(OUT)/voices/{lang}"
                for lang in profile.languages
            ),
        ),
    }
    clashes = [n for n in targets if (directory / n).exists()]
    if clashes and not force:
        raise FileExistsError(f"{', '.join(clashes)} already exist in {directory}; "
                              "pass --force to overwrite")
    written: list[Path] = []
    for name, text in targets.items():
        path = directory / name
        path.write_text(text, encoding="utf-8")
        written.append(path)
    return written
