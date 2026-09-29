"""`tramoya direct tramoya.toml`: the generic director reads a profile,
brings the app up, runs every scene through the step language, writes the
take, the marks, the narration script and one accessibility snapshot per
scene. The browser and the app process are doubles here."""

from __future__ import annotations

import json
from contextlib import contextmanager
from pathlib import Path

import pytest

from tramoya import director
from tramoya.profile import Profile, Scene
from tramoya.snapshots import diff_snapshots, write_snapshot


class FakeVideo:
    def __init__(self, path: Path) -> None:
        self._path = path

    def path(self) -> str:
        return str(self._path)


class FakePage:
    def __init__(self, video: Path) -> None:
        self.video = FakeVideo(video)
        self.log: list[str] = []
        self.tree = "- heading \"Notes\"\n- button \"New note\""

    def locator(self, sel: str):
        page = self

        class L:
            first = None

            def aria_snapshot(self) -> str:
                return page.tree

        return L()


class FakeStage:
    def __init__(self, page: FakePage) -> None:
        self.page = page
        self.marks = None

    def point_at(self, s: str) -> None: self.page.log.append(f"point {s}")
    def click(self, s: str) -> None: self.page.log.append(f"click {s}")
    def click_text(self, t: str) -> None: self.page.log.append(f"click_text {t}")
    def type_into(self, s: str, t: str, delay: int = 40) -> None: self.page.log.append(f"type {s}")
    def select(self, s: str, v: str) -> None: self.page.log.append(f"select {s}")
    def scroll(self, dy: int, dx: int = 0) -> None: self.page.log.append(f"scroll {dy}")
    def beat(self, b: float = 1.0) -> None: self.page.log.append(f"beat {b}")
    def caption(self, t: str) -> None: self.page.log.append(f"caption {t!r}")

    def snapshot(self) -> str:
        return self.page.locator("body").aria_snapshot()


@pytest.fixture
def fake_browser(monkeypatch: pytest.MonkeyPatch, tmp_path: Path):
    """open_stage double: yields a FakeStage whose video 'file' exists."""
    video = tmp_path / "raw" / "abc.webm"
    opened: dict = {}

    @contextmanager
    def open_stage(url, pacing, marks, video_dir=None, viewport=(1600, 900), headless=True,
                   storage_state=None):
        opened.update(url=url, video_dir=video_dir, viewport=viewport, headless=headless,
                      storage_state=storage_state)
        video.parent.mkdir(parents=True, exist_ok=True)
        video.write_bytes(b"webm")
        page = FakePage(video)
        stage = FakeStage(page)
        stage.marks = marks
        opened["page"] = page
        yield stage

    monkeypatch.setattr(director, "open_stage", open_stage)
    # Nothing answers before the app is launched (the short pre-launch probe);
    # the app answers once it is being waited for.
    monkeypatch.setattr(director, "http_ready", lambda url, timeout=20.0: timeout > 1)
    return opened


def _profile(tmp_path: Path, **kw) -> Profile:
    return Profile(
        name="Notes", url="http://127.0.0.1:1/index.html", languages=["en", "es"],
        scenes=[
            Scene("welcome", "A small notes app", {"en": "Hi", "es": "Hola"},
                  ["point h1", "say 2", "beat 0.1"]),
            Scene("search", "Search", {"en": "Type"}, ["type #q | re", "say"]),
        ], root=tmp_path, **kw,
    )


def test_run_profile_writes_take_marks_script_and_snapshots(tmp_path: Path, fake_browser) -> None:
    out = tmp_path / "out"
    result = director.run_profile(_profile(tmp_path), out, rehearse=True)
    assert (out / "take.webm").read_bytes() == b"webm"
    marks = json.loads((out / "marks.json").read_text())
    keys = [m["key"] for m in marks]
    assert keys == ["scene:welcome:start", "scene:welcome:end",
                    "scene:search:start", "scene:search:end"]
    script = json.loads((out / "script.json").read_text())
    assert script == {"scene-welcome": {"en": "Hi", "es": "Hola"}, "scene-search": {"en": "Type"}}
    assert sorted(p.name for p in (out / "snapshots").iterdir()) == ["search.txt", "welcome.txt"]
    assert "New note" in (out / "snapshots" / "welcome.txt").read_text()
    assert result.scenes == ["welcome", "search"]
    assert fake_browser["url"] == "http://127.0.0.1:1/index.html"
    assert fake_browser["viewport"] == (1280, 720)
    log = fake_browser["page"].log
    assert log.index("caption 'A small notes app'") < log.index("point h1")


