"""The `tramoya` command line: every subcommand that touches ffmpeg has a
`--dry-run` that prints the argv it would run and never encodes."""

from __future__ import annotations

import json
import shlex
from pathlib import Path

import pytest

from tramoya import cli

FIXTURES = Path(__file__).parent / "fixtures"


def test_help_lists_every_subcommand(capsys: pytest.CaptureFixture[str]) -> None:
    with pytest.raises(SystemExit) as exc:
        cli.main(["--help"])
    assert exc.value.code == 0
    out = capsys.readouterr().out
    for name in ("marks", "assemble", "deck", "captions", "music", "tts", "record", "cues"):
        assert name in out


def test_marks_prints_a_scene_table(capsys: pytest.CaptureFixture[str]) -> None:
    rc = cli.main(["marks", str(FIXTURES / "legacy-marks-v11.json")])
    assert rc == 0
    out = capsys.readouterr().out
    assert "apertura" in out
    assert "13 scenes" in out


def test_marks_offset_shifts_every_timestamp(capsys: pytest.CaptureFixture[str]) -> None:
    cli.main(["marks", str(FIXTURES / "legacy-marks-v11.json")])
    plain = capsys.readouterr().out
    cli.main(["marks", str(FIXTURES / "legacy-marks-v11.json"), "--offset", "10"])
    shifted = capsys.readouterr().out
    assert plain != shifted
    assert "14.31" in shifted  # 4.31 + 10


