"""Which model answers: flag > the window's settings > environment > defaults, and the key never leaks."""

from __future__ import annotations

import pytest

from tramoya import ia
from tramoya.assistant import ClaudeBackend, OllamaBackend, OpenAIBackend, pick_backend


def test_with_nothing_set_it_is_a_local_ollama() -> None:
    assert ia.elegir(None, None, None, {}) == ia.Elegida("ollama", "qwen3-coder:30b", "http://localhost:11434", "")


def test_settings_weigh_more_than_the_environment_and_less_than_a_flag() -> None:
    env = {"TRAMOYA_LLM": "ollama", "TRAMOYA_LLM_MODEL": "env-model", "OLLAMA_HOST": "0.0.0.0:9999"}
    assert ia.elegir(None, None, None, env) == ia.Elegida("ollama", "env-model", "http://127.0.0.1:9999", "")
    ajustes = {"ia_backend": "ollama", "ia_modelo": "qwen3:14b", "ia_url": "http://gpu.lan:11434/"}
    assert ia.elegir(None, None, ajustes, env) == ia.Elegida("ollama", "qwen3:14b", "http://gpu.lan:11434", "")
    assert ia.elegir(None, "small", ajustes, env).model == "small"
    # another backend by flag does not inherit the saved backend's model or address
    claude = ia.elegir("claude", None, ajustes, {"ANTHROPIC_API_KEY": "sk-env"})
    assert claude == ia.Elegida("claude", ia.DEFAULT_CLAUDE_MODEL, "", "sk-env")


def test_the_saved_key_wins_and_openai_needs_its_address() -> None:
    env = {"ANTHROPIC_API_KEY": "sk-env"}
    assert ia.elegir(None, None, {"ia_backend": "claude", "ia_clave": "sk-saved"}, env).key == "sk-saved"
    assert isinstance(pick_backend(None, env, ajustes={"ia_backend": "claude", "ia_clave": "k"}), ClaudeBackend)
    openai = pick_backend(None, {}, ajustes={"ia_backend": "openai", "ia_url": "http://x/v1/", "ia_modelo": "m"})
    assert isinstance(openai, OpenAIBackend) and openai.base_url == "http://x/v1" and openai.model == "m"
    with pytest.raises(ValueError, match="server address"):
        pick_backend(None, {}, ajustes={"ia_backend": "openai"})
    assert isinstance(pick_backend("ollama", {}, ajustes={"ia_backend": "off"}), OllamaBackend)
    with pytest.raises(ValueError, match="switched off"):
        pick_backend(None, {}, ajustes={"ia_backend": "off"})


def test_the_key_never_goes_back_to_the_window() -> None:
    a = {"ia_backend": "claude", "ia_clave": "sk-ant-abcdefgh1234", "voz": "x"}
    pub = ia.publicos(a)
    assert pub["ia_clave"] == "" and pub["ia_clave_guardada"] and pub["ia_clave_fin"] == "1234"
    assert "ia_clave" not in ia.sin_clave(a)
    assert "sk-ant" not in repr(ia.elegir(None, None, a, {}))


def test_settings_round_trip_keeps_the_key_when_the_field_is_empty(tmp_path, monkeypatch) -> None:
    import tramoya.easy.app as app

    monkeypatch.setattr(app, "AJUSTES", tmp_path / "ajustes.json")
    app.guardar_ajustes({"ia_backend": "openai", "ia_url": "http://127.0.0.1:1234/v1", "ia_modelo": "m",
                         "ia_clave": "sk-1234567890"})
    app.guardar_ajustes({"ia_modelo": "n", "ia_clave": ""})
    assert app.leer_ajustes()["ia_clave"] == "sk-1234567890"
    app.guardar_ajustes({"ia_clave_borrar": True})
    guardados = app.leer_ajustes()
    assert guardados["ia_clave"] == "" and guardados["ia_modelo"] == "n"
    assert ia.elegir(None, None, guardados, {}) == ia.Elegida("openai", "n", "http://127.0.0.1:1234/v1", "")


def test_test_connection_says_which_model_answered() -> None:
    llamadas = []

    def post(url, payload, headers):
        llamadas.append((url, headers))
        return {"model": "served", "choices": [{"message": {"content": " OK "}}]}

    r = ia.probar(ia.Elegida("openai", "asked", "http://srv/v1", "sk-x"), post)
    assert r["ok"] and r["modelo"] == "served" and r["respuesta"] == "OK"
    assert llamadas == [("http://srv/v1/chat/completions", {"Authorization": "Bearer sk-x"})]
