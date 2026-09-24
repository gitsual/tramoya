"""Scene and deck assembly: turn marks or a deck plan into rendered clips.

Two pipelines live here, both driven through a `tramoya.ffmpeg.Runner` so
they can be exercised without ever shelling out:

- `assemble_marks`: a screen recording plus `scene:*`/`wait:*` marks (see
  `tramoya.marks`) becomes one clip per scene, waits longer than
  `WAIT_THRESHOLD` sped up with a caption, each fitted to its own narration
  clip (`build_scene_clip`, `fit_and_mux`), then concatenated.
- `assemble_deck`: a slide deck read aloud, with a recorded demo clip
  spliced in right after the page that announces it (`plan_segments`,
  `render_slide`, `render_clip`).
"""

from __future__ import annotations

import tempfile
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from tramoya.ffmpeg import (
    LOUDNORM,
    Runner,
    concat_argv,
    freeze_tail,
    pad_to_grid,
    silent_audio,
    wait_compress,
    write_concat_list,
)
from tramoya.marks import load_marks, scene_bounds, wait_windows

WAIT_THRESHOLD = 20.0  # seconds -- below this, a wait window is left alone
WAIT_TARGET = 4.0  # seconds -- a long wait is sped up to land around this length
MIN_SCENE = 0.05  # a scene shorter than this (a fast rehearsal) is clamped to one frame
PAGE_TAIL = 0.6  # seconds of quiet after a narration, so pages don't clip into each other
SILENT_PAGE = 2.2  # a page with no voice clip (a section divider) still needs to be read

_FPS = 30
_GRID = (1920, 1080)
_SIZE = "1920:1080"

VoiceName = Callable[[str], str]


def humanize_wait(seconds: float, lang: str) -> str:
    """A short caption for a sped-up wait, e.g. "4 minutes later"."""
    minutes = round(seconds / 60.0)
    if minutes <= 0:
        return "unos segundos despues" if lang == "es" else "a few seconds later"
    if lang == "es":
        unit = "minuto" if minutes == 1 else "minutos"
        return f"{minutes} {unit} despues"
    unit = "minute" if minutes == 1 else "minutes"
    return f"{minutes} {unit} later"


@dataclass(frozen=True)
class Segment:
    """One piece of a deck's final timeline: a still slide or the clip."""

    kind: str  # "slide" | "clip"
    name: str
    source: Path
    duration: float
    voice: Path | None = None


def plan_segments(
    pages: dict[str, str],
    order: list[str],
    durations: dict[str, float],
    slides_dir: Path,
    clip: Path | None,
    clip_duration: float,
    voices: dict[str, Path] | None = None,
    clip_page: str = "video",
) -> list[Segment]:
    """Deck order in, timeline out.

    The clip does not replace `clip_page`: that page is read first (it is
    what announces the recording), and the clip plays right after it.
    """
    plan: list[Segment] = []
    for name in order:
        page = pages[name]
        spoken = durations.get(name)
        plan.append(Segment(
            kind="slide",
            name=name,
            source=slides_dir / f"{page}.png",
            duration=(spoken + PAGE_TAIL) if spoken is not None else SILENT_PAGE,
            voice=(voices or {}).get(name),
        ))
        if name == clip_page and clip is not None:
            plan.append(Segment(kind="clip", name="demo", source=clip, duration=clip_duration))
    return plan


