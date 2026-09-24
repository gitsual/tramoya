"""Tests for tramoya.tts: narration synthesis with an injected Kokoro-shaped
pipeline factory, so the tests never need kokoro/soundfile/numpy installed."""

from __future__ import annotations

import wave

from tramoya.tts import VOICES, synthesize, synthesize_script


class _FakePipeline:
    """Stands in for `kokoro.KPipeline`: callable, yields (graphemes,
    phonemes, audio) tuples, audio is a plain list of floats."""

    def __init__(self, lang_code: str, repo_id: str) -> None:
        self.lang_code = lang_code
        self.repo_id = repo_id
        self.calls: list[tuple[str, str]] = []

    def __call__(self, text: str, voice: str):
        self.calls.append((text, voice))
        # two "sentences" so the gap-insertion path is exercised
        yield ("g1", "p1", [0.1, 0.2, 0.3])
        yield ("g2", "p2", [0.4, 0.5])


def _factory(store: dict):
    def make(lang_code: str, repo_id: str):
        pipeline = _FakePipeline(lang_code, repo_id)
        store["pipeline"] = pipeline
        return pipeline

    return make


def test_voices_has_spanish_and_english_defaults():
    assert VOICES["es"] == ("e", "ef_dora")
    assert VOICES["en"] == ("a", "af_heart")


def test_synthesize_writes_a_readable_wav_file(tmp_path):
    store: dict = {}
    out = tmp_path / "clip.wav"
    result = synthesize(
        "hello there",
        "en",
        out,
        gap_seconds=0.1,
        tail_seconds=0.1,
        pipeline_factory=_factory(store),
    )
    assert result == out
    assert out.exists()
    with wave.open(str(out), "rb") as f:
        assert f.getnchannels() == 1
        assert f.getframerate() == 24000
        assert f.getnframes() > 0
    assert store["pipeline"].lang_code == "a"  # VOICES["en"][0]
    assert store["pipeline"].calls == [("hello there", "af_heart")]


def test_synthesize_uses_the_requested_language_voice(tmp_path):
    store: dict = {}
    out = tmp_path / "clip-es.wav"
    synthesize(
        "hola",
        "es",
        out,
        gap_seconds=0.05,
        tail_seconds=0.05,
        pipeline_factory=_factory(store),
    )
    assert store["pipeline"].lang_code == "e"
    assert store["pipeline"].calls == [("hola", "ef_dora")]


def test_synthesize_creates_parent_directories(tmp_path):
    store: dict = {}
    out = tmp_path / "nested" / "dir" / "clip.wav"
    synthesize("hi", "en", out, pipeline_factory=_factory(store))
    assert out.exists()


def test_synthesize_script_writes_one_file_per_key(tmp_path):
    store: dict = {}
    script = {
        "intro": {"en": "welcome", "es": "bienvenido"},
        "outro": {"en": "goodbye", "es": "adios"},
    }
    out_dir = tmp_path / "tts"
    paths = synthesize_script(script, "en", out_dir, pipeline_factory=_factory(store))
    assert sorted(p.name for p in paths) == ["intro.wav", "outro.wav"]
    for p in paths:
        assert p.exists()


def test_synthesize_script_picks_the_requested_language_text(tmp_path):
    calls: list[str] = []

    class RecordingPipeline(_FakePipeline):
        def __call__(self, text: str, voice: str):
            calls.append(text)
            yield from super().__call__(text, voice)

    def factory(lang_code: str, repo_id: str):
        return RecordingPipeline(lang_code, repo_id)

    script = {"only": {"en": "english text", "es": "texto en español"}}
    synthesize_script(script, "es", tmp_path / "tts", pipeline_factory=factory)
    assert calls == ["texto en español"]
