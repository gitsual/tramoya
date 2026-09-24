from __future__ import annotations

import pytest

from tramoya import console


@pytest.fixture(autouse=True)
def _restore_use_color():
    original = console.use_color
    yield
    console.use_color = original


def test_banner_prints_bordered_text(capsys: pytest.CaptureFixture[str]) -> None:
    console.use_color = False
    console.banner("hello\nworld")
    out = capsys.readouterr().out
    assert "hello" in out
    assert "world" in out
    assert "=" in out


def test_step_prints_number_and_text(capsys: pytest.CaptureFixture[str]) -> None:
    console.use_color = False
    console.step(1, "do the thing")
    out = capsys.readouterr().out
    assert "1" in out
    assert "do the thing" in out


def test_note_ok_warn_fail_sim_print_text(capsys: pytest.CaptureFixture[str]) -> None:
    console.use_color = False
    console.note("a note")
    console.ok("all good")
    console.warn("careful")
    console.fail("broken")
    console.sim("simulated operator says hi")
    out = capsys.readouterr().out
    assert "a note" in out
    assert "all good" in out
    assert "careful" in out
    assert "broken" in out
    assert "simulated operator says hi" in out


def test_no_color_strips_ansi_codes(capsys: pytest.CaptureFixture[str]) -> None:
    console.use_color = False
    console.ok("done")
    out = capsys.readouterr().out
    assert "\033[" not in out


def test_color_mode_includes_ansi_codes(capsys: pytest.CaptureFixture[str]) -> None:
    console.use_color = True
    console.ok("done")
    out = capsys.readouterr().out
    assert "\033[" in out


def test_no_color_env_var_disables_color(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("NO_COLOR", "1")
    import importlib

    reloaded = importlib.reload(console)
    assert reloaded.use_color is False
    monkeypatch.delenv("NO_COLOR", raising=False)
    importlib.reload(console)