def build_scene_clip(
    runner: Runner,
    video: Path,
    work_dir: Path,
    name: str,
    start: float,
    end: float,
    waits: list[tuple[float, float]],
    lang: str,
) -> Path:
    """Cut `[start, end)` out of `video`, compressing any wait window inside
    it that is longer than `WAIT_THRESHOLD` (with a caption), and return the
    path to the resulting video-only clip."""
    local_waits = [
        (max(0.0, a - start), min(end - start, b - start))
        for a, b in waits
        if a < end and b > start
    ]
    local_waits = [(a, b) for a, b in local_waits if b - a > WAIT_THRESHOLD]

    if end - start < MIN_SCENE:
        end = start + MIN_SCENE

    scene_dur = end - start
    out_path = work_dir / f"scene-{name}-video.mp4"

    if not local_waits:
        runner.run([
            "ffmpeg", "-loglevel", "error", "-y",
            "-ss", str(start), "-to", str(end), "-i", str(video),
            "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "20",
            str(out_path),
        ])
        return out_path

    pieces: list[Path] = []
    cursor = 0.0
    for i, (a, b) in enumerate(local_waits):
        if a > cursor:
            pre = work_dir / f"scene-{name}-pre{i}.mp4"
            runner.run([
                "ffmpeg", "-loglevel", "error", "-y",
                "-ss", str(start + cursor), "-to", str(start + a), "-i", str(video),
                "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "20", str(pre),
            ])
            pieces.append(pre)
        wait_dur = b - a
        factor = max(1.0, wait_dur / WAIT_TARGET)
        label = humanize_wait(wait_dur, lang)
        fast = work_dir / f"scene-{name}-wait{i}.mp4"
        runner.run([
            "ffmpeg", "-loglevel", "error", "-y",
            "-ss", str(start + a), "-to", str(start + b), "-i", str(video),
            "-an", "-vf", wait_compress(factor, label),
            "-c:v", "libx264", "-preset", "fast", "-crf", "20", str(fast),
        ])
        pieces.append(fast)
        cursor = b
    if cursor < scene_dur:
        post = work_dir / f"scene-{name}-post.mp4"
        runner.run([
            "ffmpeg", "-loglevel", "error", "-y",
            "-ss", str(start + cursor), "-to", str(end), "-i", str(video),
            "-an", "-c:v", "libx264", "-preset", "fast", "-crf", "20", str(post),
        ])
        pieces.append(post)

    if len(pieces) == 1:
        return pieces[0]

    list_path = work_dir / f"scene-{name}-concat.txt"
    write_concat_list(pieces, list_path)
    runner.run(concat_argv(list_path, out_path))
    return out_path


def fit_and_mux(
    runner: Runner, work_dir: Path, name: str, video_clip: Path, wav_path: Path | None
) -> tuple[Path, float]:
    """Pad the video (freeze last frame) and/or the audio (silence) so both
    reach the same final duration, then mux them into one clip."""
    video_dur = runner.probe_duration(video_clip)
    wav_dur = runner.probe_duration(wav_path) if wav_path is not None else 0.0
    final_dur = max(video_dur, wav_dur)
    out_path = work_dir / f"scene-{name}-final.mp4"

    if final_dur > video_dur + 0.02:
        padded = work_dir / f"scene-{name}-video-padded.mp4"
        runner.run([
            "ffmpeg", "-loglevel", "error", "-y", "-i", str(video_clip),
            "-vf", freeze_tail(final_dur - video_dur),
            "-c:v", "libx264", "-preset", "fast", "-crf", "20", str(padded),
        ])
        video_source = padded
    else:
        video_source = video_clip

    if wav_path is not None:
        cmd = [
            "ffmpeg", "-loglevel", "error", "-y",
            "-i", str(video_source), "-i", str(wav_path),
            "-filter_complex",
            f"[1:a]aformat=sample_rates=48000:channel_layouts=mono,{LOUDNORM},"
            f"apad=whole_dur={final_dur:.3f}[aout]",
            "-map", "0:v", "-map", "[aout]",
            "-t", f"{final_dur:.3f}",
            "-c:v", "libx264", "-preset", "fast", "-crf", "20",
            "-c:a", "aac", "-b:a", "128k",
            str(out_path),
        ]
    else:
        cmd = [
            "ffmpeg", "-loglevel", "error", "-y",
            "-i", str(video_source),
            *silent_audio(layout="mono", rate=48000),
            "-map", "0:v", "-map", "1:a",
            "-t", f"{final_dur:.3f}",
            "-c:v", "libx264", "-preset", "fast", "-crf", "20",
            "-c:a", "aac", "-b:a", "128k",
            str(out_path),
        ]
    runner.run(cmd)
    return out_path, final_dur


