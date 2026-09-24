"""The stage: a thin, testable hand over a browser page, a scene registry
that never forgets its marks and captions, and the app-under-test process
that the stage points at.

playwright is an optional extra: nothing at module import time touches it.
`Stage` and `SceneRegistry` are exercised in tests with a fake page double
that looks like a playwright `Page`; only `open_stage` imports playwright,
and only when it is actually called.
"""

from __future__ import annotations

import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from collections.abc import Callable, Iterator
from contextlib import contextmanager
from pathlib import Path
from typing import Any

from tramoya.marks import Marks
from tramoya.pacing import Pacing

OVERLAY_JS = """
// ids: #tramoya-cursor, #tramoya-ripple, #tramoya-caption
(() => {
  // Init scripts run before <html> exists; wait for it, then install once.
  const install = () => {
  const cursor = document.createElement("div");
  cursor.id = "tramoya-cursor";
  cursor.style.cssText = [
    "position: fixed", "z-index: 2147483647", "width: 18px", "height: 18px",
    "border: 2px solid #ff5f5f", "border-radius: 50%", "pointer-events: none",
    "transform: translate(-50%, -50%)", "left: -100px", "top: -100px",
    "transition: left 0.05s linear, top 0.05s linear",
  ].join(";");
  document.documentElement.appendChild(cursor);

  const caption = document.createElement("div");
  caption.id = "tramoya-caption";
  caption.style.cssText = [
    "position: fixed", "z-index: 2147483647", "left: 0", "right: 0", "bottom: 0",
    "pointer-events: none", "text-align: center", "padding: 18px",
    "font: 20px sans-serif", "color: #fff",
    "background: linear-gradient(transparent, rgba(0,0,0,0.6))",
    "opacity: 0", "transition: opacity 0.2s ease",
  ].join(";");
  document.documentElement.appendChild(caption);

  window.__tramoyaCaption = (text) => {
    caption.textContent = text || "";
    caption.style.opacity = text ? "1" : "0";
  };

  function ripple(x, y) {
    const ring = document.createElement("div");
    ring.id = "tramoya-ripple";
    ring.style.cssText = [
      "position: fixed", "z-index: 2147483647", "width: 10px", "height: 10px",
      "border: 2px solid #ff5f5f", "border-radius: 50%", "pointer-events: none",
      `left: ${x}px`, `top: ${y}px`, "transform: translate(-50%, -50%) scale(1)",
      "opacity: 1", "transition: transform 0.4s ease, opacity 0.4s ease",
    ].join(";");
    document.documentElement.appendChild(ring);
    requestAnimationFrame(() => {
      ring.style.transform = "translate(-50%, -50%) scale(3)";
      ring.style.opacity = "0";
    });
    setTimeout(() => ring.remove(), 400);
  }

  window.addEventListener("mousemove", (e) => {
    cursor.style.left = `${e.clientX}px`;
    cursor.style.top = `${e.clientY}px`;
  });
  window.addEventListener("mousedown", (e) => ripple(e.clientX, e.clientY));
  };
  if (document.documentElement) install();
  else document.addEventListener("DOMContentLoaded", install, { once: true });
})();
"""


class Stage:
    """A hand over a browser page: move, point, click, type, scroll and
    read, plus a caption band and a beat (pause) scaled by pacing.

    Takes anything shaped like a playwright `Page` -- tests pass a fake
    double instead, so nothing here needs a browser to be exercised.
    """

    def __init__(
        self,
        page: Any,
        pacing: Pacing,
        marks: Marks,
        viewport: tuple[int, int] = (1600, 900),
    ) -> None:
        self.page = page
        self.pacing = pacing
        self.marks = marks
        self.viewport = viewport
        self._x = viewport[0] / 2
        self._y = viewport[1] / 2

    def beat(self, base_seconds: float = 1.0) -> None:
        """Pause for a beat, scaled by pacing (compressed when rehearsing)."""
        self.pacing.sleep(base_seconds)

    def caption(self, text: str) -> None:
        """Show (or, with an empty string, hide) the caption band."""
        self.page.evaluate("(t) => window.__tramoyaCaption(t)", text)

    def move_to_xy(self, x: float, y: float, steps: int = 15) -> None:
        self.page.mouse.move(x, y, steps=steps)
        self._x, self._y = x, y

    def point_at(self, selector: str) -> None:
        """Scroll an element into view and move the cursor to its center."""
        locator = self.page.locator(selector).first
        locator.scroll_into_view_if_needed()
        box = locator.bounding_box()
        if not box:
            raise RuntimeError(f"element not visible: {selector}")
        self.move_to_xy(box["x"] + box["width"] / 2, box["y"] + box["height"] / 2)

    def click(self, selector: str, note: str = "") -> None:
        self.point_at(selector)
        self.page.locator(selector).first.click()

    def click_text(self, text: str, exact: bool = True) -> None:
        locator = self.page.get_by_text(text, exact=exact).first
        locator.scroll_into_view_if_needed()
        box = locator.bounding_box()
        if not box:
            raise RuntimeError(f"text not visible: {text}")
        self.move_to_xy(box["x"] + box["width"] / 2, box["y"] + box["height"] / 2)
        locator.click()

    def type_into(self, selector: str, text: str, delay: int = 40) -> None:
        self.click(selector)
        self.page.locator(selector).first.type(text, delay=delay)

    def select(self, selector: str, value: str) -> None:
        self.page.locator(selector).first.select_option(value)

    def scroll(self, dy: int, dx: int = 0) -> None:
        self.page.mouse.wheel(dx, dy)

    def read(self, selector: str, timeout: int = 5000) -> str:
        """Read an element's text, or an empty string if it never appears."""
        locator = self.page.locator(selector).first
        try:
            locator.wait_for(state="visible", timeout=timeout)
            return locator.inner_text()
        except Exception:  # noqa: BLE001 -- any failure here is expected, not a bug
            return ""


