"""Pacing control for a staged run: scaled sleeps and rehearsal compression."""

from __future__ import annotations

import time
from collections.abc import Callable

_REHEARSE_FACTOR = 0.05
_REHEARSE_CAP_SECONDS = 1.0


class Pacing:
    """How fast a staged run moves.

    `pace` scales every sleep; `rehearse` compresses waits to a token amount
    so a rehearsal run finishes in seconds without lying about the real
    budget (tracked, unscaled, in `.total_base`). `sleep` is injectable so
    tests never actually block.
    """

    def __init__(
        self,
        pace: float = 1.0,
        rehearse: bool = False,
        lang: str = "en",
        sleep: Callable[[float], None] = time.sleep,
    ) -> None:
        self.pace = pace
        self.rehearse = rehearse
        self.lang = lang
        self._sleep = sleep
        self.total_base = 0.0

    def sleep(self, base_seconds: float) -> float:
        """Sleep for `base_seconds` scaled by pace, or compressed in rehearsal.

        Returns the number of seconds actually slept. Tracks the unscaled
        total in `.total_base` regardless of mode.
        """
        self.total_base += base_seconds
        if self.rehearse:
            actual = min(base_seconds, _REHEARSE_CAP_SECONDS) * _REHEARSE_FACTOR
        else:
            actual = base_seconds * self.pace
        self._sleep(actual)
        return actual

    def op_timeout(self, minimum: float = 20.0) -> float:
        """How long to wait for an operation to reach a target status.

        Scales with ``pace`` because a slowed-down backend needs a
        proportionally patient timeout; a rehearsal only needs a short one.
        """
        scaled = 5.0 if self.rehearse else 12.0 * self.pace
        return max(minimum, scaled)