def _default_voice_name(name: str) -> str:
    return f"scene-{name}.wav"


def assemble_marks(
    runner: Runner,
    video: Path,
    marks_path: Path,
    voice_dir: Path,
    out: Path,
    offset: float = 0.0,
    lang: str = "en",
    voice_name: VoiceName = _default_voice_name,
) -> list[tuple[str, float, float]]:
    """Marks + a screen recording in, one scene per clip, concatenated to `out`.

    A missing voice clip is tolerated: that scene keeps its own video length
    and gets a silent audio track instead of failing the whole run. Both the
    canonical `voice_name(name)` filename and the legacy `escena-<name>.wav`
    one are tried before giving up.
    """
    marks = load_marks(marks_path, offset)
    scenes = scene_bounds(marks)
    if not scenes:
        raise RuntimeError(f"no scene marks found in {marks_path}")
    waits = wait_windows(marks)

    table: list[tuple[str, float, float]] = []
    with tempfile.TemporaryDirectory(prefix="tramoya-assemble-") as tmp:
        work_dir = Path(tmp)
        final_clips: list[Path] = []
        for name, (start, end) in scenes.items():
            raw_dur = end - start
            video_clip = build_scene_clip(runner, video, work_dir, name, start, end, waits, lang)

            wav_path = voice_dir / voice_name(name)
            if not wav_path.exists():
                legacy = voice_dir / f"escena-{name}.wav"
                if legacy.exists():
                    wav_path = legacy

            final_clip, final_dur = fit_and_mux(
                runner, work_dir, name, video_clip, wav_path if wav_path.exists() else None
            )
            final_clips.append(final_clip)
            table.append((name, raw_dur, final_dur))

        list_path = work_dir / "final-concat.txt"
        write_concat_list(final_clips, list_path)
        out.parent.mkdir(parents=True, exist_ok=True)
        runner.run(concat_argv(list_path, out))

    return table


def render_slide(runner: Runner, seg: Segment, out: Path) -> None:
    """A still page with its own narration (or silence) already muxed in, so
    the concat step only ever joins uniform pieces."""
    cmd = [
        "ffmpeg", "-y", "-loglevel", "error",
        "-loop", "1", "-framerate", str(_FPS), "-t", f"{seg.duration:.3f}", "-i", str(seg.source),
    ]
    if seg.voice is not None:
        cmd += [
            "-i", str(seg.voice),
            "-filter_complex", f"[1:a]{LOUDNORM},apad[a]",
            "-map", "0:v", "-map", "[a]",
        ]
    else:
        cmd += [*silent_audio(layout="stereo", rate=48000), "-map", "0:v", "-map", "1:a"]
    cmd += [
        "-t", f"{seg.duration:.3f}",
        "-vf", pad_to_grid(*_GRID),
        "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-r", str(_FPS),
        "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
        str(out),
    ]
    runner.run(cmd)


def render_clip(runner: Runner, seg: Segment, out: Path) -> None:
    """The demo clip is re-encoded to the same grid as the slides -- without
    this the concat demuxer stitches mismatched streams and the audio
    drifts."""
    runner.run([
        "ffmpeg", "-y", "-loglevel", "error", "-i", str(seg.source),
        "-vf", pad_to_grid(*_GRID),
        "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-r", str(_FPS),
        "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
        str(out),
    ])


def assemble_deck(
    runner: Runner, plan: list[Segment], work_dir: Path, out: Path
) -> list[Path]:
    """Render every segment of `plan` and concatenate them into `out`."""
    parts: list[Path] = []
    for i, seg in enumerate(plan):
        part = work_dir / f"{i:03d}.mp4"
        if seg.kind == "clip":
            render_clip(runner, seg, part)
        else:
            render_slide(runner, seg, part)
        parts.append(part)

    list_path = work_dir / "concat.txt"
    write_concat_list(parts, list_path)
    out.parent.mkdir(parents=True, exist_ok=True)
    runner.run(concat_argv(list_path, out))
    return parts
