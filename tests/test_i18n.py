from __future__ import annotations

import pytest

from tramoya.i18n import Messages


def test_t_returns_text_for_requested_lang() -> None:
    messages = Messages({"greet": {"es": "Hola", "en": "Hello"}})
    assert messages.t("greet", lang="es") == "Hola"
    assert messages.t("greet", lang="en") == "Hello"


def test_t_uses_default_lang_when_none_given() -> None:
    messages = Messages({"greet": {"es": "Hola", "en": "Hello"}}, default_lang="es")
    assert messages.t("greet") == "Hola"


def test_t_falls_back_to_default_lang_when_missing() -> None:
    messages = Messages({"greet": {"es": "Hola"}}, default_lang="es")
    assert messages.t("greet", lang="en") == "Hola"


def test_t_falls_back_to_key_when_totally_missing() -> None:
    messages = Messages({}, default_lang="es")
    assert messages.t("unknown.key") == "unknown.key"


def test_t_formats_with_kwargs() -> None:
    messages = Messages({"pace": {"en": "--pace {p} ~= {minutes:.1f} min"}})
    assert messages.t("pace", lang="en", p=2, minutes=1.5) == "--pace 2 ~= 1.5 min"


def test_register_merges_table() -> None:
    messages = Messages({"a": {"en": "A"}})
    messages.register({"b": {"en": "B"}})
    assert messages.t("a", lang="en") == "A"
    assert messages.t("b", lang="en") == "B"


def test_register_overwrites_existing_key() -> None:
    messages = Messages({"a": {"en": "A"}})
    messages.register({"a": {"en": "A2"}})
    assert messages.t("a", lang="en") == "A2"


@pytest.mark.parametrize("default_lang", ["es", "en"])
def test_default_lang_is_configurable(default_lang: str) -> None:
    messages = Messages({"k": {"es": "ES", "en": "EN"}}, default_lang=default_lang)
    assert messages.t("k") == {"es": "ES", "en": "EN"}[default_lang]
