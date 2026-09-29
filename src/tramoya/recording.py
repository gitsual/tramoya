"""Recordings into scenes.

Two formats nobody has to install anything for: the JSON that Chrome's
DevTools Recorder panel exports, and the Python that `playwright codegen`
writes. Both are read into the step language of `tramoya.profile`, one scene
per navigation, each ending in `say` so the narration has a place to go.
Selectors are kept as the recorder chose them, preferring CSS, then visible
text, then a role; XPath is used only when nothing else was offered.
"""

from __future__ import annotations

import ast
import json
import re
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

from tramoya.profile import Scene


def _scene_name(url: str, index: int) -> str:
    path = urlparse(url).path.rsplit("/", 1)[-1]
    stem = re.sub(r"\.[a-z0-9]+$", "", path)
    stem = re.sub(r"[^a-z0-9]+", "-", stem.lower()).strip("-")
    return stem or ("index" if index == 0 else f"page-{index + 1}")


def _relative(url: str) -> str:
    parsed = urlparse(url)
    return (parsed.path or "/") + (f"?{parsed.query}" if parsed.query else "")


def _finish(scenes: list[Scene], current: Scene | None) -> None:
    if current is not None:
        if not current.steps or current.steps[-1] != "say":
            current.steps.append("say")
        scenes.append(current)


# --- Chrome DevTools Recorder ---------------------------------------------


def _pick_selector(selectors: list[list[str]]) -> tuple[str, str] | None:
    """(kind, value): kind is `css`, `text` or `xpath`. Text beats aria because
    the aria name is a role guess; both beat xpath; pierce is shadow-DOM only."""
    css = text = aria = xpath = None
    for group in selectors:
        for sel in group:
            if sel.startswith("aria/"):
                aria = aria or sel[5:].split("[", 1)[0]
            elif sel.startswith("text/"):
                text = text or sel[5:]
            elif sel.startswith("xpath/"):
                xpath = xpath or sel[6:]
            elif sel.startswith("pierce/"):
                continue
            else:
                css = css or sel
    if css:
        return "css", css
    if text:
        return "text", text
    if aria:
        return "text", aria
    if xpath:
        return "xpath", xpath
    return None


def _click_step(kind: str, value: str) -> str:
    if kind == "text":
        return f"click_text {value}"
    if kind == "xpath":
        return f"click xpath={value}"
    return f"click {value}"


def _target(kind: str, value: str) -> str:
    """A selector usable by `type`/`select` (which need a locator, not text)."""
    if kind == "text":
        return f"text={value}"
    if kind == "xpath":
        return f"xpath={value}"
    return value


def scenes_from_devtools(doc: dict[str, Any]) -> list[Scene]:
    scenes: list[Scene] = []
    current: Scene | None = None
    for step in doc.get("steps", []):
        kind = step.get("type")
        if kind == "navigate":
            url = str(step.get("url", ""))
            _finish(scenes, current)
            name = _scene_name(url, len(scenes))
            current = Scene(name, name, {}, [] if not scenes else [f"goto {_relative(url)}"])
            continue
        if kind not in ("click", "change", "keyDown", "scroll"):
            continue
        if current is None:
            current = Scene("recorded", "recorded", {}, [])
        if kind == "click":
            picked = _pick_selector(step.get("selectors", []))
            if picked:
                current.steps.append(_click_step(*picked))
        elif kind == "change":
            picked = _pick_selector(step.get("selectors", []))
            if picked:
                target = _target(*picked)
                verb = "select" if _looks_like_select(picked) else "type"
                current.steps.append(f"{verb} {target} | {step.get('value', '')}")
        elif kind == "keyDown":
            current.steps.append(f"press {step.get('key', '')}")
        elif kind == "scroll":
            dy = int(step.get("y", 0) or 0)
            if dy:
                current.steps.append(f"scroll {dy}")
    _finish(scenes, current)
    return scenes


def _looks_like_select(picked: tuple[str, str]) -> bool:
    kind, value = picked
    return kind == "css" and (value.startswith("#tag") or "select" in value.lower())


# --- playwright codegen -----------------------------------------------------

_LOCATOR = re.compile(r'page\.locator\("((?:[^"\\]|\\.)*)"\)')
_ROLE = re.compile(r'page\.get_by_role\("(\w+)",\s*name="((?:[^"\\]|\\.)*)"')
_TEXT = re.compile(r'page\.get_by_text\("((?:[^"\\]|\\.)*)"')
_LABEL = re.compile(r'page\.get_by_label\("((?:[^"\\]|\\.)*)"')
_PLACEHOLDER = re.compile(r'page\.get_by_placeholder\("((?:[^"\\]|\\.)*)"')
_ACTION = re.compile(r'\.(click|fill|select_option|press|check|type)\((.*)\)\s*$')
_GOTO = re.compile(r'page\.goto\("((?:[^"\\]|\\.)*)"\)')


def _unquote(text: str) -> str:
    try:
        return str(ast.literal_eval(text))
    except (ValueError, SyntaxError):
        return text.strip('"')


def _locator_of(line: str) -> str | None:
    if m := _LOCATOR.search(line):
        return _unquote(f'"{m.group(1)}"')
    if m := _ROLE.search(line):
        return f'role={m.group(1)}[name="{_unquote(chr(34) + m.group(2) + chr(34))}"]'
    if m := _PLACEHOLDER.search(line):
        return f'[placeholder="{_unquote(chr(34) + m.group(1) + chr(34))}"]'
    if m := _LABEL.search(line):
        return f'role=textbox[name="{_unquote(chr(34) + m.group(1) + chr(34))}"]'
    return None


def scenes_from_codegen(source: str) -> list[Scene]:
    scenes: list[Scene] = []
    current: Scene | None = None
    for raw in source.splitlines():
        line = raw.strip()
        if not line.startswith("page."):
            continue
        if m := _GOTO.match(line):
            url = _unquote(f'"{m.group(1)}"')
            _finish(scenes, current)
            name = _scene_name(url, len(scenes))
            current = Scene(name, name, {}, [] if not scenes else [f"goto {_relative(url)}"])
            continue
        if current is None:
            current = Scene("recorded", "recorded", {}, [])
        action = _ACTION.search(line)
        if not action:
            continue
        verb, arg = action.group(1), action.group(2)
        if verb == "press":
            current.steps.append(f"press {_unquote(arg)}")
            continue
        if m := _TEXT.search(line):
            if verb == "click":
                current.steps.append(f"click_text {_unquote(chr(34) + m.group(1) + chr(34))}")
                continue
            locator = f"text={_unquote(chr(34) + m.group(1) + chr(34))}"
        else:
            locator = _locator_of(line)
        if locator is None:
            continue
        if verb in ("click", "check"):
            current.steps.append(f"click {locator}")
        elif verb in ("fill", "type"):
            current.steps.append(f"type {locator} | {_unquote(arg)}")
        elif verb == "select_option":
            current.steps.append(f"select {locator} | {_unquote(arg)}")
    _finish(scenes, current)
    return scenes


def scenes_from_file(path: str | Path) -> list[Scene]:
    text = Path(path).read_text(encoding="utf-8")
    stripped = text.lstrip()
    if stripped.startswith("{"):
        doc = json.loads(text)
        if isinstance(doc, dict) and "steps" in doc:
            return scenes_from_devtools(doc)
    if "page." in text:
        return scenes_from_codegen(text)
    raise ValueError(f"{path} is neither a DevTools Recorder export nor a playwright codegen "
                     "script")
