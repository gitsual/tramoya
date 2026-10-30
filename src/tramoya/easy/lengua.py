"""The language of what a person reads in `tramoya easy`: English by default, Spanish selectable.

Port of ``windows/Tramoya.Core/Lengua.cs``. The recorder (``ui/escucha.js``) still names elements
with the Spanish phrases it always used («el botón», «la casilla»); they are the internal role
names, and this module turns them into the words of the chosen language.
"""

from __future__ import annotations

import os

IDIOMAS = ("en", "es")


def normalizar(lang: str | None) -> str:
    lang = (lang or "").strip().lower()[:2]
    return lang if lang in IDIOMAS else "en"


_actual = normalizar(os.environ.get("TRAMOYA_LANG"))


def actual() -> str:
    return _actual


def fijar(lang: str | None) -> str:
    """Set the language; worker processes inherit it through ``TRAMOYA_LANG``."""
    global _actual
    _actual = normalizar(lang)
    os.environ["TRAMOYA_LANG"] = _actual
    return _actual


def t(es: str, en: str, lang: str | None = None) -> str:
    return es if normalizar(lang or _actual) == "es" else en


ROLES_EN = {
    "el botón": "button", "el enlace": "link", "la casilla": "checkbox", "el campo": "field",
    "la celda": "cell", "la columna": "column", "la imagen": "image", "la pestaña": "tab",
    "la opción del menú": "menu item", "la opción": "option", "el elemento": "item",
    "el interruptor": "switch", "el desplegable": "drop-down", "la fila": "row", "el título": "heading",
    "el buscador": "search box", "la lista": "list", "el texto": "text",
}
TECLAS_ES = {"Enter": "Intro", "Tab": "Tabulador", "Escape": "Esc", "Backspace": "Borrar",
             "ArrowDown": "flecha abajo", "ArrowUp": "flecha arriba"}
TECLAS_EN = {"Enter": "Enter", "Tab": "Tab", "Escape": "Esc", "Backspace": "Backspace",
             "ArrowDown": "down arrow", "ArrowUp": "up arrow"}


def cosa(rol: str, nombre: str, lang: str | None = None) -> str:
    """The thing a step acts on: «el botón «Guardar»» / «the “Save” button»."""
    if t("es", "en", lang) == "es":
        if nombre:
            return f"{rol} «{nombre}»" if rol else f"«{nombre}»"
        return rol or "este elemento"
    r = ROLES_EN.get(rol, "item") if rol else ""
    if nombre:
        return f"the “{nombre}” {r}" if r else f"“{nombre}”"
    return f"the {r}" if r else "this item"


def clic(c: str, marca: str = "", lang: str | None = None) -> str:
    if marca == "on":
        return t(f"Marca {c}", f"Check {c}", lang)
    if marca == "off":
        return t(f"Desmarca {c}", f"Uncheck {c}", lang)
    return t(f"Pulsa {c}", f"Click {c}", lang)


def cookies(desc: str, lang: str | None = None) -> str:
    return desc + t(" (aviso de cookies: si no sale, se salta)", " (cookie notice: skipped if it does not appear)", lang)


def escribe(valor: str, campo: str, lang: str | None = None) -> str:
    if campo:
        return t(f"Escribe «{valor}» en el campo «{campo}»", f"Type “{valor}” in the “{campo}” field", lang)
    return t(f"Escribe «{valor}»", f"Type “{valor}”", lang)


def elige(valor: str, donde: str, lang: str | None = None) -> str:
    if donde:
        return t(f"Elige «{valor}» en «{donde}»", f"Choose “{valor}” in “{donde}”", lang)
    return t(f"Elige «{valor}»", f"Choose “{valor}”", lang)


def tecla(k: str, lang: str | None = None) -> str:
    return t(f"Pulsa la tecla {TECLAS_ES.get(k, k)}", f"Press {TECLAS_EN.get(k, k)}", lang)
