# Real consumer coverage tests

`consumer_coverage.py` is an integration test used by the `consumer-coverage` job in
[CI](../workflows/nuget-publish.yml). It invokes the current DotCov CLI and the actual
composite badge action. Every PR runs both languages on Linux, Windows, and macOS.

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
