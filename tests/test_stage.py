"""Tests for tramoya.stage: the overlay script, the Stage hand, the scene
registry and the app-under-test process, all without importing playwright."""

from __future__ import annotations

import builtins
import sys
import urllib.error

import pytest

from tramoya.marks import Marks
from tramoya.pacing import Pacing
from tramoya.stage import (
    OVERLAY_JS,
    AppProcess,
    SceneRegistry,
    Stage,
    http_ready,
    open_stage,
)


def test_overlay_js_installs_tramoya_ids_and_keeps_caption_non_interactive():
    assert "#tramoya-cursor" in OVERLAY_JS
    assert "#tramoya-ripple" in OVERLAY_JS
    assert "#tramoya-caption" in OVERLAY_JS
    assert "window.__tramoyaCaption" in OVERLAY_JS
    assert "pointer-events: none" in OVERLAY_JS
    assert "mousemove" in OVERLAY_JS
    assert "mousedown" in OVERLAY_JS


# --------------------------------------------------------------------- Stage


_DEFAULT_BOX = {"x": 10, "y": 20, "width": 100, "height": 40}
_NO_BOX = object()


class FakeLocator:
    def __init__(self, page: FakeBoundPage, selector: str, box=_NO_BOX) -> None:
        self.page = page
        self.selector = selector
        self._box = _DEFAULT_BOX if box is _NO_BOX else box
        self.calls: list[str] = []

    def wait_for(self, state: str = "visible", timeout: int = 15000) -> None:
        self.calls.append(f"wait_for:{state}")

    def scroll_into_view_if_needed(self) -> None:
        self.calls.append("scroll_into_view_if_needed")

    def bounding_box(self):
        self.calls.append("bounding_box")
        return self._box

    def click(self) -> None:
        self.calls.append("click")
        self.page.clicked.append(self.selector)

    def type(self, text: str, delay: int = 0) -> None:
        self.calls.append(f"type:{text}:{delay}")
        self.page.typed.append((self.selector, text))

    def select_option(self, value: str) -> None:
        self.calls.append(f"select_option:{value}")
        self.page.selected.append((self.selector, value))

    def inner_text(self) -> str:
        return "some text"


class FakeMouse:
    def __init__(self) -> None:
        self.moves: list[tuple[float, float, int]] = []
        self.wheels: list[tuple[int, int]] = []

    def move(self, x: float, y: float, steps: int = 1) -> None:
        self.moves.append((x, y, steps))

    def wheel(self, dx: int, dy: int) -> None:
        self.wheels.append((dx, dy))


class FakeBoundPage:
    """A minimal stand-in for a playwright Page, recording every call a
    Stage makes so the tests can assert on behaviour without a browser."""

    def __init__(self) -> None:
        self.mouse = FakeMouse()
        self.waits: list[int] = []
        self.evaluated: list[tuple[str, object]] = []
        self.clicked: list[str] = []
        self.typed: list[tuple[str, str]] = []
        self.selected: list[tuple[str, str]] = []
        self.locators: dict[str, FakeLocator] = {}
        self.text_locators: dict[str, FakeLocator] = {}

    def wait_for_timeout(self, ms: int) -> None:
        self.waits.append(ms)

    def evaluate(self, script: str, arg=None) -> None:
        self.evaluated.append((script, arg))

    def locator(self, selector: str) -> _First:
        loc = self.locators.setdefault(selector, FakeLocator(self, selector))
        return _First(loc)

    def get_by_text(self, text: str, exact: bool = True) -> _First:
        loc = self.text_locators.setdefault(text, FakeLocator(self, text))
        return _First(loc)


class _First:
    """playwright locators are chained with `.first`; this stands in for
    that so `page.locator(sel).first` returns the same fake locator."""

    def __init__(self, locator: FakeLocator) -> None:
        self.first = locator


def _stage(page=None, pace: float = 1.0) -> tuple[Stage, FakeBoundPage]:
    page = page or FakeBoundPage()
    pacing = Pacing(pace=pace, sleep=lambda _s: None)
    marks = Marks(t0=0.0, clock=lambda: 0.0)
    return Stage(page, pacing, marks, viewport=(1600, 900)), page


def test_stage_beat_sleeps_scaled_by_pacing():
    slept: list[float] = []
    pacing = Pacing(pace=1.0, sleep=slept.append)
    marks = Marks(t0=0.0, clock=lambda: 0.0)
    stage = Stage(FakeBoundPage(), pacing, marks, viewport=(1600, 900))
    stage.beat(2.0)
    assert slept == [2.0]  # pace=1.0


def test_stage_caption_calls_the_overlay_hook():
    stage, page = _stage()
    stage.caption("hello")
    assert page.evaluated[-1][1] == "hello"


