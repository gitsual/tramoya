"""Screen recording: wf-recorder invocation, the wall-clock offset between
the recorder and the staged run it captures, and a private PulseAudio null
sink to route narration into the recording without the operator hearing it.
"""

from __future__ import annotations

import json
import signal
import subprocess
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any

_DEFAULT_TIMEOUT_SECONDS = 10.0


def wf_recorder_argv(
    out: str | Path,
    geometry: str,
    fps: int = 10,
    audio_sink: str | None = None,
    codec: str = "aac",
) -> list[str]:
    """Build wf-recorder's argv.

    Audio is opt-in: with no sink, the recording is mute and no codec flag is
    passed either, matching the mute takes that never touch narration.
    """
    argv = ["wf-recorder", "-r", str(fps), "-g", geometry]
    if audio_sink:
        argv += [f"--audio={audio_sink}.monitor", "-C", codec]
    argv += ["-f", str(out)]
    return argv


def _default_runner(argv: list[str]) -> str:
    return subprocess.run(argv, capture_output=True, text=True, check=True).stdout.strip()


def director_start_from_proc(
    pid: int,
    clk_tck: int | None = None,
    uptime_path: str = "/proc/uptime",
    stat_path: str | None = None,
) -> float:
    """The process's own wall-clock start time: boot epoch + starttime ticks.

    Read from the kernel rather than guessed from when the launcher spawned
    it -- every layer between a supervisor starting and the process itself
    running adds slack that would otherwise bias the offset by a second or
    more.
    """
    if clk_tck is None:
        import os

        clk_tck = int(os.sysconf("SC_CLK_TCK"))
    resolved_stat_path = stat_path or f"/proc/{pid}/stat"

    uptime_seconds = float(Path(uptime_path).read_text().split()[0])
    boot_epoch = time.time() - uptime_seconds

    stat_text = Path(resolved_stat_path).read_text()
    # comm (field 2) is parenthesised and may itself contain spaces or
    # parens, so the only safe split is on the LAST ")" in the line -- what
    # follows is always state (field 3) onward, in fixed order.
    after_comm = stat_text.rsplit(")", 1)[1]
    fields = after_comm.split()
    starttime_ticks = float(fields[19])  # field 22 overall: state is field 3
    return boot_epoch + starttime_ticks / clk_tck


def offset_sidecar(rec_start: float, director_start: float) -> dict[str, float]:
    """The sidecar recorded alongside a take: how far the director's own
    clock trails or leads the recorder's."""
    return {
        "rec_start": rec_start,
        "director_start": director_start,
        "offset": director_start - rec_start,
    }


def write_sidecar(path: str | Path, sidecar: dict[str, float]) -> None:
    Path(path).write_text(json.dumps(sidecar, indent=2) + "\n", encoding="utf-8")


def read_sidecar(path: str | Path) -> float:
    """Read a sidecar's offset back."""
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    return float(data["offset"])


class NullSink:
    """A private PulseAudio null sink, loaded and unloaded through an
    injected runner so narration can be routed into a recording without the
    operator's own speakers making a sound.

    `runner` takes an argv list and returns pactl's stdout as text; the
    default shells out to `pactl` itself.
    """

    def __init__(self, name: str, runner: Callable[[list[str]], str] = _default_runner) -> None:
        self.name = name
        self.monitor = f"{name}.monitor"
        self._runner = runner
        self._module_id: str | None = None

    def __enter__(self) -> NullSink:
        output = self._runner(
            [
                "pactl",
                "load-module",
                "module-null-sink",
                f"sink_name={self.name}",
                f"sink_properties=device.description={self.name}",
            ]
        )
        self._module_id = output.strip()
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> bool:
        if self._module_id:
            self._runner(["pactl", "unload-module", self._module_id])
            self._module_id = None
        return False


class Recorder:
    """Owns one wf-recorder process for the duration of a take.

    `runner` takes wf-recorder's argv and returns a process-like object with
    `send_signal()` and `wait()`; the default is `subprocess.Popen`.
    """

    def __init__(
        self,
        out: str | Path,
        geometry: str,
        fps: int = 10,
        audio_sink: str | None = None,
        runner: Callable[[list[str]], Any] = subprocess.Popen,
        clock: Callable[[], float] = time.time,
    ) -> None:
        self.out = out
        self.geometry = geometry
        self.fps = fps
        self.audio_sink = audio_sink
        self._runner = runner
        self._clock = clock
        self.proc: Any = None
        self.rec_start: float | None = None

    def start(self) -> Recorder:
        argv = wf_recorder_argv(self.out, self.geometry, fps=self.fps, audio_sink=self.audio_sink)
        self.rec_start = self._clock()
        self.proc = self._runner(argv)
        return self

    def stop(self, timeout: float = _DEFAULT_TIMEOUT_SECONDS) -> None:
        if self.proc is None:
            return
        proc, self.proc = self.proc, None
        proc.send_signal(signal.SIGINT)
        proc.wait(timeout=timeout)

    def __enter__(self) -> Recorder:
        self.start()
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> bool:
        self.stop()
        return False
