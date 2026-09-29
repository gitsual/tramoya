"""`tramoya init`: a linear questionnaire, every question mirrored by a flag,
answered here by a scripted double so no terminal is involved."""

from __future__ import annotations

from pathlib import Path

import pytest

from tramoya.profile import load_profile
from tramoya.wizard import (
    FlagAnswers,
    MissingAnswer,
    ScriptedAnswers,
    interview,
    scaffold,
)


def test_interview_builds_a_profile_from_answers() -> None:
    answers = ScriptedAnswers([
        "Notes",                                  # name
        "url",                                    # reach: url | launch
        "http://127.0.0.1:8000/",                 # url
        "n",                                      # login?
        "en, es",                                 # languages
        "welcome: A small notes app",             # scene 1
        "search: Search filters as you type",     # scene 2
        "",                                       # end of scenes
    ])
    profile = interview(answers)
    assert profile.name == "Notes"
    assert profile.url == "http://127.0.0.1:8000/"
    assert profile.launch is None
    assert profile.languages == ["en", "es"]
    assert [(s.name, s.label) for s in profile.scenes] == [
        ("welcome", "A small notes app"), ("search", "Search filters as you type"),
    ]
    assert profile.scenes[0].steps == ["say"]
    assert profile.scenes[0].narration == {"en": "", "es": ""}
    assert answers.exhausted


def test_interview_can_launch_the_app_and_keep_a_session() -> None:
    answers = ScriptedAnswers([
        "Shop", "launch", "npm run dev", "http://localhost:5173/", "y", "en",
        "home: The storefront", "",
    ])
    profile = interview(answers)
    assert profile.launch == "npm run dev"
    assert profile.url == "http://localhost:5173/"
    assert profile.storage_state == ".tramoya/auth.json"


def test_interview_slugs_scene_names_and_rejects_duplicates() -> None:
    answers = ScriptedAnswers([
        "N", "url", "http://x/", "n", "en",
        "New Note!: Create a note", "new-note: again", "views", "",
    ])
    profile = interview(answers)
    assert [s.name for s in profile.scenes] == ["new-note", "views"]
    assert profile.scenes[1].label == "views"
    assert any("already" in w for w in answers.warnings)


def test_flag_answers_mirror_the_questions_and_name_what_is_missing() -> None:
    flags = FlagAnswers(name="Notes", url="http://x/", languages="en",
                        scenes=["welcome: Hi", "search: Find"])
    profile = interview(flags)
    assert profile.name == "Notes" and len(profile.scenes) == 2
    with pytest.raises(MissingAnswer, match="--url or --launch"):
        interview(FlagAnswers(name="Notes"))
    with pytest.raises(MissingAnswer, match="--scene"):
        interview(FlagAnswers(name="Notes", url="http://x/"))


def test_scaffold_writes_profile_script_and_makefile(tmp_path: Path) -> None:
    profile = interview(ScriptedAnswers(["Notes", "url", "http://x/", "n", "en, es",
                                         "welcome: Hi", ""]))
    written = scaffold(profile, tmp_path)
    names = sorted(p.name for p in written)
    assert names == ["Makefile", "script.json", "tramoya.toml"]
    again = load_profile(tmp_path / "tramoya.toml")
    assert again.name == "Notes" and again.scenes[0].name == "welcome"
    makefile = (tmp_path / "Makefile").read_text()
    assert "tramoya direct tramoya.toml" in makefile
    assert "tramoya tts --script" in makefile
    assert "--lang es" in makefile
    assert '"scene-welcome"' in (tmp_path / "script.json").read_text()


def test_scaffold_refuses_to_overwrite_without_force(tmp_path: Path) -> None:
    profile = interview(ScriptedAnswers(["N", "url", "http://x/", "n", "en", "a: A", ""]))
    (tmp_path / "tramoya.toml").write_text("old")
    with pytest.raises(FileExistsError, match="--force"):
        scaffold(profile, tmp_path)
    scaffold(profile, tmp_path, force=True)
    assert "old" not in (tmp_path / "tramoya.toml").read_text()
