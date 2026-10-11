"""Real-thread pydash checks; each scenario owns a bounded, isolated subprocess."""

import json
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

import pydash
import pytest

WAIT = 10  # A liveness bound, not an assertion about millisecond scheduling accuracy.
CASES = ("delivery", "expired_timer", "blocked_callback", "concurrent_callers", "max_wait")


class Threads:
    def __init__(self):
        self.baseline = set(threading.enumerate())
        self.workers = []
        self.releases = []
        self.barriers = []
        self.errors = queue.SimpleQueue()
        self.timer_ids = set()
        self.stop = threading.Event()

    def __enter__(self):
        assert "pydash_clock" not in sys.modules
        assert pydash.functions.threading is threading
        assert pydash.now is pydash.utilities.now
        self.original_hook = threading.excepthook
        self.original_profile = threading.getprofile()

        def failed(args):
            self.errors.put(f"{args.exc_type.__name__}: {args.exc_value}")
            self.original_hook(args)

        threading.excepthook = failed
        return self

    def __exit__(self, *_):
        try:
            self.stop.set()
            for event in self.releases:
                event.set()
            for barrier in self.barriers:
                barrier.abort()
            deadline = time.monotonic() + WAIT
            for worker in self.workers:
                worker.join(max(0, deadline - time.monotonic()))
            # This dedicated process owns every thread created after the baseline.
            remaining = set(threading.enumerate()) - self.baseline
            for thread in remaining:
                if isinstance(thread, threading.Timer):
                    thread.cancel()
            for thread in remaining:
                thread.join(max(0, deadline - time.monotonic()))
            assert not (set(threading.enumerate()) - self.baseline), "worker or timer leaked"
            assert self.errors.empty(), f"background thread failed: {self.errors.get()}"
        finally:
            threading.excepthook = self.original_hook
            threading.setprofile(self.original_profile)

    def event(self):
        event = threading.Event()
        self.releases.append(event)
        return event

    def worker(self, target):
        worker = threading.Thread(target=target, daemon=True)
        self.workers.append(worker)
        worker.start()
        return worker

    def join(self, thread):
        thread.join(WAIT)
        assert not thread.is_alive(), "thread did not terminate"

    def timer(self, thread):
        assert type(thread) is threading.Timer, "callback must run on a real threading.Timer"
        assert thread is not threading.main_thread() and thread.native_id is not None
        self.timer_ids.add(thread.native_id)


def received(items, message):
    try:
        return items.get(timeout=WAIT)
    except queue.Empty:
        raise AssertionError(message) from None


def delivery(threads):
    completed = queue.Queue()

    def callback(value, *, token):
        result = (value, token)
        completed.put((result, threading.current_thread()))
        return result

    debounced = pydash.debounce(callback, 1)
    previous = None
    for value in ("first", "second"):
        assert debounced(value, token=value + "-token") == previous
        result, timer = received(completed, "callback not delivered")
        threads.timer(timer)
        threads.join(timer)  # last_result is assigned after the user callback returns.
        assert result == (value, value + "-token")
        assert debounced.last_result == result
        previous = result


def expired_timer(threads):
    arrived = queue.Queue()
    calls = []
    callback_code = pydash.functions.Debounce._on_timer.__code__

    def pause_expired_timer(frame, event, _):
        if event == "call" and frame.f_code is callback_code:
            # The real Timer has finished waiting and entered pydash's callback.
            # Pause before its lock so another public call can cancel/reschedule it.
            release = threads.event()
            arrived.put((threading.current_thread(), release))
            assert release.wait(WAIT), "expired timer was not released"

    threading.setprofile(pause_expired_timer)
    debounced = pydash.debounce(lambda value: calls.append(value) or value, 1)
    assert debounced("old") is None
    old, release_old = received(arrived, "first timer never fired")
    threads.timer(old)
    assert debounced("latest") is None
    latest, release_latest = received(arrived, "replacement timer never fired")
    threads.timer(latest)
    assert old.finished.is_set(), "rescheduling must cancel the previous timer"
    release_old.set()
    threads.join(old)
    assert calls == [], "stale timer invoked the callback"
    release_latest.set()
    threads.join(latest)
    assert calls == ["latest"], calls
    assert debounced.last_result == "latest"


def blocked_callback(threads):
    entered = queue.Queue()
    release = threads.event()
    attempted = threading.Event()
    returned = queue.Queue()
    first_finished = threading.Event()

    def callback(value):
        entered.put((value, threading.current_thread()))
        if value == "first":
            assert release.wait(WAIT), "first callback was not released"
            first_finished.set()
        else:
            assert first_finished.is_set(), "callbacks overlapped"
        return value

    debounced = pydash.debounce(callback, 1)
    assert debounced("first") is None
    value, first = received(entered, "first callback never entered")
    assert value == "first"
    threads.timer(first)
    acquired = debounced._lock.acquire(blocking=False)
    if acquired:
        debounced._lock.release()
    assert not acquired, "callback must hold the real debounce lock"

    call_code = pydash.functions.Debounce.__call__.__code__

    def caller_arrived(frame, event, _):
        if event == "call" and frame.f_code is call_code and frame.f_locals["self"] is debounced:
            attempted.set()  # The caller has actually entered pydash while the callback is blocked.

    threading.setprofile(caller_arrived)

    def caller():
        returned.put(debounced("second"))

    caller_thread = threads.worker(caller)
    assert attempted.wait(WAIT), "concurrent caller never started"
    release.set()
    threads.join(caller_thread)
    assert received(returned, "concurrent call did not return") == "first"
    value, second = received(entered, "second callback never entered")
    threads.timer(second)
    threads.join(first)
    threads.join(second)
    assert value == debounced.last_result == "second"


