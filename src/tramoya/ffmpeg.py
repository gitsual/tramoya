"""ffmpeg/ffprobe command construction and execution.

`Runner` is the only thing in this module that touches a subprocess. Every
other function here is a pure string/argv builder, so the filter graphs
themselves can be tested without ever shelling out. In `dry_run` mode
`Runner` records every argv it would have executed instead of running it,
and answers duration probes from a test-filled `fake_durations` dict --
that is what lets `assembly.py` be exercised end to end without real media.
"""

from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

LOUDNORM = "loudnorm=I=-16:TP=-1.5:LRA=11"

_DRAWTEXT_FONTCOLOR = "white"
_DRAWTEXT_FONTSIZE = 42


class Runner:
    """Executes ffmpeg/ffprobe commands, or records them when dry_run=True."""

    def __init__(
        self, dry_run: bool = False, ffmpeg: str = "ffmpeg", ffprobe: str = "ffprobe"
    ) -> None:
        self.dry_run = dry_run
        self.ffmpeg = ffmpeg
        self.ffprobe = ffprobe
        self.calls: list[list[str]] = []
        self.fake_durations: dict[Path, float] = {}

    def run(self, argv: list[str]) -> None:
        """Run `argv`, or append it to `.calls` in dry_run mode."""
        self.calls.append(argv)
        if not self.dry_run:
            subprocess.run(argv, check=True)

    def probe_duration(self, path: Path) -> float:
        """Return the duration of `path` in seconds.

        In dry_run mode this never shells out to ffprobe -- it reads
        `.fake_durations`, a dict the test fills, defaulting to 0.0.
        """
        if self.dry_run:
            return self.fake_durations.get(path, 0.0)
        argv = [
            self.ffprobe, "-v", "error", "-show_entries", "format=duration",
            "-of", "csv=p=0", str(path),
        ]
        out = subprocess.check_output(argv)
        return float(out.decode().strip())

    def available(self) -> bool:
        """Whether the configured ffmpeg binary can be found on PATH."""
        return shutil.which(self.ffmpeg) is not None


def pad_to_grid(width: int, height: int, color: str = "0x14181F") -> str:
    """Scale-then-letterbox a video filter fitting any source into a fixed grid."""
    return (
        f"scale={width}:{height}:force_original_aspect_ratio=decrease,"
        f"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color={color},format=yuv420p"
    )


def _escape_drawtext(label: str) -> str:
    return label.replace("'", "’").replace(":", "\\:")  # noqa: RUF001


def wait_compress(factor: float, label: str) -> str:
    """A setpts+drawtext filter that speeds up a wait and captions the label."""
    escaped = _escape_drawtext(label)
    return (
        f"setpts=PTS/{factor},"
        f"drawtext=text='{escaped}':fontcolor={_DRAWTEXT_FONTCOLOR}:"
        f"fontsize={_DRAWTEXT_FONTSIZE}:x=(w-text_w)/2:y=h-th-40:"
        "box=1:boxcolor=black@0.5:boxborderw=12"
    )


def freeze_tail(seconds: float) -> str:
    """A tpad filter that clones the last frame to pad the clip's tail."""
    return f"tpad=stop_mode=clone:stop_duration={seconds:.3f}"


def silent_audio(layout: str = "mono", rate: int = 48000) -> list[str]:
    """An anullsrc lavfi input argv fragment producing silence."""
    return ["-f", "lavfi", "-i", f"anullsrc=channel_layout={layout}:sample_rate={rate}"]


def concat_argv(list_path: Path, out: Path) -> list[str]:
    """A concat-demuxer command stitching the paths in `list_path` into `out`."""
    return [
        "ffmpeg", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0",
        "-i", str(list_path), "-c", "copy", str(out),
    ]


def write_concat_list(paths: list[Path], list_path: Path) -> None:
    """Write a concat-demuxer list file, one quoted `file` line per path."""
    lines = "".join(f"file '{path}'\n" for path in paths)
    list_path.write_text(lines, encoding="utf-8")
