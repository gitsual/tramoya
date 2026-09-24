"""Subtitle cue derivation and rendering (SRT and ASS), plus the ffmpeg argv
to burn them in or to trim a video before burning.

Cue timing is derived from a timeline of segments (duck-typed: anything with
`.kind`, `.name` and `.duration`, e.g. `assembly.Segment`) rather than from
forced alignment -- narration is split by sentence and apportioned across its
segment's window in proportion to character count, which is the cheapest
usable proxy for speaking time.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

from tramoya.ffmpeg import concat_argv, write_concat_list

MAX_CHARS = 84  # two lines of 42 -- the usual ceiling for burned-in subtitles
LINE_CHARS = 42
MIN_CHUNK = 28  # a fragment this short is joined to the next rather than shown alone

_TAIL_TRIM = 0.15
_FADE = "{\\fad(300,300)}"


class _Segment(Protocol):
    kind: str
    name: str
    duration: float


@dataclass(frozen=True)
class Cue:
    start: float
    end: float
    text: str


def split_sentences(text: str) -> list[str]:
    """Narration into sentences, keeping their terminal punctuation."""
    parts = re.split(r"(?<=[.:?!])\s+", text.strip())
    return [p.strip() for p in parts if p.strip()]


def wrap(text: str, width: int = LINE_CHARS) -> str:
    """Two lines at most; the break goes at the word nearest the middle."""
    if len(text) <= width:
        return text
    words = text.split()
    best, best_worst = 1, None
    for i in range(1, len(words)):
        worst = max(len(" ".join(words[:i])), len(" ".join(words[i:])))
        if best_worst is None or worst < best_worst:
            best, best_worst = i, worst
    return " ".join(words[:best]) + "\n" + " ".join(words[best:])


def fits(text: str) -> bool:
    """Whether `wrap` lays this out inside the burned-in width."""
    return all(len(line) <= LINE_CHARS for line in wrap(text).split("\n"))


def split_long(sentence: str, limit: int = MAX_CHARS) -> list[str]:
    """A sentence too long for one cue, cut at word boundaries into even pieces."""
    if len(sentence) <= limit and fits(sentence):
        return [sentence]
    parts = -(-len(sentence) // limit)
    target = len(sentence) / parts
    words = sentence.split()
    pieces: list[str] = []
    current = ""
    for word in words:
        candidate = f"{current} {word}".strip()
        room = len(pieces) < parts - 1
        if current and room and (len(candidate) > limit or len(current) >= target):
            pieces.append(current)
            current = word
        else:
            current = candidate
    if current:
        pieces.append(current)
    out: list[str] = []
    for piece in pieces:
        if limit <= LINE_CHARS or fits(piece):
            out.append(piece)
        else:
            out.extend(split_long(piece, limit - 4))
    return out


def page_cues(text: str, start: float, end: float) -> list[Cue]:
    """Split a page's narration and share its window out by character count."""
    pieces: list[str] = []
    for sentence in split_sentences(text):
        pieces.extend(split_long(sentence))
    if not pieces:
        return []
    chunks: list[str] = []
    for piece in pieces:
        if chunks and len(chunks[-1]) < MIN_CHUNK and fits(f"{chunks[-1]} {piece}"):
            chunks[-1] = f"{chunks[-1]} {piece}"
        else:
            chunks.append(piece)
    if len(chunks) > 1 and len(chunks[-1]) < MIN_CHUNK:
        tail = chunks.pop()
        if fits(f"{chunks[-1]} {tail}"):
            chunks[-1] = f"{chunks[-1]} {tail}"
        else:
            chunks.append(tail)
    total = sum(len(c) for c in chunks)
    cues: list[Cue] = []
    at = start
    span = end - start
    for i, chunk in enumerate(chunks):
        width = span * len(chunk) / total
        stop = end if i == len(chunks) - 1 else at + width
        cues.append(Cue(at, stop, wrap(chunk)))
        at = stop
    return cues


def build_cues(
    script_lines: list[tuple[str, str]],
    segments: list[_Segment],
    clip_cue: str | None,
    scale: float = 1.0,
) -> list[Cue]:
    """Timeline in, subtitle track out.

    `clip_cue`, when given, announces the recorded clip segment with a single
    short cue instead of narration (the clip carries its own voice). `scale`
    anchors the track to the video's measured runtime.
    """
    said = dict(script_lines)
    cues: list[Cue] = []
    at = 0.0
    for seg in segments:
        start, end = at * scale, (at + seg.duration) * scale
        at += seg.duration
        if seg.kind == "clip":
            if clip_cue:
                cues.append(Cue(start, min(start + 4.0, end), wrap(clip_cue)))
            continue
        text = said.get(seg.name)
        if text:
            cues.extend(page_cues(text, start, end))
    return cues


