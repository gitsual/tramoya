"""The app profile: one `tramoya.toml` that names the app, how to reach it,
the languages, and the scenes as plain step lines. Reading, writing and the
step language are pure; running steps is exercised on a fake stage."""

from __future__ import annotations

from pathlib import Path

import pytest

from tramoya.profile import (
    Profile,
    Scene,
    StepError,
    dump_profile,
    load_profile,
    parse_step,
    run_steps,
)

SAMPLE = """\
[app]
name = "Notes"
url = "http://127.0.0.1:8765/index.html"
launch = "python -m http.server 8765"
viewport = [1280, 720]

[demo]
languages = ["en", "es"]
pace = 1.5

[[scene]]
name = "welcome"
label = "A small notes app"
steps = ["point h1", "say 6", "beat 1"]

[scene.narration]
en = "This is a small notes app."
es = "Esta es una pequeña app de notas."

[[scene]]
name = "search"
label = "Search filters as you type"
steps = ["type #search | re", "say 5"]

[scene.narration]
en = "Typing filters the list."
"""


def test_load_profile_reads_app_demo_and_scenes(tmp_path: Path) -> None:
    path = tmp_path / "tramoya.toml"
    path.write_text(SAMPLE, encoding="utf-8")
    profile = load_profile(path)
    assert profile.name == "Notes"
    assert profile.url == "http://127.0.0.1:8765/index.html"
    assert profile.launch == "python -m http.server 8765"
    assert profile.viewport == (1280, 720)
    assert profile.languages == ["en", "es"]
    assert profile.pace == 1.5
    assert [s.name for s in profile.scenes] == ["welcome", "search"]
    assert profile.scenes[0].narration["es"].startswith("Esta")
    assert profile.scenes[1].steps == ["type #search | re", "say 5"]
    assert profile.root == tmp_path


def test_load_profile_needs_a_url_or_a_launch(tmp_path: Path) -> None:
    path = tmp_path / "tramoya.toml"
    path.write_text('[app]\nname = "X"\n', encoding="utf-8")
    with pytest.raises(ValueError, match="url"):
        load_profile(path)


def test_dump_then_load_round_trips(tmp_path: Path) -> None:
    profile = Profile(
        name='Quotes "R" Us',
        url="http://localhost:3000/",
        languages=["en"],
        scenes=[Scene("one", "First", {"en": "Hello, \"world\"."}, ["click #go", "say 3"])],
    )
    path = tmp_path / "tramoya.toml"
    path.write_text(dump_profile(profile), encoding="utf-8")
    again = load_profile(path)
    assert again.name == profile.name
    assert again.scenes[0].narration == {"en": 'Hello, "world".'}
    assert again.scenes[0].steps == ["click #go", "say 3"]


def test_script_is_one_key_per_scene_with_the_scene_prefix() -> None:
    profile = Profile(name="N", url="http://x/", scenes=[
        Scene("a", "A", {"en": "one"}, []),
        Scene("b", "B", {"en": "two", "es": "dos"}, []),
    ])
    assert profile.script() == {"scene-a": {"en": "one"}, "scene-b": {"en": "two", "es": "dos"}}


@pytest.mark.parametrize("line, verb, args", [
    ("click #save", "click", ["#save"]),
    ("click_text New note", "click_text", ["New note"]),
    ("type #title | Water the plants", "type", ["#title", "Water the plants"]),
    ("select #tag | home", "select", ["#tag", "home"]),
    ("point h1", "point", ["h1"]),
    ("beat 1.5", "beat", ["1.5"]),
    ("say 6", "say", ["6"]),
    ("say", "say", []),
    ("scroll 300", "scroll", ["300"]),
    ("press Enter", "press", ["Enter"]),
    ("clear #search", "clear", ["#search"]),
    ("goto /settings", "goto", ["/settings"]),
])
def test_parse_step_splits_verb_and_arguments(line: str, verb: str, args: list[str]) -> None:
    step = parse_step(line)
    assert (step.verb, step.args) == (verb, args)


def test_parse_step_rejects_unknown_verbs_and_bad_arity() -> None:
    with pytest.raises(StepError, match="unknown"):
        parse_step("dance #floor")
    with pytest.raises(StepError, match="type"):
        parse_step("type #title")


class FakeStage:
    def __init__(self) -> None:
        self.calls: list[tuple] = []
        self.page = self

    # Stage verbs
    def point_at(self, s: str) -> None: self.calls.append(("point", s))
    def click(self, s: str) -> None: self.calls.append(("click", s))
    def click_text(self, t: str) -> None: self.calls.append(("click_text", t))
    def type_into(self, s: str, t: str, delay: int = 40) -> None: self.calls.append(("type", s, t))
    def select(self, s: str, v: str) -> None: self.calls.append(("select", s, v))
    def scroll(self, dy: int, dx: int = 0) -> None: self.calls.append(("scroll", dy))
    def beat(self, b: float = 1.0) -> None: self.calls.append(("beat", b))
    # Page verbs
    def fill(self, s: str, v: str) -> None: self.calls.append(("fill", s, v))
    def dispatch_event(self, s: str, e: str) -> None: self.calls.append(("event", s, e))
    def keyboard_press(self, k: str) -> None: self.calls.append(("press", k))
    def goto(self, u: str) -> None: self.calls.append(("goto", u))
    def wait_for(self, s: str, t: float) -> None: self.calls.append(("wait", s, t))

    @property
    def keyboard(self):
        stage = self

        class K:
            def press(self, k: str) -> None: stage.keyboard_press(k)

        return K()


def test_run_steps_drives_the_stage_in_order() -> None:
    stage = FakeStage()
    said: list[float] = []
    run_steps(stage, ["point h1", "type #q | re", "say 4", "clear #q", "press Enter",
                      "beat 2", "goto /x"], say=lambda fallback: said.append(fallback),
              base_url="http://app/")
    assert stage.calls == [
        ("point", "h1"), ("type", "#q", "re"), ("fill", "#q", ""), ("event", "#q", "input"),
        ("press", "Enter"), ("beat", 2.0), ("goto", "http://app/x"),
    ]
    assert said == [4.0]


def test_run_steps_wait_opens_a_wait_window_on_the_stage() -> None:
    stage = FakeStage()
    run_steps(stage, ["wait #listo | 180", "point #listo"], say=lambda fallback: None)
    assert stage.calls == [("wait", "#listo", 180.0), ("point", "#listo")]
    with pytest.raises(StepError):
        parse_step("wait #listo")


def test_run_steps_names_the_failing_line() -> None:
    with pytest.raises(StepError, match="line 2"):
        run_steps(FakeStage(), ["point h1", "jump"], say=lambda fallback: None)
