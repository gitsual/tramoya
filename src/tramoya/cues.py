"""Cue files: timestamped narration lines written live during a take, and
their conversion into scene marks and burned-in ASS subtitles.

A cue file is one `timestamp|text` line per shot, in the order they were
said. The final line is a sentinel: its text is empty and its timestamp is
the take's total elapsed time, so a reader never has to guess where the take
ended from the last spoken line alone.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

from tramoya.marks import Mark

_TRAILING_GAP_SECONDS = 5.0
_FADE_MS = 300
_END_TRIM_SECONDS = 0.15


@dataclass(frozen=True)
class Cue:
    t: float
    text: str


def parse_cues_with_total(text: str) -> tuple[list[Cue], float]:
    """Parse a cue file's text into its cues and the sentinel total.

    The sentinel line (empty text) is not returned as a cue; its timestamp
    becomes `total`. If no sentinel is present, `total` is 0.0.
    """
    cues: list[Cue] = []
    total = 0.0
    for line in text.splitlines():
        if not line:
            continue
        at, _, body = line.partition("|")
        t = float(at)
        if body:
            cues.append(Cue(t=t, text=body))
        else:
            total = t
    return cues, total


def parse_cues(text: str) -> list[Cue]:
    """Parse a cue file's text into its cues, ignoring the sentinel line."""
    cues, _total = parse_cues_with_total(text)
    return cues


def parse_meta(text: str) -> dict[str, str]:
    """Parse a `key=value` per line meta file into a dict."""
    return dict(re.findall(r"^(\w+)=(.*)$", text, re.MULTILINE))


def offset_from_duration(duration: float, elapsed: float) -> float:
    """How much pre-roll happened before the cue clock started.

    The cue clock starts after the recorder is already writing, so every cue
    is late by exactly that pre-roll -- which the finished file's own
    duration gives away, and which is added back to every cue's timestamp.
    """
    return max(0.0, duration - elapsed)


def shift(cues: list[Cue], offset: float) -> list[Cue]:
    """Return a new list with `offset` added to every cue's timestamp."""
    return [Cue(t=c.t + offset, text=c.text) for c in cues]


def cues_to_marks(cues: list[Cue]) -> list[Mark]:
    """Turn a cue sequence into scene marks: cue `i` starts scene `i`, and
    the next cue's timestamp closes it. The last cue only opens its scene --
    there is nothing after it to mark where it ends."""
    marks: list[Mark] = []
    for index, cue in enumerate(cues):
        marks.append(Mark(t=cue.t, key=f"scene:{index}:start", label=cue.text))
        if index + 1 < len(cues):
            marks.append(Mark(t=cues[index + 1].t, key=f"scene:{index}:end", label=cue.text))
    return marks


def _ass_stamp(seconds: float) -> str:
    seconds = max(0.0, seconds)
    hours, seconds = divmod(seconds, 3600)
    minutes, seconds = divmod(seconds, 60)
    return f"{int(hours)}:{int(minutes):02d}:{seconds:05.2f}"


def _ass_colour(hex_rgb: str, alpha: str = "00") -> str:
    """ASS takes &HAABBGGRR -- alpha first, and the channels reversed."""
    rr, gg, bb = (hex_rgb[i : i + 2] for i in (0, 2, 4))
    return f"&H{alpha}{bb}{gg}{rr}".upper()


def cues_to_ass(
    cues: list[Cue],
    total: float,
    width: int,
    height: int,
    font: str = "JetBrainsMono Nerd Font",
    fg: str = "D9E2DD",
    bg: str = "0E1513",
) -> str:
    """Render a cue sequence as an ASS subtitle document.

    Each cue is a plated dialogue line (BorderStyle=3 fills a box in the
    outline colour, so the outline becomes padding rather than a stroke) that
    fades in and out and ends `_END_TRIM_SECONDS` before the next one starts,
    so two adjacent lines never overlap on screen.
    """
    size = max(20, height // 30)
    margin = height // 16
    primary = _ass_colour(fg)
    outline = _ass_colour(bg)

    header = (
        "[Script Info]\n"
        "ScriptType: v4.00+\n"
        f"PlayResX: {width}\n"
        f"PlayResY: {height}\n"
        "WrapStyle: 0\n"
        "ScaledBorderAndShadow: yes\n"
        "\n"
        "[V4+ Styles]\n"
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, "
        "OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, "
        "ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, "
        "MarginL, MarginR, MarginV, Encoding\n"
        f"Style: Narration,{font},{size},{primary},{primary},{outline},{outline},"
        f"0,0,0,0,100,100,0,0,3,6,0,2,{margin},{margin},{margin},1\n"
        "\n"
        "[Events]\n"
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, "
        "Effect, Text\n"
    )

    lines: list[str] = []
    for index, cue in enumerate(cues):
        if not cue.text:
            continue
        if index + 1 < len(cues):
            end = cues[index + 1].t
        else:
            end = max(total, cue.t + _TRAILING_GAP_SECONDS)
        lines.append(
            f"Dialogue: 0,{_ass_stamp(cue.t)},{_ass_stamp(end - _END_TRIM_SECONDS)},"
            f"Narration,,0,0,0,,{{\\fad({_FADE_MS},{_FADE_MS})}}{cue.text}"
        )

    return header + "\n".join(lines) + ("\n" if lines else "")