def timestamp(seconds: float) -> str:
    """SRT-shaped timestamp: `HH:MM:SS,mmm`."""
    ms = round(seconds * 1000)
    h, ms = divmod(ms, 3_600_000)
    m, ms = divmod(ms, 60_000)
    s, ms = divmod(ms, 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def render_srt(cues: list[Cue]) -> str:
    """Render an SRT subtitle track."""
    blocks = [
        f"{i}\n{timestamp(c.start)} --> {timestamp(c.end)}\n{c.text}\n"
        for i, c in enumerate(cues, start=1)
    ]
    return "\n".join(blocks)


def _ass_stamp(seconds: float) -> str:
    hours = int(seconds // 3600)
    minutes = int((seconds % 3600) // 60)
    secs = seconds % 60
    return f"{hours}:{minutes:02d}:{secs:05.2f}"


def _ass_colour(hex_rgb: str, alpha: str = "00") -> str:
    red, green, blue = hex_rgb[0:2], hex_rgb[2:4], hex_rgb[4:6]
    return f"&H{alpha}{blue}{green}{red}".upper()


def render_ass(
    cues: list[Cue],
    width: int,
    height: int,
    font: str = "JetBrainsMono Nerd Font",
    fg: str = "D9E2DD",
    bg: str = "0E1513",
) -> str:
    """Render an ASS subtitle track with the vivac "plate" caption style."""
    size = max(20, round(height / 30))
    margin = round(height / 16)
    fg_colour = _ass_colour(fg)
    bg_colour = _ass_colour(bg)
    header = (
        "[Script Info]\n"
        "ScriptType: v4.00+\n"
        f"PlayResX: {width}\n"
        f"PlayResY: {height}\n"
        "\n"
        "[V4+ Styles]\n"
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, "
        "OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, "
        "ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, "
        "Alignment, MarginL, MarginR, MarginV, Encoding\n"
        f"Style: Narration,{font},{size},{fg_colour},{fg_colour},{bg_colour},"
        f"{bg_colour},0,0,0,0,100,100,0,0,3,6,0,2,{margin},{margin},{margin},1\n"
        "\n"
        "[Events]\n"
        "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, "
        "Effect, Text\n"
    )
    lines = []
    for cue in cues:
        text = cue.text.replace("\n", "\\N")
        start = _ass_stamp(cue.start)
        end = _ass_stamp(max(cue.start, cue.end - _TAIL_TRIM))
        lines.append(f"Dialogue: 0,{start},{end},Narration,,0,0,0,,{_FADE}{text}")
    return header + "\n".join(lines) + "\n"


def burn_argv(video: Path, subtitles_path: Path, out: Path) -> list[str]:
    """A command burning `subtitles_path` into `video`, copying the audio."""
    return [
        "ffmpeg", "-y", "-i", str(video),
        "-vf", f"subtitles={subtitles_path}",
        "-c:a", "copy", str(out),
    ]


def _fmt_seconds(value: float) -> str:
    return str(int(value)) if float(value).is_integer() else str(value)


def trim_plan(
    ranges: list[tuple[float, float]], video: Path, work_dir: Path, out: Path
) -> list[list[str]]:
    """Cut every `(start, end)` range from `video`, then concat them into `out`.

    Writes the concat demuxer's list file into `work_dir` as a side effect
    (it must be a real path for the returned concat argv to reference).
    """
    argvs: list[list[str]] = []
    parts: list[Path] = []
    for i, (start, end) in enumerate(ranges):
        part = work_dir / f"trim-{i}.mp4"
        argvs.append([
            "ffmpeg", "-loglevel", "error", "-y",
            "-ss", _fmt_seconds(start), "-to", _fmt_seconds(end),
            "-i", str(video), "-c", "copy", str(part),
        ])
        parts.append(part)
    list_path = work_dir / "trim-concat.txt"
    write_concat_list(parts, list_path)
    argvs.append(concat_argv(list_path, out))
    return argvs
