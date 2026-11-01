"""Which model answers: a command-line flag, then the window's settings, then the environment, then defaults.

Port of ``windows/Tramoya.Core/Ia.cs``. The settings keys (in ``ajustes.json``) are ``ia_backend``
("", "off", "ollama", "claude" or "openai"), ``ia_modelo``, ``ia_url`` (Ollama's host or the
OpenAI-compatible base URL, ending in ``/v1``) and ``ia_clave`` (the API key, never sent back to the
window, never printed).
"""

from __future__ import annotations

import json
import os
import time
import urllib.request
from collections.abc import Callable, Mapping
from dataclasses import dataclass

BACKENDS = ("ollama", "claude", "openai")
DEFAULT_OLLAMA_MODEL = "qwen3-coder:30b"
DEFAULT_OLLAMA_URL = "http://localhost:11434"
DEFAULT_CLAUDE_MODEL = "claude-sonnet-5-5"
DEFAULT_OPENAI_MODEL = "gpt-4o-mini"
CLAUDE_URL = "https://api.anthropic.com/v1/messages"
K_BACKEND, K_MODELO, K_URL, K_CLAVE = "ia_backend", "ia_modelo", "ia_url", "ia_clave"


@dataclass(frozen=True)
class Elegida:
    backend: str
    model: str
    url: str
    key: str

    def __repr__(self) -> str:  # the key is never printed, only whether there is one
        return f"Elegida({self.backend!r}, {self.model!r}, {self.url!r}, key={'set' if self.key else 'none'})"


def _str(ajustes: Mapping | None, key: str) -> str:
    v = (ajustes or {}).get(key)
    return v.strip() if isinstance(v, str) else ""


def _first(*values: str | None) -> str | None:
    for v in values:
        if v and v.strip():
            return v.strip()
    return None


def url_ollama(host: str | None) -> str:
    """OLLAMA_HOST often comes without a scheme or as a listening address: make it a URL that connects."""
    if not host or not host.strip():
        return DEFAULT_OLLAMA_URL
    url = host.strip()
    if "://" not in url:
        url = "http://" + url
    url = url.replace("://0.0.0.0", "://127.0.0.1").replace("://[::]", "://localhost").replace("://::", "://localhost")
    return url.rstrip("/")


def apagada(ajustes: Mapping | None) -> bool:
    return _str(ajustes, K_BACKEND) == "off"


def elegir(flag_backend: str | None, flag_model: str | None, ajustes: Mapping | None,
           env: Mapping[str, str] | None = None) -> Elegida:
    """flag > settings > environment > default. A flag naming another backend than the saved one
    does not inherit the saved model, URL or key, which belong to the saved backend."""
    env = os.environ if env is None else env

    def e(k: str) -> str | None:
        v = env.get(k)
        return v.strip() if v and v.strip() else None

    guardado = _str(ajustes, K_BACKEND)
    if guardado == "auto":
        guardado = ""
    backend = (_first(flag_backend, guardado, e("TRAMOYA_LLM"))
               or ("claude" if e("ANTHROPIC_API_KEY") else "ollama")).lower()
    if backend == "off":
        return Elegida("off", "", "", "")
    if backend not in BACKENDS:
        raise ValueError(f"unknown AI backend {backend!r}: use ollama, claude or openai")
    mismo = not guardado or guardado == backend

    def g(k: str) -> str | None:
        return _first(_str(ajustes, k)) if mismo else None

    model = _first(flag_model, g(K_MODELO), e("TRAMOYA_LLM_MODEL"))
    if backend == "claude":
        return Elegida("claude", model or DEFAULT_CLAUDE_MODEL, "", _first(g(K_CLAVE), e("ANTHROPIC_API_KEY")) or "")
    if backend == "openai":
        return Elegida("openai", model or DEFAULT_OPENAI_MODEL,
                       (_first(g(K_URL), e("OPENAI_BASE_URL")) or "").rstrip("/"),
                       _first(g(K_CLAVE), e("OPENAI_API_KEY")) or "")
    return Elegida("ollama", model or DEFAULT_OLLAMA_MODEL, url_ollama(_first(g(K_URL), e("OLLAMA_HOST"))), "")


def publicos(ajustes: Mapping) -> dict:
    """The settings as the window may see them: no key, only whether there is one and its end."""
    clave = _str(ajustes, K_CLAVE)
    return {**ajustes, K_CLAVE: "", "ia_clave_guardada": bool(clave),
            "ia_clave_fin": clave[-4:] if len(clave) >= 8 else ""}


def sin_clave(ajustes: Mapping) -> dict:
    return {k: v for k, v in ajustes.items() if k != K_CLAVE}


# --- the window's «Test connection» and model list -------------------------------


def _get_json(url: str, headers: dict, timeout: float = 8) -> dict:
    req = urllib.request.Request(url, headers=headers)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def modelos(ia: Elegida, get: Callable[[str, dict], dict] = _get_json) -> list[str]:
    if ia.backend == "ollama":
        return sorted(m["name"] for m in get(f"{ia.url}/api/tags", {}).get("models", []))
    if ia.backend == "openai":
        headers = {"Authorization": f"Bearer {ia.key}"} if ia.key else {}
        return sorted(m["id"] for m in get(f"{ia.url}/models", headers).get("data", []))
    if ia.backend == "claude":
        data = get("https://api.anthropic.com/v1/models?limit=100",
                   {"x-api-key": ia.key, "anthropic-version": "2023-06-01"})
        return sorted(m["id"] for m in data.get("data", []))
    raise ValueError("the AI is switched off")


def probar(ia: Elegida, post: Callable[[str, dict, dict], dict] | None = None) -> dict:
    """One short question: which model answered, what it said, how long it took."""
    from tramoya.assistant import http_post

    post = post or http_post
    pregunta = "Reply with the single word OK."
    inicio = time.monotonic()
    if ia.backend == "claude":
        if not ia.key:
            raise ValueError("the API key is missing")
        reply = post(CLAUDE_URL, {"model": ia.model, "max_tokens": 16,
                                  "messages": [{"role": "user", "content": pregunta}]},
                     {"x-api-key": ia.key, "anthropic-version": "2023-06-01"})
        texto = "".join(b.get("text", "") for b in reply.get("content", []))
    elif ia.backend == "openai":
        reply = post(f"{ia.url}/chat/completions", {"model": ia.model, "max_tokens": 16,
                                                     "messages": [{"role": "user", "content": pregunta}]},
                     {"Authorization": f"Bearer {ia.key}"} if ia.key else {})
        texto = reply["choices"][0]["message"]["content"]
    elif ia.backend == "ollama":
        reply = post(f"{ia.url}/api/chat", {"model": ia.model, "stream": False, "think": False,
                                             "messages": [{"role": "user", "content": pregunta}]}, {})
        texto = reply["message"]["content"]
    else:
        raise ValueError("the AI is switched off")
    return {"ok": True, "backend": ia.backend, "modelo": reply.get("model") or ia.model,
            "respuesta": (texto or "").strip()[:80], "ms": round((time.monotonic() - inicio) * 1000)}
