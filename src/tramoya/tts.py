"""Narration synthesis with Kokoro.

kokoro, soundfile and numpy are optional extras: nothing at module import time
touches them. A caller who never invokes `synthesize`/`synthesize_script`
needs none of the three installed, and tests exercise the whole module by
injecting a `pipeline_factory` shaped like `kokoro.KPipeline`.
"""

from __future__ import annotations

import warnings
import wave
from array import array
from collections.abc import Callable, Iterable
from pathlib import Path
from typing import Any

VOICES: dict[str, tuple[str, str]] = {"es": ("e", "ef_dora"), "en": ("a", "af_heart")}
SAMPLE_RATE = 24000
_REPO_ID = "hexgrad/Kokoro-82M"

PipelineFactory = Callable[[str, str], Any]


def _to_samples(audio: Iterable[float]) -> list[float]:
    """Accept anything iterable of numbers -- a numpy array when numpy is
    present, or a plain list/tuple in tests that avoid the dependency."""
    return [float(sample) for sample in audio]


def _write_wav(path: Path, samples: list[float], sample_rate: int = SAMPLE_RATE) -> None:
    """Write 16-bit mono PCM with the stdlib `wave` module, so this works
    even when soundfile/numpy are absent (only `synthesize`'s caller needs
    them, and only when it wants soundfile's writer specifically -- this
    path never does)."""
    path.parent.mkdir(parents=True, exist_ok=True)
    clipped = (max(-1.0, min(1.0, sample)) for sample in samples)
    ints = array("h", (int(sample * 32767) for sample in clipped))
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(sample_rate)
        handle.writeframes(ints.tobytes())


def _default_pipeline_factory(lang_code: str, repo_id: str) -> Any:
    from kokoro import KPipeline

    return KPipeline(lang_code=lang_code, repo_id=repo_id)


def synthesize(
    text: str,
    lang: str,
    out: str | Path,
    voices: dict[str, tuple[str, str]] = VOICES,
    gap_seconds: float = 0.35,
    tail_seconds: float = 0.6,
    pipeline_factory: PipelineFactory | None = None,
) -> Path:
    """Synthesize `text` in `lang` to a 24kHz mono wav at `out`.

    Kokoro splits long text into several utterances and yields one
    `(graphemes, phonemes, audio)` tuple per sentence; a short silence is
    inserted between them so the concatenation doesn't run words together,
    plus a fixed silence at the end of the clip.
    """
    out_path = Path(out)
    lang_code, voice = voices[lang]
    factory = pipeline_factory or _default_pipeline_factory

    gap = [0.0] * int(SAMPLE_RATE * gap_seconds)
    tail = [0.0] * int(SAMPLE_RATE * tail_seconds)

    samples: list[float] = []
    # Kokoro's torch stack prints deprecation notices on every load; they are
    # nothing the person running `tramoya tts` can act on.
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        pipeline = factory(lang_code, _REPO_ID)
        for index, (_graphemes, _phonemes, audio) in enumerate(pipeline(text, voice=voice)):
            if index:
                samples.extend(gap)
            samples.extend(_to_samples(audio))
    samples.extend(tail)

    _write_wav(out_path, samples)
    return out_path


def synthesize_script(
    script: dict[str, dict[str, str]],
    lang: str,
    out_dir: str | Path,
    voices: dict[str, tuple[str, str]] = VOICES,
    gap_seconds: float = 0.35,
    tail_seconds: float = 0.6,
    pipeline_factory: PipelineFactory | None = None,
) -> list[Path]:
    """Synthesize every key of `script` in `lang`, one wav per key under
    `out_dir/<key>.wav`."""
    out_dir_path = Path(out_dir)
    paths: list[Path] = []
    for key, texts in script.items():
        out = out_dir_path / f"{key}.wav"
        synthesize(
            texts[lang],
            lang,
            out,
            voices=voices,
            gap_seconds=gap_seconds,
            tail_seconds=tail_seconds,
            pipeline_factory=pipeline_factory,
        )
        paths.append(out)
    return paths
