"""The one test that pays for a real encode: three seconds of `testsrc`
through marks -> scene clips -> voice fit -> concat. Opt in with `-m slow`."""

from __future__ import annotations

import json
import math
import shutil
import struct
import subprocess
import wave
from pathlib import Path

import pytest

from tramoya.assembly import assemble_marks
from tramoya.ffmpeg import Runner

pytestmark = pytest.mark.slow


def _tone(path: Path, seconds: float, hz: float = 220.0) -> None:
    """A quiet tone, not digital silence: `loudnorm` yields NaN on all-zero
    input and the AAC encoder refuses it, which no real voice clip triggers."""
    rate = 24000
    frames = bytearray()
    for i in range(int(rate * seconds)):
        sample = int(3000 * math.sin(2 * math.pi * hz * i / rate))
        frames += struct.pack("<h", sample)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(bytes(frames))


@pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="ffmpeg not installed")
def test_three_seconds_go_through_the_real_pipeline(tmp_path: Path) -> None:
    take = tmp_path / "take.mp4"
    subprocess.run([
        "ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
        "testsrc=duration=3:size=320x180:rate=10", "-pix_fmt", "yuv420p", str(take),
    ], check=True)
    marks = tmp_path / "marks.json"
    marks.write_text(json.dumps([
        {"t": 0.0, "key": "scene:a:start", "label": "A"},
        {"t": 1.5, "key": "scene:a:end", "label": "A"},
        {"t": 1.5, "key": "scene:b:start", "label": "B"},
        {"t": 3.0, "key": "scene:b:end", "label": "B"},
    ]))
    voices = tmp_path / "voices"
    voices.mkdir()
    _tone(voices / "scene-a.wav", 2.5)  # longer than its scene: the frame freezes

    runner = Runner()
    out = tmp_path / "out.mp4"
    table = assemble_marks(runner, take, marks, voices, out)

    assert out.exists() and out.stat().st_size > 0
    assert [name for name, _, _ in table] == ["a", "b"]
    assert runner.probe_duration(out) == pytest.approx(4.0, abs=0.3)  # 2.5 + 1.5
