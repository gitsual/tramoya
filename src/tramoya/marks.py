"""Scene and wait marks for a recorded demo run.

A `Marks` instance timestamps named moments (scene boundaries, model waits)
relative to a start time, and can write itself to disk after every mark so a
crashed or killed recording still leaves every mark reached up to that point
on disk -- which is what lets a montage script cut a partial take.
"""

from __future__ import annotations

import json
import time
from collections.abc import Callable, Iterator
from contextlib import contextmanager
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Mark:
    t: float
    key: str
    label: str = ""


def scene_key(name: str, edge: str) -> str:
    """Build the canonical key for a scene boundary, e.g. `scene:intro:start`."""
    return f"scene:{name}:{edge}"


def _wait_key(edge: str) -> str:
    return f"wait:{edge}"


def parse_key(key: str) -> tuple[str, str | None, str | None]:
    """Parse a mark key into (kind, name, edge).

    `kind` is one of "scene", "wait" or "other". Accepts both the canonical
    `scene:<name>:<edge>` / `wait:<edge>` keys and the legacy Spanish keys
    `escena-<name>-inicio` / `escena-<name>-fin` / `espera-inicio` /
    `espera-fin`.
    """
    if key.startswith("scene:"):
        _, name, edge = key.split(":", 2)
        return ("scene", name, edge)
    if key.startswith("wait:"):
        edge = key[len("wait:") :]
        return ("wait", None, edge)
    if key.startswith("escena-") and key.endswith("-inicio"):
        return ("scene", key[len("escena-") : -len("-inicio")], "start")
    if key.startswith("escena-") and key.endswith("-fin"):
        return ("scene", key[len("escena-") : -len("-fin")], "end")
    if key == "espera-inicio":
        return ("wait", None, "start")
    if key == "espera-fin":
        return ("wait", None, "end")
    return ("other", key, None)


def _to_dict(mark: Mark) -> dict[str, float | str]:
    return {"t": mark.t, "key": mark.key, "label": mark.label}


def _from_row(row: dict, offset: float) -> Mark:
    if "key" in row:
        key = row["key"]
        label = row.get("label", "")
    else:
        key = row["clave"]
        label = row.get("etiqueta", "")
    return Mark(t=row["t"] + offset, key=key, label=label)


def load_marks(path: Path, offset: float = 0.0) -> list[Mark]:
    """Load marks from `path`, accepting both the new and legacy JSON shapes."""
    raw = json.loads(Path(path).read_text(encoding="utf-8"))
    return [_from_row(row, offset) for row in raw]


def scene_bounds(marks: list[Mark]) -> dict[str, tuple[float, float]]:
    """Map each fully-bounded scene name to (start, end), in first-seen order."""
    starts: dict[str, float] = {}
    ends: dict[str, float] = {}
    order: list[str] = []
    for mark in marks:
        kind, name, edge = parse_key(mark.key)
        if kind != "scene" or name is None:
            continue
        if edge == "start":
            if name not in starts:
                order.append(name)
            starts[name] = mark.t
        elif edge == "end":
            ends[name] = mark.t
    return {name: (starts[name], ends[name]) for name in order if name in ends}


def wait_windows(marks: list[Mark]) -> list[tuple[float, float]]:
    """Return every fully-bounded wait window as (start, end)."""
    windows: list[tuple[float, float]] = []
    pending: float | None = None
    for mark in marks:
        kind, _name, edge = parse_key(mark.key)
        if kind != "wait":
            continue
        if edge == "start":
            pending = mark.t
        elif edge == "end" and pending is not None:
            windows.append((pending, mark.t))
            pending = None
    return windows


class Marks:
    """Incremental writer for a run's mark file.

    If `path` is given, the whole mark list is rewritten to disk after every
    `add()` -- that incremental flush is the canonical write mode.
    """

    def __init__(
        self,
        t0: float | None = None,
        path: Path | None = None,
        clock: Callable[[], float] = time.time,
    ) -> None:
        self._clock = clock
        self.t0 = t0 if t0 is not None else clock()
        self.path = path
        self._rows: list[Mark] = []
        if self.path is not None:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self._flush()

    @property
    def rows(self) -> list[Mark]:
        return list(self._rows)

    def add(self, key: str, label: str = "") -> Mark:
        mark = Mark(t=round(self._clock() - self.t0, 2), key=key, label=label)
        self._rows.append(mark)
        if self.path is not None:
            self._flush()
        return mark

    def write(self, path: Path) -> None:
        Path(path).write_text(
            json.dumps([_to_dict(m) for m in self._rows], ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

    def _flush(self) -> None:
        assert self.path is not None
        self.write(self.path)

    @contextmanager
    def scene(self, name: str, label: str = "") -> Iterator[None]:
        self.add(scene_key(name, "start"), label)
        try:
            yield
        finally:
            self.add(scene_key(name, "end"), label)

    @contextmanager
    def wait(self, label: str = "") -> Iterator[None]:
        self.add(_wait_key("start"), label)
        try:
            yield
        finally:
            self.add(_wait_key("end"), label)
