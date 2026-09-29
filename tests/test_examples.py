"""Every example profile loads, every step parses and the plan describes
itself, so a broken example never reaches the README."""

from __future__ import annotations

from pathlib import Path

import pytest

from tramoya.director import describe
from tramoya.profile import load_profile, parse_step

EXAMPLES = sorted(Path(__file__).resolve().parents[1].glob("examples/*/tramoya.toml"))


@pytest.mark.parametrize("path", EXAMPLES, ids=lambda p: p.parent.name)
def test_example_profile_loads_and_every_step_parses(path: Path) -> None:
    profile = load_profile(path)
    assert profile.scenes, f"{path} has no scenes"
    for scene in profile.scenes:
        for step in scene.steps:
            parse_step(step)
        for lang in profile.languages:
            assert scene.narration.get(lang), f"{scene.name} has no {lang} narration"
    plan = describe(profile)
    assert profile.name in plan and profile.scenes[0].name in plan


def test_examples_use_distinct_ports_so_they_can_run_side_by_side() -> None:
    urls = [load_profile(p).url for p in EXAMPLES]
    assert len(set(urls)) == len(urls)
