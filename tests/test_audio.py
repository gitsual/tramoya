"""Seamless-loop bed synthesis and the ffmpeg duck/solo mix filters."""

from __future__ import annotations

import struct
import wave
from itertools import pairwise
from pathlib import Path

import pytest

from tramoya.audio import (
    CHORDS,
    LOOP_SECONDS,
    PEAK_CEILING,
    SAMPLE_RATE,
    build_loop,
    duck_filter,
    mix_argv,
    quantize,
    render_wav,
    solo_filter,
)


def _peak(frames: list[tuple[float, float]]) -> float:
    return max(max(abs(left), abs(right)) for left, right in frames)


@pytest.fixture(scope="module")
def loop() -> list[tuple[float, float]]:
    """`build_loop` is pure Python synthesis over ~768k frames -- expensive
    enough that every test sharing one loop, instead of building its own,
    keeps this file from dominating the suite's runtime."""
    return build_loop()


def test_every_partial_completes_whole_cycles_in_the_loop() -> None:
    for hz in (55.0, 110.0, 220.0, 329.63, 440.0):
        fitted = quantize(hz)
        cycles = fitted * LOOP_SECONDS
        assert cycles == pytest.approx(round(cycles), abs=1e-9)
        assert abs(fitted - hz) < 0.5


def test_the_loop_wraps_without_a_click(loop: list[tuple[float, float]]) -> None:
    inner = max(
        max(abs(b[0] - a[0]), abs(b[1] - a[1])) for a, b in pairwise(loop)
    )
    wrap = max(abs(loop[0][0] - loop[-1][0]), abs(loop[0][1] - loop[-1][1]))
    assert wrap <= inner, f"the loop clicks: {wrap:.6f} > {inner:.6f}"


def test_the_bed_never_clips(loop: list[tuple[float, float]]) -> None:
    assert _peak(loop) < 1.0


def test_the_bed_sits_far_below_speech(loop: list[tuple[float, float]]) -> None:
    assert _peak(loop) <= PEAK_CEILING


def test_it_is_deterministic(loop: list[tuple[float, float]]) -> None:
    assert build_loop() == loop


def test_the_chords_share_notes_so_nothing_lurches() -> None:
    for a, b in zip(CHORDS, CHORDS[1:] + CHORDS[:1], strict=True):
        assert len(set(a) & set(b)) >= 2, f"{a} -> {b} lurches"


def test_render_wav_round_trips_the_frames(tmp_path: Path) -> None:
    target = tmp_path / "bed.wav"
    render_wav([(0.5, -0.5), (0.0, 0.25)], target)
    with wave.open(str(target), "rb") as handle:
        raw = handle.readframes(handle.getnframes())
    assert struct.unpack("<4h", raw) == (16383, -16383, 0, 8191)


def test_render_wav_writes_the_shipped_format(tmp_path: Path) -> None:
    target = tmp_path / "bed.wav"
    render_wav(build_loop(), target)
    with wave.open(str(target), "rb") as handle:
        assert handle.getnchannels() == 2
        assert handle.getframerate() == SAMPLE_RATE
        assert handle.getsampwidth() == 2
        assert handle.getnframes() == int(LOOP_SECONDS * SAMPLE_RATE)


def test_duck_filter_matches_the_recorded_dry_run() -> None:
    assert duck_filter() == (
        "[0:a]asplit=2[voz][key];"
        "[1:a]aresample=48000,volume=3.2[bed];"
        "[bed][key]sidechaincompress=threshold=0.02:ratio=8:attack=20:release=800[ducked];"
        "[voz][ducked]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[out]"
    )


def test_solo_filter_matches_the_recorded_dry_run() -> None:
    assert solo_filter() == "[1:a]aresample=48000,volume=1.6[out]"


def test_mix_argv_duck_mode() -> None:
    argv = mix_argv(Path("demo-captions.mp4"), Path("bed-32s.wav"), Path("demo-musica.mp4"), duck=True)
    assert argv == [
        "ffmpeg", "-y",
        "-i", "demo-captions.mp4",
        "-stream_loop", "-1", "-i", "bed-32s.wav",
        "-filter_complex", duck_filter(),
        "-map", "0:v", "-map", "[out]",
        "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
        "-shortest", "demo-musica.mp4",
    ]


def test_mix_argv_solo_mode_drops_the_source_audio() -> None:
    argv = mix_argv(Path("demo-mudo.mp4"), Path("bed-32s.wav"), Path("demo-directo.mp4"), duck=False)
    assert "-filter_complex" in argv
    assert argv[argv.index("-filter_complex") + 1] == solo_filter()
    assert "0:a" not in " ".join(argv).replace("0:v", "")
    assert argv[-1] == "demo-directo.mp4"
