"""A synthesized, copyright-free background bed and its ffmpeg mix filters.

The bed is a short SEAMLESS LOOP (sine partials over a voice-led chord
progression, cross-faded by construction rather than by an explicit
crossfade) that ffmpeg repeats with `-stream_loop`. `duck_filter` sidechains
it under a narration track; `solo_filter` is the flat-level mix used when
there is no synthetic narration to key off (a live presenter instead).
"""

from __future__ import annotations

import math
import wave
from pathlib import Path

SAMPLE_RATE = 24000  # the rate the narration clips already use
LOOP_SECONDS = 32.0  # four chords of 8 s; short enough to synthesize, long
# enough that the repeat is not noticed under speech
TARGET_PEAK = 0.10  # ~-20 dBFS
PEAK_CEILING = 0.12  # what the tests refuse to let the bed exceed

# Partial amplitudes. The rolloff is steep on purpose: a pad with energy in
# the 300-3400 Hz speech band masks consonants.
PARTIALS = (1.0, 0.36, 0.14, 0.05)

NOTES = {
    "F2": 87.31, "A2": 110.00, "C3": 130.81, "D3": 146.83,
    "E3": 164.81, "F3": 174.61, "G3": 196.00, "A3": 220.00,
    "B3": 246.94, "C4": 261.63,
}

# Diatonic to A minor, voiced so consecutive chords share at least two notes
# at the same octave -- the pad moves by voice-leading, not by lurching.
CHORDS = (
    ("A2", "C3", "E3", "G3", "A3"),  # Am7
    ("C3", "E3", "G3", "B3"),  # Cmaj7
    ("F2", "C3", "E3", "A3"),  # Fmaj7
    ("D3", "F3", "A3", "C3"),  # Dm7
)

_DUCK_VOICE_VOLUME = 3.2
_DUCK_THRESHOLD = 0.02
_DUCK_RATIO = 8
_DUCK_ATTACK = 20
_DUCK_RELEASE = 800
_SOLO_VOLUME = 1.6


def quantize(hz: float) -> float:
    """Nearest frequency completing a whole number of cycles in the loop."""
    return round(hz * LOOP_SECONDS) / LOOP_SECONDS


def _note_wave(hz: float, phase: float, frames: int) -> list[float]:
    """One note over the whole loop, as the sum of its partials."""
    out = [0.0] * frames
    for k, amp in enumerate(PARTIALS, start=1):
        step = 2.0 * math.pi * quantize(hz * k) / SAMPLE_RATE
        for i in range(frames):
            out[i] += amp * math.sin(step * i + phase)
    return out


def build_loop() -> list[tuple[float, float]]:
    """The bed, as stereo float frames covering exactly one loop."""
    frames = int(LOOP_SECONDS * SAMPLE_RATE)

    names = sorted({n for chord in CHORDS for n in chord})
    left = {n: _note_wave(NOTES[n], 0.0, frames) for n in names}
    right = {
        n: _note_wave(NOTES[n], math.pi * 2 * (i * 0.618 % 1.0), frames)
        for i, n in enumerate(names)
    }

    span = LOOP_SECONDS / len(CHORDS)
    raw: list[tuple[float, float]] = []
    for i in range(frames):
        t = i / SAMPLE_RATE
        acc_l = acc_r = 0.0
        for c, chord in enumerate(CHORDS):
            centre = c * span + span / 2.0
            window = math.cos(2.0 * math.pi * (t - centre) / LOOP_SECONDS)
            if window <= 0.0:
                continue
            gain = window * window / len(chord)
            for name in chord:
                acc_l += gain * left[name][i]
                acc_r += gain * right[name][i]
        raw.append((acc_l, acc_r))

    peak = max(max(abs(left), abs(right)) for left, right in raw)
    scale = TARGET_PEAK / peak if peak else 0.0
    return [(left * scale, right * scale) for left, right in raw]


def render_wav(frames: list[tuple[float, float]], target: Path) -> Path:
    """Write `frames` as a 16-bit PCM stereo wav at `target`."""
    target.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(target), "wb") as handle:
        handle.setnchannels(2)
        handle.setsampwidth(2)
        handle.setframerate(SAMPLE_RATE)
        handle.writeframes(b"".join(
            int(left * 32767).to_bytes(2, "little", signed=True)
            + int(right * 32767).to_bytes(2, "little", signed=True)
            for left, right in frames
        ))
    return target


def duck_filter(
    voice_volume: float = _DUCK_VOICE_VOLUME,
    threshold: float = _DUCK_THRESHOLD,
    ratio: int = _DUCK_RATIO,
    attack: int = _DUCK_ATTACK,
    release: int = _DUCK_RELEASE,
) -> str:
    """Sidechain the bed under the source's own (narration) audio track."""
    return (
        "[0:a]asplit=2[voz][key];"
        f"[1:a]aresample=48000,volume={voice_volume}[bed];"
        f"[bed][key]sidechaincompress=threshold={threshold}:ratio={ratio}:"
        f"attack={attack}:release={release}[ducked];"
        "[voz][ducked]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[out]"
    )


def solo_filter(volume: float = _SOLO_VOLUME) -> str:
    """A flat-level bed with nothing to key off (no synthetic narration)."""
    return f"[1:a]aresample=48000,volume={volume}[out]"


def mix_argv(video: Path, bed_wav: Path, out: Path, duck: bool) -> list[str]:
    """Mux `bed_wav` (looped) into `video`'s audio, ducked or at a flat level."""
    filter_complex = duck_filter() if duck else solo_filter()
    return [
        "ffmpeg", "-y",
        "-i", str(video),
        "-stream_loop", "-1", "-i", str(bed_wav),
        "-filter_complex", filter_complex,
        "-map", "0:v", "-map", "[out]",
        "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
        "-shortest", str(out),
    ]
