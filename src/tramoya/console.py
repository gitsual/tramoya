"""ANSI-coloured console output for a staged run."""

from __future__ import annotations

import os

WIDTH = 72

_BOLD = "\033[1m"
_DIM = "\033[2m"
_GREEN = "\033[32m"
_YELLOW = "\033[33m"
_CYAN = "\033[36m"
_RED = "\033[31m"
_MAGENTA = "\033[35m"
_RESET = "\033[0m"

use_color = os.environ.get("NO_COLOR", "") == ""


def _c(code: str, text: str) -> str:
    if not use_color:
        return text
    return f"{code}{text}{_RESET}"


def banner(text: str) -> None:
    line = "=" * WIDTH
    print(f"\n{_c(_BOLD + _CYAN, line)}")
    for row in text.splitlines():
        print(_c(_BOLD + _CYAN, f"  {row}"))
    print(f"{_c(_BOLD + _CYAN, line)}\n")


def step(number: int | str, text: str) -> None:
    print(f"{_c(_BOLD + _YELLOW, f'[{number}]')} {_c(_BOLD, text)}")


def note(text: str) -> None:
    print(f"    {_c(_DIM, text)}")


def ok(text: str) -> None:
    print(f"    {_c(_GREEN, 'OK')}    {text}")


def warn(text: str) -> None:
    print(f"    {_c(_YELLOW, 'WARN')}  {text}")


def fail(text: str) -> None:
    print(f"    {_c(_RED, 'ERROR')} {text}")


def sim(text: str) -> None:
    print(f"    {_c(_MAGENTA, '[simulated operator]')} {text}")