def test_stage_move_to_xy_moves_the_mouse_and_tracks_position():
    stage, page = _stage()
    stage.move_to_xy(50, 60)
    assert page.mouse.moves[-1][:2] == (50, 60)
    assert stage._x == 50
    assert stage._y == 60


def test_stage_point_at_locates_scrolls_and_moves_to_the_box_center():
    stage, page = _stage()
    stage.point_at("#thing")
    loc = page.locators["#thing"]
    assert "scroll_into_view_if_needed" in loc.calls
    assert "bounding_box" in loc.calls
    # box: x=10,y=20,w=100,h=40 -> center (60, 40)
    assert page.mouse.moves[-1][:2] == (60, 40)


def test_stage_point_at_raises_when_locator_has_no_box():
    page = FakeBoundPage()
    page.locators["#ghost"] = FakeLocator(page, "#ghost", box=None)
    stage, _ = _stage(page)
    with pytest.raises(RuntimeError):
        stage.point_at("#ghost")


def test_stage_click_clicks_the_pointed_at_element():
    stage, page = _stage()
    stage.click("#button", note="press it")
    assert page.clicked == ["#button"]


def test_stage_click_text_clicks_matching_text():
    stage, page = _stage()
    stage.click_text("Submit")
    assert page.clicked == ["Submit"]


def test_stage_click_text_raises_when_no_box():
    page = FakeBoundPage()
    page.text_locators["Ghost"] = FakeLocator(page, "Ghost", box=None)
    stage, _ = _stage(page)
    with pytest.raises(RuntimeError):
        stage.click_text("Ghost")


def test_stage_type_into_clicks_then_types():
    stage, page = _stage()
    stage.type_into("#field", "hi")
    assert page.clicked == ["#field"]
    assert page.typed == [("#field", "hi")]


def test_stage_select_chooses_the_option():
    stage, page = _stage()
    stage.select("#dropdown", "value-a")
    assert page.selected == [("#dropdown", "value-a")]


def test_stage_scroll_wheels_the_page():
    stage, page = _stage()
    stage.scroll(300)
    assert page.mouse.wheels == [(0, 300)]


def test_stage_read_returns_inner_text():
    stage, _page = _stage()
    assert stage.read("#thing") == "some text"


def test_stage_read_returns_empty_string_when_it_raises():
    page = FakeBoundPage()

    class RaisingLocator(FakeLocator):
        def wait_for(self, state="visible", timeout=15000):
            raise TimeoutError("nope")

    page.locators["#missing"] = RaisingLocator(page, "#missing")
    stage, _ = _stage(page)
    assert stage.read("#missing") == ""


# ------------------------------------------------------------- SceneRegistry


def test_scene_registry_runs_scenes_in_registration_order():
    registry = SceneRegistry()
    order: list[str] = []

    @registry.scene("intro", "Intro")
    def intro(stage, base):
        order.append("intro")

    @registry.scene("outro", "Outro")
    def outro(stage, base):
        order.append("outro")

    stage, _page = _stage()
    registry.run_all(stage, "http://base")
    assert order == ["intro", "outro"]


def test_scene_registry_names_and_order():
    registry = SceneRegistry()

    @registry.scene("a", "A")
    def a(stage, base):
        pass

    @registry.scene("b", "B")
    def b(stage, base):
        pass

    assert registry.names() == ["a", "b"]
    assert registry.order == ["a", "b"]


def test_scene_registry_wraps_marks_and_captions_around_the_scene():
    registry = SceneRegistry()

    @registry.scene("only", "Only scene")
    def only(stage, base):
        pass

    stage, page = _stage()
    registry.run_all(stage, "http://base")
    keys = [m.key for m in stage.marks.rows]
    assert keys == ["scene:only:start", "scene:only:end"]
    # caption turned on then off
    captions = [arg for _script, arg in page.evaluated]
    assert captions == ["Only scene", ""]


def test_scene_registry_raises_on_empty_registry_when_run():
    registry = SceneRegistry()
    stage, _ = _stage()
    with pytest.raises(RuntimeError):
        registry.run_all(stage, "http://base")


# ------------------------------------------------------------------ http_ready


def test_http_ready_true_when_urlopen_succeeds(monkeypatch):
    class Resp:
        status = 200

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    monkeypatch.setattr("urllib.request.urlopen", lambda url, timeout=2.0: Resp())
    assert http_ready("http://127.0.0.1:9/", timeout=1.0) is True


def test_http_ready_false_when_it_never_answers(monkeypatch):
    def boom(url, timeout=2.0):
        raise urllib.error.URLError("refused")

    monkeypatch.setattr("urllib.request.urlopen", boom)
    monkeypatch.setattr("time.sleep", lambda s: None)
    assert http_ready("http://127.0.0.1:9/", timeout=0.01) is False


