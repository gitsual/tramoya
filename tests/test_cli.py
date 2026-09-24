"""The `tramoya` command line: every subcommand that touches ffmpeg has a
`--dry-run` that prints the argv it would run and never encodes."""

from __future__ import annotations

import json
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
