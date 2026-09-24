from __future__ import annotations

from tramoya.pacing import Pacing


def _recording_sleep(calls: list[float]):
    def sleep(seconds: float) -> None:
        calls.append(seconds)

    return sleep


def test_sleep_scales_by_pace() -> None:
    calls: list[float] = []
    pacing = Pacing(pace=2.0, sleep=_recording_sleep(calls))
    slept = pacing.sleep(3.0)
    assert slept == 6.0
    assert calls == [6.0]


def test_sleep_accumulates_unscaled_total_base() -> None:
    calls: list[float] = []
    pacing = Pacing(pace=2.0, sleep=_recording_sleep(calls))
    pacing.sleep(3.0)
    pacing.sleep(1.5)
    assert pacing.total_base == 4.5


def test_sleep_rehearsal_compresses_wait() -> None:
    calls: list[float] = []
    pacing = Pacing(pace=1.0, rehearse=True, sleep=_recording_sleep(calls))
    slept = pacing.sleep(10.0)
    assert slept == 0.05
    assert calls == [0.05]


def test_sleep_rehearsal_caps_base_at_one_second() -> None:
    calls: list[float] = []
    pacing = Pacing(pace=1.0, rehearse=True, sleep=_recording_sleep(calls))
    slept = pacing.sleep(0.4)
    assert slept == 0.4 * 0.05
    assert calls == [0.4 * 0.05]


def test_sleep_rehearsal_still_accumulates_real_base() -> None:
    pacing = Pacing(pace=1.0, rehearse=True, sleep=lambda s: None)
    pacing.sleep(10.0)
    assert pacing.total_base == 10.0


def test_op_timeout_default_minimum() -> None:
    pacing = Pacing(pace=1.0)
    assert pacing.op_timeout() == 20.0


def test_op_timeout_scales_with_pace() -> None:
    pacing = Pacing(pace=4.0)
    assert pacing.op_timeout() == 48.0


def test_op_timeout_in_rehearsal_is_short_but_never_below_minimum() -> None:
    assert Pacing(pace=4.0, rehearse=True).op_timeout() == 20.0
    assert Pacing(pace=4.0, rehearse=True).op_timeout(minimum=2.0) == 5.0


def test_op_timeout_respects_custom_minimum() -> None:
    pacing = Pacing(pace=1.0)
    assert pacing.op_timeout(minimum=50.0) == 50.0
