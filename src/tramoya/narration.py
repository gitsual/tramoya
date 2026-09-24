"""Narration: scripted text keyed by moment, and voice clip playback."""

from __future__ import annotations

import subprocess
import wave
from collections.abc import Callable, Iterator, Mapping
from pathlib import Path
from typing import Any

from tramoya.pacing import Pacing

_TAIL_SECONDS = 0.5


class Script(Mapping[str, dict[str, str]]):
    """A narration script keyed by moment: `key -> {lang: text}`."""

    def __init__(self, table: dict[str, dict[str, str]]) -> None:
        self._table = dict(table)

    def text(self, key: str, lang: str) -> str:
        return self._table[key][lang]

    def __getitem__(self, key: str) -> dict[str, str]:
        return self._table[key]

    def __iter__(self) -> Iterator[str]:
        return iter(self._table)

    def __len__(self) -> int:
        return len(self._table)


class DeckScript:
    """A per-page narration script: `lang -> [(page, text), ...]`."""

    def __init__(self, table: dict[str, list[tuple[str, str]]]) -> None:
        self._table = {lang: list(pages) for lang, pages in table.items()}

    def pages(self, lang: str) -> list[str]:
        return [page for page, _text in self._table.get(lang, [])]

    def text(self, page: str, lang: str) -> str:
        for candidate_page, text in self._table.get(lang, []):
            if candidate_page == page:
                return text
        raise KeyError(page)


def wav_seconds(path: Path) -> float:
    """Duration of a WAV file in seconds, or 0.0 if it is missing or unreadable."""
    try:
        with wave.open(str(path), "rb") as wav:
            return wav.getnframes() / float(wav.getframerate() or 1)
    except (wave.Error, OSError):
        return 0.0


def _default_player(path: Path) -> subprocess.Popen:
    return subprocess.Popen(
        ["paplay", str(path)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL
    )


class Narrator:
    """Plays a voice clip for a narration key, or falls back to a paced sleep."""

    def __init__(
        self,
        voice_dir: Path,
        pacing: Pacing,
        player: Callable[[Path], Any] | None = None,
    ) -> None:
        self.voice_dir = Path(voice_dir)
        self.pacing = pacing
        self._player = player if player is not None else _default_player
        self._pending: Any | None = None

    def clip(self, key: str) -> Path | None:
        path = self.voice_dir / self.pacing.lang / f"{key}.wav"
        if path.is_file():
            return path
        return None

    def wait_pending(self) -> None:
        if self._pending is not None:
            self._pending.wait()
            self._pending = None

    def say(self, key: str, fallback: float = 0.0, wait: bool = True) -> float:
        self.wait_pending()
        clip = self.clip(key)
        if clip is None:
            self.pacing.sleep(fallback)
            return fallback
        seconds = wav_seconds(clip)
        proc = self._player(clip)
        if wait and not self.pacing.rehearse:
            proc.wait()
        else:
            self._pending = proc
        return seconds