def concurrent_callers(threads):
    barrier = threading.Barrier(9, timeout=WAIT)
    threads.barriers.append(barrier)
    completed = queue.Queue()
    calls = []
    state_lock = threading.Lock()

    def callback(worker, sequence, *, token):
        assert token == (worker, sequence), "concurrent arguments were mixed"
        with state_lock:
            calls.append(token)
        if worker == "final":
            completed.put(threading.current_thread())
        return token

    debounced = pydash.debounce(callback, 1)

    def caller(worker):
        barrier.wait()
        for sequence in range(8):
            result = debounced(worker, sequence, token=(worker, sequence))
            assert result is None or (result[0] in range(8) and result[1] in range(8))

    workers = [threads.worker(lambda worker=worker: caller(worker)) for worker in range(8)]
    barrier.wait()
    for worker in workers:
        threads.join(worker)
    debounced("final", 0, token=("final", 0))
    timer = received(completed, "final concurrent call was lost")
    threads.timer(timer)
    # No caller can create more timers now; let every remaining timer terminate.
    for thread in set(threading.enumerate()) - threads.baseline:
        threads.join(thread)
    assert calls[-1] == debounced.last_result == ("final", 0)
    assert len(calls) == len(set(calls)), "a cancelled call executed more than once"


def max_wait(threads):
    completed = queue.Queue()
    started = threading.Event()

    def callback(value):
        completed.put((value, threading.current_thread(), threads.stop.is_set()))
        return value

    # A missing max_wait cannot pass by eventually reaching the quiet-period timer:
    # its 60 seconds exceed both the event deadline and the whole-process deadline.
    debounced = pydash.debounce(callback, 60_000, max_wait=100)

    def producer():
        started.set()
        value = 0
        while not threads.stop.is_set():
            debounced(value)
            value += 1
            threads.stop.wait(0.005)  # Pace calls; no assertion depends on this duration.

    producer_thread = threads.worker(producer)
    assert started.wait(WAIT), "producer never started"
    value, callback_thread, stopped = received(completed, "max_wait did not deliver during the burst")
    assert isinstance(value, int) and value >= 0 and not stopped
    # Upstream may invoke synchronously when a call arrives after max_wait;
    # otherwise its genuine background Timer must deliver the result.
    if callback_thread is not producer_thread:
        threads.timer(callback_thread)
    threads.stop.set()
    threads.join(producer_thread)


def run_case(case, fault=None, timeout=30):
    command = [sys.executable, str(Path(__file__).resolve()), case]
    if fault:
        command.append(fault)
    try:
        return subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              encoding="utf-8", timeout=timeout, check=False)
    except subprocess.TimeoutExpired as error:
        raise AssertionError(f"{case} subprocess timed out; child killed and reaped: {error.stdout}") from error


@pytest.mark.parametrize("case", CASES)
@pytest.mark.parametrize("repetition", range(3))
def test_real_threads(case, repetition):
    result = run_case(case)
    assert result.returncode == 0, result.stdout
    evidence = json.loads(result.stdout)
    assert evidence["case"] == case and evidence["real_time"]
    if case != "max_wait":
        assert evidence["timer_threads"] > 0


@pytest.mark.parametrize("case,fault,message", [
    ("expired_timer", "stale", "stale timer invoked the callback"),
    ("delivery", "exception", "background thread failed: RuntimeError: injected timer failure"),
    ("blocked_callback", "unlocked_callback", "callback must hold the real debounce lock"),
    ("max_wait", "no_max_wait", "max_wait did not deliver during the burst"),
])
def test_real_thread_failures_fail_the_process(case, fault, message):
    result = run_case(case, fault)
    assert result.returncode != 0, "fault injection unexpectedly passed"
    assert message in result.stdout, result.stdout


def test_deadlocked_child_is_killed_and_reaped():
    with pytest.raises(AssertionError, match="subprocess timed out; child killed and reaped"):
        run_case("delivery", "deadlock", timeout=5)


def inject_fault(fault):
    # Negative controls run in disposable children, never in the coverage measurement.
    debounce = pydash.functions.Debounce
    if fault == "stale":
        def stale(self, generation):
            with self._lock:
                self._invoke()
        debounce._on_timer = stale
    elif fault == "unlocked_callback":
        def unlocked(self, generation):
            if generation == self._generation:
                self._invoke()
        debounce._on_timer = unlocked
    elif fault == "exception":
        original = debounce._on_timer
        def raises_after_callback(self, generation):
            original(self, generation)
            raise RuntimeError("injected timer failure")
        debounce._on_timer = raises_after_callback
    elif fault == "no_max_wait":
        original = debounce.__init__
        def without_max_wait(self, func, wait, max_wait=False):
            original(self, func, wait, max_wait=False)
        debounce.__init__ = without_max_wait
    elif fault == "deadlock":
        debounce.__call__ = lambda *args, **kwargs: threading.Event().wait()
    else:
        raise AssertionError(f"unknown fault: {fault}")


if __name__ == "__main__":
    case = sys.argv[1]
    assert case in CASES
    if len(sys.argv) == 3:
        inject_fault(sys.argv[2])
    with Threads() as threads:
        globals()[case](threads)
    print(json.dumps({"case": case, "real_time": True, "timer_threads": len(threads.timer_ids)}))