# ------------------------------------------------------------------ AppProcess


class _FakeCompletedProcess:
    def __init__(self, returncode=0, stderr=""):
        self.returncode = returncode
        self.stderr = stderr


class _FakePopen:
    def __init__(self, argv, cwd=None, stdout=None, stderr=None):
        self.argv = argv
        self.cwd = cwd
        self.sent = []
        self.waited = False

    def send_signal(self, sig):
        self.sent.append(sig)

    def wait(self, timeout=None):
        self.waited = True

    def terminate(self):
        self.sent.append("terminate")

    def kill(self):
        self.sent.append("kill")


def test_app_process_start_clones_and_launches(monkeypatch, tmp_path):
    calls = []

    def fake_run(argv, capture_output=True, text=True):
        calls.append(argv)
        return _FakeCompletedProcess(returncode=0)

    monkeypatch.setattr("subprocess.run", fake_run)
    monkeypatch.setattr("subprocess.Popen", _FakePopen)
    monkeypatch.setattr("tempfile.mkdtemp", lambda prefix="": str(tmp_path))

    app = AppProcess("https://forge/repo.git", "main", 8765, python="python3", entry="app.py")
    app.start()

    assert calls[0][:2] == ["git", "-c"]
    assert isinstance(app.proc, _FakePopen)
    assert app.proc.argv[0] == "python3"
    assert str(tmp_path) in app.proc.argv[1]


def test_app_process_start_raises_when_clone_fails(monkeypatch, tmp_path):
    def fake_run(argv, capture_output=True, text=True):
        return _FakeCompletedProcess(returncode=1, stderr="fatal: not found")

    monkeypatch.setattr("subprocess.run", fake_run)
    monkeypatch.setattr("tempfile.mkdtemp", lambda prefix="": str(tmp_path))

    app = AppProcess("https://forge/repo.git", "main", 8765)
    with pytest.raises(RuntimeError):
        app.start()


def test_app_process_stop_sends_sigint_and_waits(monkeypatch, tmp_path):
    monkeypatch.setattr(
        "subprocess.run", lambda argv, capture_output=True, text=True: _FakeCompletedProcess(0)
    )
    monkeypatch.setattr("subprocess.Popen", _FakePopen)
    monkeypatch.setattr("tempfile.mkdtemp", lambda prefix="": str(tmp_path))

    app = AppProcess("https://forge/repo.git", "main", 8765)
    app.start()
    proc = app.proc
    app.stop()
    assert proc.waited is True
    assert proc.sent


def test_app_process_context_manager_stops_on_exit(monkeypatch, tmp_path):
    monkeypatch.setattr(
        "subprocess.run", lambda argv, capture_output=True, text=True: _FakeCompletedProcess(0)
    )
    monkeypatch.setattr("subprocess.Popen", _FakePopen)
    monkeypatch.setattr("tempfile.mkdtemp", lambda prefix="": str(tmp_path))

    app = AppProcess("https://forge/repo.git", "main", 8765)
    with app:
        app.start()
        proc = app.proc
    assert proc.waited is True


# ------------------------------------------------------------------- open_stage


def test_open_stage_raises_clear_error_without_playwright(monkeypatch):
    real_import = builtins.__import__

    def fake_import(name, *args, **kwargs):
        if name == "playwright.sync_api" or name.startswith("playwright"):
            raise ImportError("no module named playwright")
        return real_import(name, *args, **kwargs)

    monkeypatch.setattr(builtins, "__import__", fake_import)
    for mod in list(sys.modules):
        if mod.startswith("playwright"):
            monkeypatch.delitem(sys.modules, mod, raising=False)

    pacing = Pacing(sleep=lambda _s: None)
    marks = Marks(t0=0.0, clock=lambda: 0.0)

    with pytest.raises(ImportError, match="playwright"):
        with open_stage("http://base", pacing, marks):
            pass


def test_overlay_installs_when_page_has_a_body() -> None:
    """Init scripts run before `document.documentElement` exists; a real
    browser once threw `appendChild of null` and the caption hook was never
    defined. The overlay must defer until the document is there."""
    playwright = pytest.importorskip("playwright.sync_api")
    with playwright.sync_playwright() as p:
        browser = p.chromium.launch()
        context = browser.new_context()
        context.add_init_script(OVERLAY_JS)
        page = context.new_page()
        errors: list[str] = []
        page.on("pageerror", lambda e: errors.append(str(e)))
        page.goto("data:text/html,<h1>hello</h1>")
        assert page.evaluate("typeof window.__tramoyaCaption") == "function"
        page.evaluate("window.__tramoyaCaption('scene one')")
        assert page.evaluate("document.getElementById('tramoya-caption').textContent") == "scene one"
        assert errors == []
        browser.close()
