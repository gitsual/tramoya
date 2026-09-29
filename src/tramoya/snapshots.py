"""Accessibility snapshots: one text file per scene, written next to the
take, so a demo that silently broke (a button moved, a label changed) fails
a diff in CI instead of shipping.

The snapshot is Playwright's ARIA snapshot of the page body: roles and
names, no pixels, so it is stable across themes and fonts and reads as a
diff a person can act on.
"""

from __future__ import annotations

import difflib
from pathlib import Path


def write_snapshot(directory: Path, scene: str, text: str) -> Path:
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / f"{scene}.txt"
    path.write_text(text if text.endswith("\n") else text + "\n", encoding="utf-8")
    return path


def read_snapshots(directory: Path) -> dict[str, str]:
    if not directory.is_dir():
        return {}
    return {p.stem: p.read_text(encoding="utf-8") for p in sorted(directory.glob("*.txt"))}


def diff_snapshots(expected_dir: Path, actual_dir: Path, context: int = 2) -> list[str]:
    """Human-readable problems, one per scene that differs; empty when equal."""
    expected, actual = read_snapshots(expected_dir), read_snapshots(actual_dir)
    problems: list[str] = []
    for scene in sorted(set(expected) | set(actual)):
        if scene not in actual:
            problems.append(f"{scene}: missing from {actual_dir} (the scene did not run)")
        elif scene not in expected:
            problems.append(f"{scene}: new scene, not in {expected_dir}")
        elif expected[scene] != actual[scene]:
            diff = difflib.unified_diff(
                expected[scene].splitlines(), actual[scene].splitlines(),
                fromfile=f"expected/{scene}", tofile=f"actual/{scene}", lineterm="", n=context,
            )
            problems.append(f"{scene}: changed\n" + "\n".join(diff))
    return problems
