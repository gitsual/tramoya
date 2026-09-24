from __future__ import annotations

import json
from dataclasses import FrozenInstanceError
from pathlib import Path

import pytest

from tramoya.marks import Mark, Marks, load_marks, parse_key, scene_bounds, scene_key, wait_windows

FIXTURES = Path(__file__).parent / "fixtures"


def test_mark_is_frozen_dataclass() -> None:
    m = Mark(t=1.0, key="scene:intro:start", label="hi")
    assert m.t == 1.0
    assert m.key == "scene:intro:start"
    assert m.label == "hi"
    with pytest.raises(FrozenInstanceError):
        m.t = 2.0  # type: ignore[misc]


def test_marks_add_rounds_and_appends() -> None:
    clock = iter([10.126, 11.4]).__next__
    marks = Marks(t0=10.0, clock=clock)
    m1 = marks.add("scene:a:start", "A starts")
    m2 = marks.add("scene:a:end")
    assert m1.t == 0.13
    assert m2.t == 1.4
    assert marks.rows == [m1, m2]


def test_marks_incremental_flush_writes_after_every_add(tmp_path: Path) -> None:
    path = tmp_path / "marks.json"
    times = iter([1.0, 2.0])
    marks = Marks(t0=0.0, path=path, clock=lambda: next(times))

    marks.add("scene:a:start", "A")
    on_disk_after_first = json.loads(path.read_text(encoding="utf-8"))
    assert on_disk_after_first == [{"t": 1.0, "key": "scene:a:start", "label": "A"}]

    marks.add("scene:a:end", "A")
    on_disk_after_second = json.loads(path.read_text(encoding="utf-8"))
    assert on_disk_after_second == [
        {"t": 1.0, "key": "scene:a:start", "label": "A"},
        {"t": 2.0, "key": "scene:a:end", "label": "A"},
    ]


def test_marks_write_dumps_all_rows(tmp_path: Path) -> None:
    path = tmp_path / "out.json"
    times = iter([1.0])
    marks = Marks(t0=0.0, clock=lambda: next(times))
    marks.add("scene:a:start")
    marks.write(path)
    assert json.loads(path.read_text(encoding="utf-8")) == [
        {"t": 1.0, "key": "scene:a:start", "label": ""}
    ]


def test_marks_scene_context_manager_emits_start_and_end() -> None:
    times = iter([0.0, 1.0, 2.0])
    marks = Marks(t0=0.0, clock=lambda: next(times))
    with marks.scene("intro", "Intro label"):
        pass
    assert [r.key for r in marks.rows] == ["scene:intro:start", "scene:intro:end"]
    assert all(r.label == "Intro label" for r in marks.rows)


def test_marks_scene_context_manager_emits_end_on_exception() -> None:
    times = iter([0.0, 1.0, 2.0])
    marks = Marks(t0=0.0, clock=lambda: next(times))
    with pytest.raises(ValueError, match="boom"):
        with marks.scene("intro"):
            raise ValueError("boom")
    assert [r.key for r in marks.rows] == ["scene:intro:start", "scene:intro:end"]


def test_marks_wait_context_manager() -> None:
    times = iter([0.0, 1.0, 2.0])
    marks = Marks(t0=0.0, clock=lambda: next(times))
    with marks.wait("model call"):
        pass
    assert [r.key for r in marks.rows] == ["wait:start", "wait:end"]


def test_scene_key_builds_canonical_key() -> None:
    assert scene_key("intro", "start") == "scene:intro:start"
    assert scene_key("intro", "end") == "scene:intro:end"


@pytest.mark.parametrize(
    ("key", "expected"),
    [
        ("scene:intro:start", ("scene", "intro", "start")),
        ("scene:intro:end", ("scene", "intro", "end")),
        ("wait:start", ("wait", None, "start")),
        ("wait:end", ("wait", None, "end")),
        ("escena-apertura-inicio", ("scene", "apertura", "start")),
        ("escena-apertura-fin", ("scene", "apertura", "end")),
        ("espera-inicio", ("wait", None, "start")),
        ("espera-fin", ("wait", None, "end")),
        ("whatever-else", ("other", "whatever-else", None)),
    ],
)
def test_parse_key(key: str, expected: tuple) -> None:
    assert parse_key(key) == expected


def test_load_marks_new_shape(tmp_path: Path) -> None:
    path = tmp_path / "marks.json"
    path.write_text(
        json.dumps([{"t": 1.0, "key": "scene:a:start", "label": "A"}]),
        encoding="utf-8",
    )
    marks = load_marks(path)
    assert marks == [Mark(t=1.0, key="scene:a:start", label="A")]


def test_load_marks_legacy_shape(tmp_path: Path) -> None:
    path = tmp_path / "marks.json"
    path.write_text(
        json.dumps([{"t": 1.0, "clave": "escena-a-inicio", "etiqueta": "A"}]),
        encoding="utf-8",
    )
    marks = load_marks(path)
    assert marks == [Mark(t=1.0, key="escena-a-inicio", label="A")]


def test_load_marks_applies_offset(tmp_path: Path) -> None:
    path = tmp_path / "marks.json"
    path.write_text(json.dumps([{"t": 1.0, "key": "scene:a:start", "label": ""}]), encoding="utf-8")
    marks = load_marks(path, offset=5.0)
    assert marks[0].t == 6.0


def test_load_marks_legacy_fixture_has_matched_scenes() -> None:
    marks = load_marks(FIXTURES / "legacy-marks-v11.json")
    bounds = scene_bounds(marks)
    assert len(bounds) == 13
    assert "apertura" in bounds
    for start, end in bounds.values():
        assert start < end


def test_scene_bounds_preserves_first_seen_order() -> None:
    marks = [
        Mark(t=0.0, key="scene:b:start"),
        Mark(t=1.0, key="scene:a:start"),
        Mark(t=2.0, key="scene:b:end"),
        Mark(t=3.0, key="scene:a:end"),
    ]
    assert list(scene_bounds(marks).keys()) == ["b", "a"]


def test_scene_bounds_new_and_legacy_keys_agree() -> None:
    new_marks = [
        Mark(t=0.0, key="scene:apertura:start"),
        Mark(t=1.0, key="scene:apertura:end"),
    ]
    legacy_marks = [
        Mark(t=0.0, key="escena-apertura-inicio"),
        Mark(t=1.0, key="escena-apertura-fin"),
    ]
    assert scene_bounds(new_marks) == scene_bounds(legacy_marks)


def test_wait_windows() -> None:
    marks = [
        Mark(t=0.0, key="wait:start"),
        Mark(t=1.0, key="wait:end"),
        Mark(t=2.0, key="scene:x:start"),
        Mark(t=3.0, key="wait:start"),
        Mark(t=4.0, key="wait:end"),
    ]
    assert wait_windows(marks) == [(0.0, 1.0), (3.0, 4.0)]


def test_wait_windows_legacy_keys() -> None:
    marks = [Mark(t=0.0, key="espera-inicio"), Mark(t=1.0, key="espera-fin")]
    assert wait_windows(marks) == [(0.0, 1.0)]