def test_assemble_dry_run_prints_ffmpeg_commands_without_encoding(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    rc = cli.main([
        "assemble", "--video", "take.mp4",
        "--marks", str(FIXTURES / "legacy-marks-v11.json"),
        "--voice-dir", str(tmp_path), "--out", str(tmp_path / "out.mp4"),
        "--dry-run",
    ])
    assert rc == 0
    out = capsys.readouterr().out
    assert out.count("ffmpeg") >= 13
    assert "concat" in out
    assert not (tmp_path / "out.mp4").exists()


def test_assemble_reads_the_offset_sidecar_next_to_the_video(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    video = tmp_path / "take.mp4"
    (tmp_path / "take.mp4.offset.json").write_text(
        json.dumps({"rec_start": 0.0, "director_start": 2.5, "offset": 2.5})
    )
    cli.main([
        "assemble", "--video", str(video),
        "--marks", str(FIXTURES / "legacy-marks-v11.json"),
        "--voice-dir", str(tmp_path), "--out", str(tmp_path / "out.mp4"),
        "--dry-run",
    ])
    out = capsys.readouterr().out
    assert "-ss 6.81" in out  # 4.31 + 2.5


def test_captions_burn_dry_run(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    rc = cli.main([
        "captions", "burn", "--video", "in.mp4", "--subtitles", "cues.srt",
        "--out", str(tmp_path / "o.mp4"), "--dry-run",
    ])
    assert rc == 0
    out = capsys.readouterr().out
    assert "subtitles=cues.srt" in out


def test_captions_burn_with_trim_plans_cuts_and_concat(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    cli.main([
        "captions", "burn", "--video", "in.mp4", "--subtitles", "cues.srt",
        "--out", str(tmp_path / "o.mp4"), "--trim", "0-120,600-780", "--dry-run",
    ])
    out = capsys.readouterr().out
    assert out.count("ffmpeg") == 4
    assert "-ss 0" in out and "-to 120" in out
    assert "-f concat" in out


def test_music_bed_writes_a_wav(tmp_path: Path) -> None:
    rc = cli.main(["music", "bed", "--out", str(tmp_path / "bed.wav")])
    assert rc == 0
    assert (tmp_path / "bed.wav").stat().st_size > 1000


def test_music_mix_dry_run_ducks_by_default(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    cli.main([
        "music", "mix", "--video", "v.mp4", "--bed", "bed.wav",
        "--out", str(tmp_path / "o.mp4"), "--dry-run",
    ])
    out = capsys.readouterr().out
    assert "sidechaincompress" in out
    cli.main([
        "music", "mix", "--video", "v.mp4", "--bed", "bed.wav",
        "--out", str(tmp_path / "o.mp4"), "--solo", "--dry-run",
    ])
    assert "sidechaincompress" not in capsys.readouterr().out


def test_cues_to_marks_converts_a_vivac_take(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    cues = tmp_path / "take.cues"
    cues.write_text("1.000|Hello\n5.500|World\n9.000|\n")
    meta = tmp_path / "take.meta"
    meta.write_text("resolution=1920x1080\nelapsed=9.0\n")
    out = tmp_path / "marks.json"
    rc = cli.main([
        "cues", "marks", "--cues", str(cues), "--meta", str(meta),
        "--duration", "10.0", "--out", str(out),
    ])
    assert rc == 0
    rows = json.loads(out.read_text())
    assert rows[0]["key"] == "scene:0:start"
    assert rows[0]["t"] == pytest.approx(2.0)  # 1.0 + (10.0 - 9.0)


def test_cues_to_ass_writes_a_subtitle_file(tmp_path: Path) -> None:
    cues = tmp_path / "take.cues"
    cues.write_text("1.000|Hello\n5.500|World\n9.000|\n")
    out = tmp_path / "take.ass"
    rc = cli.main([
        "cues", "ass", "--cues", str(cues), "--width", "1920", "--height", "1080",
        "--out", str(out),
    ])
    assert rc == 0
    text = out.read_text()
    assert "[V4+ Styles]" in text
    assert "Hello" in text and "World" in text


def test_record_dry_run_prints_the_recorder_argv(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    rc = cli.main([
        "record", "--out", str(tmp_path / "take.mp4"), "--geometry", "0,48 1920x1032",
        "--fps", "10", "--dry-run",
    ])
    assert rc == 0
    out = capsys.readouterr().out
    assert "wf-recorder" in out
    assert "-r 10" in out


def test_tts_without_the_extra_fails_with_a_clear_message(
    tmp_path: Path, capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    script = tmp_path / "script.json"
    script.write_text(json.dumps({"intro": {"en": "Hello"}}))

    def boom(*_a: object, **_k: object) -> None:
        raise ImportError("kokoro")

    monkeypatch.setattr(cli, "synthesize_script", boom)
    rc = cli.main(["tts", "--script", str(script), "--lang", "en", "--out-dir", str(tmp_path)])
    assert rc == 2
    assert "tramoya[tts]" in capsys.readouterr().err


def test_tts_names_the_scenes_without_text_in_that_language(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    script = tmp_path / "script.json"
    script.write_text(json.dumps({"intro": {"en": "Hello", "es": "Hola"}, "end": {"en": "Bye"}}))
    rc = cli.main(["tts", "--script", str(script), "--lang", "es", "--out-dir", str(tmp_path / "v")])
    assert rc == 2
    assert "tts: the script has no 'es' text for end; languages in it: en, es" in capsys.readouterr().err
    assert not (tmp_path / "v").exists()


def test_deck_dry_run_plans_pages_and_clip(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    slides = tmp_path / "slides"
    slides.mkdir()
    for name in ("cover", "video", "end"):
        (slides / f"{name}.png").write_bytes(b"png")
    (slides / "index.json").write_text(
        json.dumps({"pages": {"cover": "001", "video": "002", "end": "003"}})
    )
    voices = tmp_path / "voices"
    voices.mkdir()
    rc = cli.main([
        "deck", "--slides", str(slides), "--voice-dir", str(voices),
        "--clip", "clip.mp4", "--clip-duration", "12",
        "--out", str(tmp_path / "deck.mp4"), "--dry-run",
    ])
    assert rc == 0
    out = capsys.readouterr().out
    assert "cover" in out and "clip" in out
    assert "ffmpeg" in out


def test_direct_dry_run_prints_the_director_argv(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    script = tmp_path / "director.py"
    script.write_text("print('hi')\n")
    rc = cli.main([
        "direct", str(script), "--out", str(tmp_path / "out"), "--pace", "0.8",
        "--rehearse", "--lang", "en", "--dry-run",
    ])
    assert rc == 0
    out = capsys.readouterr().out
    assert shlex.quote(str(script)) in out
    assert f"--out {shlex.quote(str(tmp_path / 'out'))} --pace 0.8 --rehearse --lang en" in out
    assert "python" in out.split()[0]


def test_direct_runs_the_director_with_the_current_python(tmp_path: Path) -> None:
    script = tmp_path / "director.py"
    script.write_text(
        "import sys, pathlib\n"
        "out = pathlib.Path(sys.argv[sys.argv.index('--out') + 1])\n"
        "out.mkdir(exist_ok=True)\n"
        "(out / 'marks.json').write_text('[]')\n"
    )
    rc = cli.main(["direct", str(script), "--out", str(tmp_path / "out")])
    assert rc == 0
    assert (tmp_path / "out" / "marks.json").read_text() == "[]"


# --- the profile path: init, direct tramoya.toml, import, capture, login, snapshots -------------


def test_help_lists_the_profile_subcommands(capsys: pytest.CaptureFixture[str]) -> None:
    with pytest.raises(SystemExit):
        cli.main(["--help"])
    out = capsys.readouterr().out
    for name in ("init", "import", "capture", "login", "snapshots"):
        assert name in out


def test_init_non_interactive_writes_a_project(tmp_path: Path, capsys) -> None:
    rc = cli.main(["init", str(tmp_path), "--no-interactive", "--name", "Notes",
                   "--url", "http://x/", "--lang", "en,es",
                   "--scene", "welcome: Hi", "--scene", "search: Find"])
    assert rc == 0
    assert (tmp_path / "tramoya.toml").exists()
    assert (tmp_path / "script.json").exists()
    assert (tmp_path / "Makefile").exists()
    out = capsys.readouterr().out
    assert "tramoya direct tramoya.toml" in out


def test_init_non_interactive_names_the_missing_flag(tmp_path: Path, capsys) -> None:
    rc = cli.main(["init", str(tmp_path), "--no-interactive", "--name", "Notes",
                   "--url", "http://x/"])
    assert rc == 2
    assert "--scene" in capsys.readouterr().err


def test_init_interactive_reads_answers_from_stdin(tmp_path: Path, monkeypatch, capsys) -> None:
    replies = iter(["Notes", "url", "http://x/", "n", "en", "welcome: Hi", ""])
    monkeypatch.setattr("builtins.input", lambda prompt="": next(replies))
    monkeypatch.setattr("sys.stdin.isatty", lambda: True)
    rc = cli.main(["init", str(tmp_path)])
    assert rc == 0
    assert "welcome" in (tmp_path / "tramoya.toml").read_text()


def test_direct_with_a_profile_dry_run_prints_the_scene_plan(tmp_path: Path, capsys) -> None:
    cli.main(["init", str(tmp_path), "--no-interactive", "--name", "Notes", "--url",
              "http://x/", "--scene", "welcome: Hi"])
    capsys.readouterr()
    rc = cli.main(["direct", str(tmp_path / "tramoya.toml"), "--out", str(tmp_path / "out"),
                   "--dry-run"])
    assert rc == 0
    out = capsys.readouterr().out
    assert "1 scenes at http://x/" in out and "welcome" in out
    assert not (tmp_path / "out").exists()


def test_direct_with_a_profile_runs_the_generic_director(tmp_path: Path, monkeypatch) -> None:
    cli.main(["init", str(tmp_path), "--no-interactive", "--name", "Notes", "--url",
              "http://x/", "--scene", "welcome: Hi"])
    seen: dict = {}

    def fake_run(profile, out, **kw):
        seen.update(name=profile.name, out=Path(out), **kw)
        from tramoya.director import Result
        return Result(Path(out))

    monkeypatch.setattr("tramoya.cli.run_profile", fake_run)
    rc = cli.main(["direct", str(tmp_path / "tramoya.toml"), "--out", str(tmp_path / "out"),
                   "--rehearse", "--url", "http://staging/", "--headed"])
    assert rc == 0
    assert seen["name"] == "Notes" and seen["rehearse"] is True
    assert seen["url"] == "http://staging/" and seen["headless"] is False


def test_import_appends_recorded_scenes_to_the_profile(tmp_path: Path, capsys) -> None:
    cli.main(["init", str(tmp_path), "--no-interactive", "--name", "Notes", "--url",
              "http://x/", "--scene", "welcome: Hi"])
    flow = tmp_path / "flow.json"
    flow.write_text(json.dumps({"steps": [
        {"type": "navigate", "url": "http://x/index.html"},
        {"type": "click", "selectors": [["#new"]]},
        {"type": "navigate", "url": "http://x/settings.html"},
        {"type": "click", "selectors": [["#save"]]},
    ]}))
    capsys.readouterr()
    rc = cli.main(["import", str(flow), "--into", str(tmp_path / "tramoya.toml")])
    assert rc == 0
    from tramoya.profile import load_profile
    profile = load_profile(tmp_path / "tramoya.toml")
    assert [s.name for s in profile.scenes] == ["welcome", "index", "settings"]
    assert profile.scenes[1].steps == ["click #new", "say"]
    assert profile.scenes[1].narration == {"en": ""}
    # the script follows the profile
    script = json.loads((tmp_path / "script.json").read_text())
    assert "scene-settings" in script
    assert "2 scenes" in capsys.readouterr().out


def test_import_into_a_missing_profile_creates_one_from_the_recording(tmp_path: Path) -> None:
    flow = tmp_path / "flow.json"
    flow.write_text(json.dumps({"steps": [
        {"type": "navigate", "url": "http://x/index.html"},
        {"type": "click", "selectors": [["#new"]]},
    ]}))
    into = tmp_path / "demo" / "tramoya.toml"  # the folder does not exist yet
    rc = cli.main(["import", str(flow), "--into", str(into), "--name", "Notes"])
    assert rc == 0
    from tramoya.profile import load_profile
    profile = load_profile(into)
    assert profile.url == "http://x/index.html" and profile.name == "Notes"


def test_capture_dry_run_prints_the_codegen_command(capsys) -> None:
    rc = cli.main(["capture", "http://x/", "--out", "flow.py", "--dry-run"])
    assert rc == 0
    out = capsys.readouterr().out
    assert "playwright codegen" in out and "--target python" in out and "http://x/" in out


def test_login_dry_run_says_where_the_session_goes(capsys) -> None:
    rc = cli.main(["login", "http://x/", "--dry-run"])
    assert rc == 0
    assert str(Path(".tramoya/auth.json")) in capsys.readouterr().out


def test_snapshots_diff_exits_nonzero_when_a_scene_changed(tmp_path: Path, capsys) -> None:
    from tramoya.snapshots import write_snapshot
    write_snapshot(tmp_path / "a", "welcome", "- heading Notes\n")
    write_snapshot(tmp_path / "b", "welcome", "- heading Notas\n")
    assert cli.main(["snapshots", "diff", str(tmp_path / "a"), str(tmp_path / "a")]) == 0
    rc = cli.main(["snapshots", "diff", str(tmp_path / "a"), str(tmp_path / "b")])
    assert rc == 1
    assert "welcome: changed" in capsys.readouterr().out