class SceneRegistry:
    """An ordered collection of scenes, each wrapped so its start/end marks
    and its caption on/off are never forgotten by the scene itself."""

    def __init__(self) -> None:
        self.order: list[str] = []
        self._scenes: dict[str, tuple[str, Callable[..., None]]] = {}

    def scene(self, name: str, label: str) -> Callable[[Callable[..., None]], Callable[..., None]]:
        def decorator(fn: Callable[..., None]) -> Callable[..., None]:
            self.order.append(name)
            self._scenes[name] = (label, fn)
            return fn

        return decorator

    def names(self) -> list[str]:
        return list(self.order)

    def run_all(self, stage: Stage, *args: Any) -> None:
        if not self.order:
            raise RuntimeError("no scenes registered")
        for name in self.order:
            label, fn = self._scenes[name]
            with stage.marks.scene(name, label):
                stage.caption(label)
                try:
                    fn(stage, *args)
                finally:
                    stage.caption("")


def http_ready(url: str, timeout: float = 20.0, poll_seconds: float = 0.25) -> bool:
    """Poll `url` until it answers or `timeout` seconds pass."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2.0):
                return True
        except (urllib.error.URLError, OSError):
            time.sleep(poll_seconds)
    return False


class AppProcess:
    """The app under test: cloned into a scratch directory and launched as
    a subprocess, torn down with SIGINT and a fallback kill.

    `runner`/`popen` are not injected directly here; tests instead patch
    `subprocess.run`/`subprocess.Popen` and `tempfile.mkdtemp`, which is
    simpler to stub than threading factories through every call site.
    """

    def __init__(
        self,
        clone_url: str,
        branch: str,
        port: int,
        workdir: str | Path | None = None,
        python: str = "python",
        entry: str = "app.py",
        git_ssl_verify: bool = True,
    ) -> None:
        self.clone_url = clone_url
        self.branch = branch
        self.port = port
        self.python = python
        self.entry = entry
        self.git_ssl_verify = git_ssl_verify
        self._workdir = str(workdir) if workdir else None
        self.workdir: Path | None = None
        self.proc: Any = None

    def _clone(self) -> Path:
        workdir = Path(self._workdir or tempfile.mkdtemp(prefix="tramoya-app-"))
        argv = [
            "git",
            "-c",
            f"http.sslVerify={'true' if self.git_ssl_verify else 'false'}",
            "clone",
            "--branch",
            self.branch,
            "--depth",
            "1",
            self.clone_url,
            str(workdir),
        ]
        result = subprocess.run(argv, capture_output=True, text=True)
        if result.returncode != 0:
            raise RuntimeError(f"failed to clone {self.clone_url}: {result.stderr}")
        return workdir

    def start(self) -> AppProcess:
        self.workdir = self._clone()
        entry_path = str(self.workdir / self.entry)
        argv = [self.python, entry_path, "--port", str(self.port)]
        self.proc = subprocess.Popen(argv, cwd=str(self.workdir))
        return self

    def stop(self, timeout: float = 10.0) -> None:
        if self.proc is None:
            return
        proc, self.proc = self.proc, None
        import signal

        proc.send_signal(signal.SIGINT)
        try:
            proc.wait(timeout=timeout)
        except Exception:  # noqa: BLE001 -- any failure here is expected, not a bug
            proc.kill()
            proc.wait(timeout=timeout)

    def __enter__(self) -> AppProcess:
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> bool:
        self.stop()
        return False


@contextmanager
def open_stage(
    url: str,
    pacing: Pacing,
    marks: Marks,
    video_dir: Path | None = None,
    viewport: tuple[int, int] = (1600, 900),
    headless: bool = True,
) -> Iterator[Stage]:
    """Open a browser, install the overlay before any app script runs, and
    yield a `Stage` bound to the resulting page.

    playwright is imported lazily here -- this is the only function in the
    module that touches it -- so importing `tramoya.stage` never fails when
    playwright is not installed; only calling `open_stage` does, with a
    clear message pointing at the extra to install.
    """
    try:
        from playwright.sync_api import sync_playwright
    except ImportError as exc:
        raise ImportError(
            "open_stage requires the 'playwright' extra: pip install tramoya[playwright]"
        ) from exc

    record_kwargs: dict[str, Any] = {}
    if video_dir is not None:
        record_kwargs["record_video_dir"] = str(video_dir)
        record_kwargs["record_video_size"] = {"width": viewport[0], "height": viewport[1]}

    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=headless)
        context = browser.new_context(
            viewport={"width": viewport[0], "height": viewport[1]}, **record_kwargs
        )
        context.add_init_script(OVERLAY_JS)
        page = context.new_page()
        page.goto(url)
        try:
            yield Stage(page, pacing, marks, viewport=viewport)
        finally:
            context.close()
            browser.close()