def test_run_profile_launches_the_app_and_waits_for_it(
    tmp_path: Path, fake_browser, monkeypatch: pytest.MonkeyPatch
) -> None:
    started: list[tuple[str, Path]] = []
    stopped: list[bool] = []

    class FakeProc:
        def __init__(self, cmd: str, cwd: Path) -> None:
            started.append((cmd, cwd))

        def terminate(self) -> None: stopped.append(True)
        def wait(self, timeout: float = 0) -> None: pass
        def kill(self) -> None: pass
        def poll(self) -> None: return None

    monkeypatch.setattr(director, "launch_app", lambda cmd, cwd: FakeProc(cmd, cwd))
    profile = _profile(tmp_path, launch="python -m http.server 1", ready="http://127.0.0.1:1/")
    director.run_profile(profile, tmp_path / "out", rehearse=True)
    assert started == [("python -m http.server 1", tmp_path)]
    assert stopped == [True]


def test_run_profile_fails_clearly_when_the_app_never_answers(
    tmp_path: Path, fake_browser, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(director, "http_ready", lambda url, timeout=20.0: False)
    with pytest.raises(RuntimeError, match="did not answer"):
        director.run_profile(_profile(tmp_path), tmp_path / "out", rehearse=True)


def test_run_profile_overrides_url_and_storage_state(tmp_path: Path, fake_browser) -> None:
    profile = _profile(tmp_path, storage_state="auth.json")
    (tmp_path / "auth.json").write_text("{}")
    director.run_profile(profile, tmp_path / "out", rehearse=True, url="http://other/")
    assert fake_browser["url"] == "http://other/"
    assert fake_browser["storage_state"] == tmp_path / "auth.json"


def test_run_profile_needs_scenes(tmp_path: Path, fake_browser) -> None:
    profile = _profile(tmp_path)
    profile.scenes = []
    with pytest.raises(ValueError, match="no scenes"):
        director.run_profile(profile, tmp_path / "out", rehearse=True)


def test_plan_lists_scenes_and_steps_without_a_browser(tmp_path: Path) -> None:
    text = director.describe(_profile(tmp_path))
    assert "welcome" in text and "point h1" in text and "2 scenes" in text


# --- snapshots ------------------------------------------------------------


def test_snapshot_diff_reports_changed_missing_and_new_scenes(tmp_path: Path) -> None:
    a, b = tmp_path / "a", tmp_path / "b"
    write_snapshot(a, "welcome", "- heading \"Notes\"\n- button \"New note\"\n")
    write_snapshot(a, "search", "- textbox\n")
    write_snapshot(a, "gone", "- x\n")
    write_snapshot(b, "welcome", "- heading \"Notes\"\n- button \"New\"\n")
    write_snapshot(b, "search", "- textbox\n")
    write_snapshot(b, "extra", "- y\n")
    problems = diff_snapshots(a, b)
    assert any("welcome" in p and "New note" in p for p in problems)
    assert any("gone" in p and "missing" in p for p in problems)
    assert any("extra" in p and "new" in p for p in problems)
    assert not any("search" in p for p in problems)
    assert diff_snapshots(a, a) == []


def test_run_profile_refuses_to_launch_when_something_already_answers(
    tmp_path: Path, fake_browser, monkeypatch: pytest.MonkeyPatch
) -> None:
    """A stale server (or someone else's) on the same port would hand the
    demo the wrong page; better to stop before launching anything."""
    started: list[str] = []
    monkeypatch.setattr(director, "launch_app", lambda cmd, cwd: started.append(cmd))
    monkeypatch.setattr(director, "http_ready", lambda url, timeout=20.0: True)
    profile = _profile(tmp_path, launch="python -m http.server 1", ready="http://127.0.0.1:1/")
    with pytest.raises(RuntimeError, match="already answers"):
        director.run_profile(profile, tmp_path / "out", rehearse=True)
    assert started == []
