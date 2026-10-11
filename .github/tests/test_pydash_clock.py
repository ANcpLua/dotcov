"""Exercise the clock and unchanged upstream assertions, including failure cases."""

import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import threading
import time
from types import SimpleNamespace
import xml.etree.ElementTree as ET

import pydash
import pytest

from pydash_clock import Clock, install_clock


@pytest.fixture
def upstream():
    path = Path(pydash.__file__).resolve().parents[2] / "tests/test_functions.py"
    spec = importlib.util.spec_from_file_location("upstream_functions", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_timer_deadlines_cancellation_and_nested_callbacks():
    clock = Clock()
    calls = []

    def record(label):
        calls.append((label, clock.now()))
        if label == "first":
            clock.timer(0.005, record, kwargs={"label": "nested"}).start()

    cancelled = clock.timer(0.005, record, ("cancelled",))
    cancelled.start()
    cancelled.cancel()
    clock.timer(0.010, record, ("first",)).start()
    clock.timer(0.010, record, ("second",)).start()
    clock.sleep(0.009)
    assert calls == []
    clock.sleep(0.001)
    assert calls == [("first", 10), ("second", 10)]
    clock.sleep(0.020)
    assert calls == [("first", 10), ("second", 10), ("nested", 15)]
    assert clock.now() == 30
    with pytest.raises(RuntimeError):
        cancelled.start()
    with pytest.raises(ValueError):
        clock.sleep(-1)


@pytest.mark.parametrize("name", ["test_debounce", "test_debounce_max_wait"])
@pytest.mark.parametrize("stall", [0, 0.030])
def test_upstream_assertions_survive_scheduler_stalls(upstream, name, stall):
    original_now, original_threading = pydash.now, pydash.functions.threading
    for _ in range(10):
        clock = Clock()
        with pytest.MonkeyPatch.context() as patch:
            install_clock(patch, upstream, clock)

            def stalled_sleep(seconds):
                # Real scheduling delays exceed the upstream 15 ms timer deadline.
                time.sleep(stall)
                clock.sleep(seconds)

            patch.setattr(upstream, "time", SimpleNamespace(sleep=stalled_sleep))
            assert pydash.functions.threading.Lock is threading.Lock
            getattr(upstream, name)()
        assert upstream.time is time
        assert pydash.now is original_now
        assert pydash.functions.threading is original_threading


@pytest.mark.parametrize("fault", ["early", "late"])
def test_upstream_assertions_still_reject_broken_timers(upstream, fault, monkeypatch):
    clock = Clock()
    install_clock(monkeypatch, upstream, clock)

    def broken_timer(interval, function, args=(), kwargs=None):
        return clock.timer(0 if fault == "early" else interval + 1, function, args, kwargs)

    monkeypatch.setattr(pydash.functions.threading, "Timer", broken_timer)
    with pytest.raises(AssertionError):
        upstream.test_debounce()


def test_pytest_plugin_scope_and_junit_evidence(tmp_path):
    tests = tmp_path / "tests"
    tests.mkdir()
    (tests / "test_functions.py").write_text(
        "import threading, time\n"
        "import pydash\n"
        "original_now = pydash.now\n"
        "def test_debounce():\n"
        "    assert pydash.functions.threading is not threading\n"
        "    assert pydash.now() == 0\n"
        "    calls = []\n"
        "    pydash.debounce(lambda: calls.append(pydash.now()), 15)()\n"
        "    time.sleep(0.010)\n"
        "    assert calls == []\n"
        "    time.sleep(0.005)\n"
        "    assert calls == [15]\n"
        "def test_debounce_max_wait():\n"
        "    assert pydash.now() == 0\n"
        "def test_other_test_keeps_real_time():\n"
        "    assert pydash.now is original_now\n"
        "    assert pydash.functions.threading is threading\n"
        "    assert time is __import__('time')\n",
        encoding="utf-8",
    )
    env = {**os.environ, "PYTHONPATH": str(Path(__file__).resolve().parent)}
    result = subprocess.run(
        [sys.executable, "-m", "pytest", "-p", "pydash_clock", "-q", "--junitxml=tests.xml"],
        cwd=tmp_path, env=env, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        encoding="utf-8", check=False,
    )
    assert result.returncode == 0, result.stdout
    cases = ET.parse(tmp_path / "tests.xml").getroot().findall(".//testcase")
    marked = [case.attrib["name"] for case in cases
              if case.find("properties/property[@name='dotcov-clock'][@value='deterministic']") is not None]
    assert marked == ["test_debounce", "test_debounce_max_wait"]
