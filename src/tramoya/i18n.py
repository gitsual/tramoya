"""Small message-table translator, used for the narrator and console output."""

from __future__ import annotations


class Messages:
    """Look up a key in a `{key: {lang: text}}` table and `.format(**kw)` it.

    Falls back to `default_lang` when the requested language has no entry
    for a key, and to the bare key itself when the key is missing entirely.
    """

    def __init__(self, table: dict[str, dict[str, str]], default_lang: str = "en") -> None:
        self._table: dict[str, dict[str, str]] = dict(table)
        self.default_lang = default_lang

    def register(self, table: dict[str, dict[str, str]]) -> None:
        """Merge another table into this one, overwriting existing keys."""
        self._table.update(table)

    def t(self, key: str, lang: str | None = None, **kw: object) -> str:
        entry = self._table.get(key)
        if entry is None:
            return key
        effective_lang = lang if lang is not None else self.default_lang
        template = entry.get(effective_lang, entry.get(self.default_lang, key))
        return template.format(**kw)
