"""Tests for tramoya.record: wf-recorder invocation, offset bookkeeping and
the private null-sink used to capture narration."""

from __future__ import annotations

import json
import signal
import time

from tramoya.record import (
    NullSink,
    Recorder,
    director_start_from_proc,
    offset_sidecar,
    read_sidecar,
    wf_recorder_argv,
    write_sidecar,
)


def test_wf_recorder_argv_without_audio():
    argv = wf_recorder_argv("/tmp/out.mp4", "0,48 1920x1032", fps=10)
    assert argv == ["wf-recorder", "-r", "10", "-g", "0,48 1920x1032", "-f", "/tmp/out.mp4"]


def test_wf_recorder_argv_with_audio_adds_sink_and_codec():
    argv = wf_recorder_argv(
        "/tmp/out.mp4", "0,48 1920x1032", fps=10, audio_sink="demo-narracion", codec="aac"
    )
    assert argv == [
        "wf-recorder",
        "-r",
        "10",
        "-g",
        "0,48 1920x1032",
        "--audio=demo-narracion.monitor",
        "-C",
        "aac",
        "-f",
        "/tmp/out.mp4",
    ]


def test_director_start_from_proc_reads_boot_epoch_plus_starttime(tmp_path):
    uptime_path = tmp_path / "uptime"
    uptime_path.write_text("1000.00 900.00\n")
    stat_path = tmp_path / "stat"
    # comm can contain spaces/parens, hence the rsplit(")") parsing this must survive.
    stat_path.write_text(
        "4242 (python demo) S 1 4242 4242 0 -1 4194560 100 0 0 0 "
        "5 2 0 0 20 0 4 0 500 0 0 0 0 0 0 0 0 0 0 0 0 0 17 1 0 0 0 0 0\n"
    )
    # field 22 (starttime) above is "500"
    before = time.time()
    result = director_start_from_proc(
        4242, clk_tck=100, uptime_path=str(uptime_path), stat_path=str(stat_path)
    )
    after = time.time()
    expected = result - 500 / 100  # boot_epoch as the function computed it
    # boot_epoch = time.time() - uptime_seconds, taken somewhere in [before, after]
    assert before - 1000.0 <= expected <= after - 1000.0


def test_offset_sidecar_shape():
    sidecar = offset_sidecar(rec_start=10.0, director_start=12.5)
    assert sidecar == {"rec_start": 10.0, "director_start": 12.5, "offset": 2.5}


def test_write_and_read_sidecar_round_trip(tmp_path):
    path = tmp_path / "out.mp4.offset.json"
    sidecar = offset_sidecar(rec_start=10.0, director_start=8.0)
    write_sidecar(path, sidecar)
    assert json.loads(path.read_text()) == sidecar
    assert read_sidecar(path) == -2.0


def test_null_sink_loads_and_unloads_via_injected_runner():
    calls: list[list[str]] = []

    def runner(argv: list[str]) -> str:
        calls.append(argv)
        if argv[:2] == ["pactl", "load-module"]:
            return "17\n"
        return ""

    with NullSink("demo-narracion", runner=runner) as sink:
        assert sink.name == "demo-narracion"
        assert sink.monitor == "demo-narracion.monitor"
        assert calls[0][:3] == ["pactl", "load-module", "module-null-sink"]
        assert "sink_name=demo-narracion" in calls[0]

    assert calls[1] == ["pactl", "unload-module", "17"]


def test_null_sink_skips_unload_when_load_never_ran():
    calls: list[list[str]] = []

    def runner(argv: list[str]) -> str:
        calls.append(argv)
        return "9"

    sink = NullSink("demo-narracion", runner=runner)
    # never entered: __exit__ must not be called manually here, this only
    # checks that construction alone issues no calls.
    assert calls == []
    del sink


class _FakeProc:
    def __init__(self) -> None:
        self.signals: list[int] = []
        self.waited: list[float | None] = []

    def send_signal(self, sig: int) -> None:
        self.signals.append(sig)

    def wait(self, timeout: float | None = None) -> None:
        self.waited.append(timeout)


def test_recorder_start_records_rec_start_and_launches_via_runner():
    launched: dict = {}

    def runner(argv: list[str]) -> _FakeProc:
        launched["argv"] = argv
        return _FakeProc()

    clock_values = iter([100.0])
    rec = Recorder(
        "/tmp/out.mp4", "0,48 1920x1032", fps=10, runner=runner, clock=lambda: next(clock_values)
    )
    rec.start()
    assert rec.rec_start == 100.0
    assert launched["argv"][0] == "wf-recorder"
    assert isinstance(rec.proc, _FakeProc)


def test_recorder_stop_sends_sigint_and_waits():
    fake = _FakeProc()

    def runner(argv: list[str]) -> _FakeProc:
        return fake

    rec = Recorder("/tmp/out.mp4", "0,48 1920x1032", runner=runner)
    rec.start()
    rec.stop()
    assert fake.signals == [signal.SIGINT]
    assert len(fake.waited) == 1


def test_recorder_as_context_manager_starts_and_stops():
    fake = _FakeProc()

    def runner(argv: list[str]) -> _FakeProc:
        return fake

    with Recorder("/tmp/out.mp4", "0,48 1920x1032", runner=runner) as rec:
        assert rec.proc is fake
    assert fake.signals == [signal.SIGINT]


def test_recorder_stop_without_start_is_a_no_op():
    rec = Recorder("/tmp/out.mp4", "0,48 1920x1032", runner=lambda argv: _FakeProc())
    rec.stop()  # must not raise
