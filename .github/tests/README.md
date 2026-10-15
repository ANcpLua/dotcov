# Real consumer coverage tests

`consumer_coverage.py` is an integration test used by the `consumer-coverage` job in
[CI](../workflows/nuget-publish.yml). It invokes the current DotCov CLI and the actual
composite badge action. Every push to main and every release tag runs both languages on Linux and Windows.

The upstream projects already have coverage configuration. Their sources and tests are
unchanged: the TypeScript project declares a 100% Jest threshold, and the Python project
configures branch measurement with coverage.py. CI pins source commits, uses the upstream
Yarn lockfile or pinned Python test requirements, and selects Node 24 / Python 3.12.

| Target | Revision | Measurement | Observed baseline (covered / total) |
|---|---|---|---|
| [ts-deepmerge 7.0.3](https://github.com/voodoocreation/ts-deepmerge) | `305a05831a462fb2c353d3cbbff55a0733286f8c` | All 19 tests; Jest 29 V8 provider; Istanbul Cobertura and JSON-summary reporters | 151/154 lines; 31/32 branches; one source file |
| [ItsDangerous 2.2.0](https://github.com/pallets/itsdangerous) | `096c8d42545d3b68ea21a4f890fb2b2d8979c0bd` | All 297 tests; coverage.py 7.10.4, branch mode, `itsdangerous` source | 420/438 lines; 94/102 branches; eight source files |

The baseline was measured on 2026-10-11. Assertions compare exact integer counts from the
producer's native JSON with DotCov's JSON, both overall and for every file. They do not derive
expected counts by parsing Cobertura or by calling DotCov twice. Percentages displayed by
coverage.py combine statements and branches; Istanbul truncates percentages while DotCov's
report rounds them, so displayed percentages are not used as the oracle.

Both reports contain real misses. A 90% line / 90% branch gate must pass; raising either
threshold separately to 100% must return `FAIL` with exit 1. The upstream TypeScript suite's
own 100% coverage threshold rejects the V8 measurement, so the script requires that exit and
separately verifies all 19 tests passed with no failing test suites. Coverage.py's native 100%
check also rejects its report. No XML is rewritten and no test is removed to manufacture
these outcomes. ItsDangerous's upstream reporting config also lists its tests directory as
a source root: the resulting `FileIdentityAmbiguous` diagnostic must remain visible, while
the per-file native comparison proves the paths in this report are attributed correctly. DotCov's own 100% coverage requirement is unchanged.

The composite action then publishes a green badge followed by a red branch-failure badge.
The test compares badge numbers with the independent native counts, asserts action exit
codes and SVG verdicts, and checks the exact JSON/SVG bytes and two commits in a local bare
origin. Reports, producer results, DotCov results, and badges are uploaded as CI artifacts.
No badge branch is pushed to GitHub.

## Exporter limitation found by the comparison

With ts-deepmerge's default Babel instrumentation, Istanbul's native summary counts 23
branches, but its Cobertura class-level lines contain only 17. The reporter attaches branches
only to statement lines, dropping multiline conditional-expression branches. The V8
measurement preserves all 32 of its measured branches in the exported line entries and is
the configuration validated here. This is why the test uses a native JSON oracle instead
of trusting a green gate or the Cobertura root's summary alone. The original Babel export
cannot provide the missing per-file/per-line branch data to DotCov.

## Reproduce

Check out either upstream project at the revision above. Install its locked dependencies
as shown in the CI job, and build `src/DotCov.Tool` in Release configuration. From this
repository, with a fresh output directory:

```sh
python .github/tests/consumer_coverage.py measure typescript \
  --project /path/to/ts-deepmerge --output /tmp/dotcov-ts-run \
  --tool src/DotCov.Tool/bin/Release/net10.0/DotCov.Tool.dll

python .github/tests/consumer_coverage.py measure python \
  --project /path/to/itsdangerous --output /tmp/dotcov-python-run \
  --tool src/DotCov.Tool/bin/Release/net10.0/DotCov.Tool.dll
```

For the Python command, use the interpreter where the pinned requirements and ItsDangerous
are installed. The badge phase is exercised by GitHub's composite-action runner in CI.

## Published README badge comparisons

`readme_badge_coverage.py` and the `readme-badge-coverage` matrix add an independent,
published baseline to the fresh producer comparisons above. All three targets embed a coverage
badge in the README at the exact tested revision:

| Target | Source commit | Published coverage behind the README badge |
|---|---|---|
| [compare-versions 6.1.1](https://github.com/omichelsen/compare-versions/blob/98e81116ef4197b42dca8c3fde8d1e8166e2a81f/README.md) | `98e81116ef4197b42dca8c3fde8d1e8166e2a81f` | [Coveralls build 68651498](https://coveralls.io/builds/68651498.json): 100%; 245/245 lines and 88/88 branches |
| [peek-readable 5.4.2](https://github.com/Borewit/peek-readable/blob/9cef754de7baad45e43195bbb35bd9bbf30abbc9/README.md) | `9cef754de7baad45e43195bbb35bd9bbf30abbc9` | [Coveralls build 71980502](https://coveralls.io/builds/71980502.json): 205/209 lines, 54/57 branches; 97.36842105263158% combined |
| [pydash 8.1.0](https://github.com/dgilland/pydash/blob/f46de3669742c075e5379c1e193c86c1c2c70dc2/README.rst) | `f46de3669742c075e5379c1e193c86c1c2c70dc2` | [Coveralls build 81442862](https://coveralls.io/builds/81442862.json) and [Python 3.12 job 187016260](https://coveralls.io/jobs/187016260.json): 100% |

The evidence in [readme-badges](readme-badges/) was retrieved on 2026-10-11. Coveralls
records identify the repository, commit SHA, measured percentage, counts, and badge image.
Author names, emails, and commit messages were omitted from the saved build responses.
`targets.json` records the original endpoints and exact README image markup. CI verifies
that markup still exists in the pinned checkout, checks `git rev-parse HEAD` against the
service record, and compares exact counts before checking the badge display. Compare-versions
and pydash point to the saved `coveralls-100.svg`; peek-readable points to `coveralls-97.svg`. Pydash's README uses
a legacy Shields wrapper for that Coveralls result; the reference is the linked service's
commit-specific result, not a request to the moving Shields endpoint.

Compare-versions uses its original c8/Mocha coverage provider, npm lockfile, and Node 20
(pinned here to 20.15.1). All 339 tests run. Its fresh native JSON, Cobertura/DotCov totals,
and historical Coveralls line and branch totals must agree exactly.

Peek-readable 5.4.2 uses its immutable Yarn 4.6.0 lockfile, TypeScript 5.7.3,
c8 10.1.3, Mocha 11.1.0, and Node 20 (pinned to 20.18.2). The same upstream TypeScript
build produces the JavaScript measured by its original report. All 29 tests run with zero
failures or skips. Native JSON and DotCov must agree per file across all six compiled source
files, and their totals must equal Coveralls' **205/209 lines and 54/57 branches** exactly.
The saved build also links the source commit to the original GitHub Actions run.

The full suite exercises promise-based Node and Web stream reads, end-of-stream and error
handling, a real 500 ms Node stream delivery, and cancellation of a pending read. No timer,
clock, callback, test assertion, or coverage report is replaced or rewritten. The upstream
Web BYOB helper can deliver immediately despite receiving a delay argument; the native
timer requirement deliberately applies to the Node stream cases, not to that helper.
`peek_real_timers.cjs` is an observing Mocha hook outside the upstream coverage scope. It
checks timer and clock identities around every test and uses `async_hooks` to require a
real timer from the upstream stream helper and an executed callback before the delayed-read
test completes. CI retains the audit JSON. Three isolated negative controls prove the hook
rejects a replaced clock, a test without a native timer, and a timer that was not awaited;
the controls contribute no coverage and do not modify the measured upstream suite.

Coveralls includes branches in this project's headline: `(205 + 54) / (209 + 57)` gives
97.36842105263158%, displayed as a 97% badge. Its published line and branch rates are
98.08612440191388% and 94.73684210526316%. DotCov's badge displays these separately as
`98.1% / 94.7%`; CI verifies both formats using the same published counts. The only tolerance
is 1e-12 percentage points for Coveralls' serialized floating-point combined percentage;
all four integer counts must match exactly. DotCov JSON's two-decimal display is checked
separately from the exact fractions.

The baseline CLI/action gates use 98.086% lines and 94.736% branches (published rates rounded
down to three decimals), in addition to exact count equality. Independent 100% line and
100% branch gates must reject the real misses. The real composite action must publish the
passing green badge and both failing red badges with exit 1 and failed step outcomes.
All three publications go to a local bare repository; exact JSON/SVG bytes and the three
commits are checked. `continue-on-error` applies only to these expected failing actions;
the following verification steps require their failures and cannot silently accept them.
DotCov's own 100% line/branch gate and the other targets' gates are unchanged.

Pydash uses Python 3.12 and the coverage.py 7.16.0 / pytest 9.1.1 / pytest-cov 7.1.0
versions recorded in its published [Python 3.12 CI job](https://github.com/dgilland/pydash/actions/runs/33272967265/job/99154667022).
That upstream job ran Python 3.12.14. CI selects the available Python 3.12 patch release on
each host (setup-python does not distribute 3.12.14 on Windows); exact count
comparisons remain mandatory. Its 2,274 unit tests and doctests run with upstream's targets, source configuration,
exclusions, and 100% gate. CI installs the package editable so per-file paths refer to the
checkout. A local pytest plugin supplies a virtual clock and timer queue only to
`test_debounce` and `test_debounce_max_wait`: their 10 ms sleeps and 15 ms timers otherwise
race the hosted macOS scheduler. All source files and test assertions remain unchanged,
and the measurement checks that Git's tracked source/test/config files are unchanged.
The plugin replaces module references for those two tests and restores them after each
test; other tests and the Python standard library keep real time. Timers execute pydash's
actual callbacks in deadline order, including cancellation and max-wait behavior.

CI first runs `test_pydash_clock.py` on both hosts. It repeats both unchanged upstream
tests with real 30 ms scheduling stalls, verifies clock isolation and JUnit evidence, and
proves that deliberately early or late timers still fail upstream assertions. The
full measurement then requires all 2,274 tests to pass with zero skips or errors, evidence
that exactly the two intended tests used the clock, and the same exact coverage counts.
There are no retries, merged partial measurements, extra exclusions, or relaxed thresholds.

A separate `test_pydash_threads.py` suite runs on Linux and Windows using pydash's
real clock, `threading.Timer`, and locks, without loading the virtual-clock plugin. Five
scenarios each run three times: background delivery and last-result reuse; cancellation
after a timer has fired; a caller arriving during a blocked callback; eight concurrent
callers with argument/result checks; and `max_wait` delivery during an ongoing burst.
Every repetition must pass. Events and barriers coordinate progress, with generous
liveness deadlines instead of short-sleep timing assertions. For the expired-timer race,
a profiling hook pauses genuine timer threads at entry to `_on_timer`, before the lock;
the test then reschedules through the public API and releases the old and new threads in
order. The timer implementation, clock, lock, and pydash callback body are not replaced.

Four negative controls prove the suite rejects a missing stale-generation guard, missing
callback lock, disabled `max_wait`, and an uncaught background-thread exception. A fifth
proves a deadlocked child is killed and reaped. These mutations occur only in disposable
negative-control processes. All 20 checks must pass; background exceptions and leaked
threads fail the process. Normal cleanup releases barriers, cancels outstanding timers,
and joins owned threads, while a 30-second subprocess deadline bounds deadlocks. CI retains
the separate JUnit report. These checks contribute no data to the independent 2,274-test
coverage measurement, whose exact counts and 100% gates remain mandatory.

The saved upstream log excerpt contains the original coverage table and test summary
(with terminal color escapes removed). Fresh per-file counts must also match that table:
2,607/2,607 lines across 14 files. GitHub's saved job metadata and commit statuses connect
the log and the Python 3.12 Coveralls job to the same SHA.

Pydash's overall Coveralls build aggregates five Python environments with separate installed
paths (13,033/13,033 lines). The matrix compares its published 100% percentage and the exact
Python 3.12 per-file counts; it does not compare one environment's line count with the
five-environment sum. Upstream did not collect branch coverage, and the test preserves this
line-only mode. The existing ItsDangerous tests continue to verify Python branches and
negative gates.

The six compare-versions/pydash jobs also execute DotCov's 100% gate and the real composite
badge action, assert a `100.0%` green line-coverage badge, and compare the published JSON/SVG
bytes in a local bare origin. Together with the three peek-readable jobs, all nine jobs gate releases and retain the reports and reference evidence as
artifacts. They read saved historical service evidence so external badge changes or service
outages do not change the baseline. Updating a target requires refreshing its commit,
README markup, service evidence, dependencies, and measured results together.

## Publication verification

`test_verify_publication.py` exercises the release verifier through a local HTTP
feed, including incomplete uploads, delayed availability, stale registration data
and invalid public package contents. Run it with:

```sh
python3 -B -m unittest discover -s .github/tests -p test_verify_publication.py -v
```

The release workflow separately verifies every actual public NuGet package and its
source commit. It holds back the tool pointer until all six runtime/library
packages can be downloaded, then installs and tests the public native tool before
creating the GitHub Release. Availability checks retain their attempts and use
fresh four-minute processes, with five windows before failing.
