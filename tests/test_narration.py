from __future__ import annotations

import wave
from pathlib import Path

import pytest

from tramoya.narration import DeckScript, Narrator, Script, wav_seconds
from tramoya.pacing import Pacing


def _write_silent_wav(path: Path, seconds: float = 0.2, framerate: int = 16000) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    n_frames = int(seconds * framerate)
    with wave.open(str(path), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(framerate)
        wav.writeframes(b"\x00\x00" * n_frames)


# --- Script ------------------------------------------------------------


def test_script_text_returns_text_for_key_and_lang() -> None:
    script = Script({"intro": {"es": "Hola", "en": "Hello"}})
    assert script.text("intro", "es") == "Hola"
    assert script.text("intro", "en") == "Hello"


def test_script_keys() -> None:
    script = Script({"intro": {"en": "Hello"}, "outro": {"en": "Bye"}})
    assert set(script.keys()) == {"intro", "outro"}


def test_script_supports_len_and_contains() -> None:
    script = Script({"intro": {"en": "Hello"}})
    assert len(script) == 1
    assert "intro" in script
    assert "missing" not in script


# --- DeckScript ----------------------------------------------------------


def test_deck_script_pages_and_text() -> None:
    deck = DeckScript({"en": [("cover", "Cover text"), ("agenda", "Agenda text")]})
    assert deck.pages("en") == ["cover", "agenda"]
    assert deck.text("cover", "en") == "Cover text"
    assert deck.text("agenda", "en") == "Agenda text"


# --- wav_seconds -----------------------------------------------------------


def test_wav_seconds_reads_duration(tmp_path: Path) -> None:
    path = tmp_path / "clip.wav"
    _write_silent_wav(path, seconds=0.5, framerate=16000)
    assert wav_seconds(path) == pytest.approx(0.5, abs=0.01)


def test_wav_seconds_returns_zero_for_missing_file(tmp_path: Path) -> None:
    assert wav_seconds(tmp_path / "missing.wav") == 0.0


def test_wav_seconds_returns_zero_for_corrupt_file(tmp_path: Path) -> None:
    path = tmp_path / "bad.wav"
    path.write_bytes(b"not a wav file")
    assert wav_seconds(path) == 0.0


# --- Narrator ----------------------------------------------------------


class _FakePlayer:
    def __init__(self) -> None:
        self.calls: list[Path] = []
        self.waited: list[Path] = []

    def __call__(self, path: Path):
        self.calls.append(path)
        return _FakeProc(path, self.waited)


class _FakeProc:
    def __init__(self, path: Path, waited: list[Path]) -> None:
        self._path = path
        self._waited = waited

    def wait(self) -> None:
        self._waited.append(self._path)


def test_narrator_plays_clip_and_waits_for_duration(tmp_path: Path) -> None:
    clip = tmp_path / "en" / "intro.wav"
    _write_silent_wav(clip)
    player = _FakePlayer()
    pacing = Pacing(sleep=lambda s: None)
    narrator = Narrator(voice_dir=tmp_path, pacing=pacing, player=player)

    duration = narrator.say("intro", fallback=5.0, wait=True)

    assert player.calls == [clip]
    assert player.waited == [clip]
    assert duration == pytest.approx(0.2, abs=0.05)


def test_narrator_falls_back_to_pacing_sleep_when_no_clip(tmp_path: Path) -> None:
    slept: list[float] = []
    pacing = Pacing(sleep=lambda s: slept.append(s))
    player = _FakePlayer()
    narrator = Narrator(voice_dir=tmp_path, pacing=pacing, player=player)

    narrator.say("missing-key", fallback=3.0, wait=True)

    assert player.calls == []
    assert slept == [3.0]


def test_narrator_no_wait_does_not_block_immediately(tmp_path: Path) -> None:
    clip = tmp_path / "en" / "intro.wav"
    _write_silent_wav(clip)
    player = _FakePlayer()
    pacing = Pacing(sleep=lambda s: None)
    narrator = Narrator(voice_dir=tmp_path, pacing=pacing, player=player)

    narrator.say("intro", wait=False)

    assert player.calls == [clip]
    assert player.waited == []


def test_narrator_rehearsal_skips_wait_even_when_wait_true(tmp_path: Path) -> None:
    clip = tmp_path / "en" / "intro.wav"
    _write_silent_wav(clip)
    player = _FakePlayer()
    pacing = Pacing(rehearse=True, sleep=lambda s: None)
    narrator = Narrator(voice_dir=tmp_path, pacing=pacing, player=player)

    narrator.say("intro", wait=True)

    assert player.calls == [clip]
    assert player.waited == []


def test_narrator_wait_pending_blocks_on_previous_no_wait_clip(tmp_path: Path) -> None:
    clip = tmp_path / "en" / "intro.wav"
    _write_silent_wav(clip)
    player = _FakePlayer()
    pacing = Pacing(sleep=lambda s: None)
    narrator = Narrator(voice_dir=tmp_path, pacing=pacing, player=player)

    narrator.say("intro", wait=False)
    assert player.waited == []
    narrator.wait_pending()
    assert player.waited == [clip]


def test_narrator_clip_looks_up_lang_subdir(tmp_path: Path) -> None:
    clip_en = tmp_path / "en" / "hello.wav"
    _write_silent_wav(clip_en)
    pacing_en = Pacing(lang="en", sleep=lambda s: None)
    narrator = Narrator(voice_dir=tmp_path, pacing=pacing_en)
    assert narrator.clip("hello") == clip_en

    pacing_es = Pacing(lang="es", sleep=lambda s: None)
    narrator_es = Narrator(voice_dir=tmp_path, pacing=pacing_es)
    assert narrator_es.clip("hello") is None
