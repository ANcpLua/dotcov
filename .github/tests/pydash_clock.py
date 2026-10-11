"""Deterministic time for the two sleep-based tests in the pinned pydash suite.

Only module references used by those tests are replaced. The upstream debounce
implementation, its locks, test assertions, and coverage configuration stay intact.
"""

import heapq
import itertools
import threading
from types import SimpleNamespace

import pytest

DEBOUNCE_TESTS = {
    "tests/test_functions.py::test_debounce",
    "tests/test_functions.py::test_debounce_max_wait",
}
CLOCK_PROPERTY = ("dotcov-clock", "deterministic")


class Timer:
    """The start/cancel/daemon surface used by pydash, driven by Clock.sleep."""

    def __init__(self, clock, interval, function, args=(), kwargs=None):
        self.clock = clock
        self.interval = interval
        self.function = function
        self.args = args
        self.kwargs = kwargs or {}
        self.cancelled = False
        self.started = False
        self.daemon = False

    def start(self):
        if self.started:
            raise RuntimeError("threads can only be started once")
        self.started = True
        deadline = self.clock.ticks + max(0, round(self.interval * 1_000_000_000))
        heapq.heappush(self.clock.timers, (deadline, next(self.clock.sequence), self))

    def cancel(self):
        self.cancelled = True


class Clock:
    def __init__(self):
        self.ticks = 0
        self.timers = []
        self.sequence = itertools.count()

    def now(self):
        return self.ticks // 1_000_000  # pydash.now returns integer milliseconds

    def timer(self, interval, function, args=(), kwargs=None):
        return Timer(self, interval, function, args, kwargs)

    def sleep(self, seconds):
        if seconds < 0:
            raise ValueError("sleep length must be non-negative")
        end = self.ticks + round(seconds * 1_000_000_000)
        while self.timers and self.timers[0][0] <= end:
            deadline, _, timer = heapq.heappop(self.timers)
            self.ticks = deadline
            if not timer.cancelled:
                timer.function(*timer.args, **timer.kwargs)
        self.ticks = end


def install_clock(monkeypatch, test_module, clock):
    # Import only during test execution, after pytest-cov starts measuring imports.
    import pydash
    import pydash.functions

    monkeypatch.setattr(test_module, "time", SimpleNamespace(sleep=clock.sleep))
    monkeypatch.setattr(pydash, "now", clock.now)
    monkeypatch.setattr(pydash.functions, "threading", SimpleNamespace(
        Timer=clock.timer, Lock=threading.Lock,
    ))


@pytest.fixture(autouse=True)
def pydash_debounce_clock(request, monkeypatch):
    if request.node.nodeid in DEBOUNCE_TESTS:
        install_clock(monkeypatch, request.module, Clock())
        # The measurement harness requires evidence that both tests used the clock.
        request.node.user_properties.append(CLOCK_PROPERTY)
