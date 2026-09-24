"""Pure ffmpeg/ffprobe argv builders and the Runner that executes them.

Nothing here shells out for real: `Runner(dry_run=True)` records every argv it
would have run in `.calls` instead, and `.probe_duration` reads from a
test-filled `.fake_durations` dict instead of invoking ffprobe.
"""

from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

from tramoya.ffmpeg import (
    LOUDNORM,
    Runner,
    concat_argv,
    freeze_tail,
    pad_to_grid,
    silent_audio,
    wait_compress,
    write_concat_list,
)


def test_loudnorm_matches_the_shipped_normalisation() -> None:
    assert LOUDNORM == "loudnorm=I=-16:TP=-1.5:LRA=11"


def test_runner_dry_run_records_calls_without_shelling_out(monkeypatch: pytest.MonkeyPatch) -> None:
    def _boom(*args: object, **kwargs: object) -> None:
        raise AssertionError("subprocess.run must not be called in dry_run mode")

    monkeypatch.setattr(subprocess, "run", _boom)
    runner = Runner(dry_run=True)
    runner.run(["ffmpeg", "-y", "-i", "in.mp4", "out.mp4"])
    assert runner.calls == [["ffmpeg", "-y", "-i", "in.mp4", "out.mp4"]]


def test_runner_live_run_shells_out_with_check(monkeypatch: pytest.MonkeyPatch) -> None:
    seen: dict[str, object] = {}

    def _fake_run(argv: list[str], check: bool = False) -> None:
        seen["argv"] = argv
        seen["check"] = check

    monkeypatch.setattr(subprocess, "run", _fake_run)
    runner = Runner(dry_run=False)
    runner.run(["ffmpeg", "-y", "out.mp4"])
    assert seen == {"argv": ["ffmpeg", "-y", "out.mp4"], "check": True}
    assert runner.calls == [["ffmpeg", "-y", "out.mp4"]]


def test_probe_duration_in_dry_run_reads_fake_durations() -> None:
    runner = Runner(dry_run=True)
    clip = Path("scene-1.mp4")
    runner.fake_durations[clip] = 12.5
    assert runner.probe_duration(clip) == 12.5


def test_probe_duration_in_dry_run_defaults_to_zero_for_unknown_paths() -> None:
    runner = Runner(dry_run=True)
    assert runner.probe_duration(Path("missing.mp4")) == 0.0


def test_probe_duration_live_shells_out_to_ffprobe(monkeypatch: pytest.MonkeyPatch) -> None:
    def _fake_check_output(argv: list[str]) -> bytes:
        assert argv[0] == "ffprobe"
        assert argv[-1] == "clip.mp4"
        return b"3.140000\n"

    monkeypatch.setattr(subprocess, "check_output", _fake_check_output)
    runner = Runner(dry_run=False)
    assert runner.probe_duration(Path("clip.mp4")) == pytest.approx(3.14)


def test_available_checks_for_the_ffmpeg_binary(monkeypatch: pytest.MonkeyPatch) -> None:
    import shutil

    monkeypatch.setattr(shutil, "which", lambda name: "/usr/bin/ffmpeg" if name == "ffmpeg" else None)
    assert Runner().available() is True

    monkeypatch.setattr(shutil, "which", lambda name: None)
    assert Runner().available() is False


def test_pad_to_grid_scales_and_letterboxes_to_the_target_size() -> None:
    vf = pad_to_grid(1920, 1080)
    assert vf == (
        "scale=1920:1080:force_original_aspect_ratio=decrease,"
        "pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=0x14181F,format=yuv420p"
    )


def test_pad_to_grid_accepts_a_custom_background_colour() -> None:
    vf = pad_to_grid(640, 480, color="0x000000")
    assert "color=0x000000" in vf
    assert vf.startswith("scale=640:480:")


def test_wait_compress_builds_setpts_and_drawtext() -> None:
    vf = wait_compress(5.0, "4 minutes later")
    assert vf.startswith("setpts=PTS/5.0,")
    assert "drawtext=text='4 minutes later'" in vf
    assert "fontcolor=white:fontsize=42" in vf


def test_wait_compress_escapes_apostrophes_and_colons_in_the_label() -> None:
    vf = wait_compress(2.0, "it's 12:30")
    assert "text='it’s 12\\:30'" in vf  # noqa: RUF001


def test_freeze_tail_builds_a_tpad_clone_filter() -> None:
    assert freeze_tail(1.5) == "tpad=stop_mode=clone:stop_duration=1.500"


def test_silent_audio_defaults_to_mono_48k() -> None:
    argv = silent_audio()
    assert argv == ["-f", "lavfi", "-i", "anullsrc=channel_layout=mono:sample_rate=48000"]


def test_silent_audio_accepts_layout_and_rate() -> None:
    argv = silent_audio(layout="stereo", rate=44100)
    assert argv == ["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=44100"]


def test_concat_argv_builds_the_concat_demuxer_command() -> None:
    argv = concat_argv(Path("list.txt"), Path("out.mp4"))
    assert argv == [
        "ffmpeg", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0",
        "-i", "list.txt", "-c", "copy", "out.mp4",
    ]


def test_write_concat_list_writes_one_quoted_file_line_per_path(tmp_path: Path) -> None:
    list_path = tmp_path / "concat.txt"
    write_concat_list([Path("a.mp4"), Path("b.mp4")], list_path)
    assert list_path.read_text(encoding="utf-8") == "file 'a.mp4'\nfile 'b.mp4'\n"


def test_probe_duration_uses_the_csv_shape_ffprobe_accepts(monkeypatch) -> None:
    seen: list[list[str]] = []

    def fake_check_output(argv: list[str]) -> bytes:
        seen.append(argv)
        return b"1.500000\n"

    monkeypatch.setattr("tramoya.ffmpeg.subprocess.check_output", fake_check_output)
    assert Runner().probe_duration(Path("x.mp4")) == 1.5
    assert seen[0][:7] == [
        "ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0",
    ]
