# Changelog

Rolling task-session log, oldest → newest, capped at 20 entries.

## Task 1 — 2026-05-15 — Coverlet-cased `branch="True"`, branch dedup, 100% self-coverage

Built on top of `b306779` (which switched `ParseClass` to a full `<class>` subtree walk
with per-source-line Math.Max aggregation across multiple IL-type class blocks).

Changed:
- Case-insensitive match for `branch="true"` / `branch="True"` in `CoberturaParser`. The
  previous literal-pattern compare rejected Coverlet's Pascal-cased value (`XmlConvert
  .ToString(bool)`), silently dropping every branch line in Coverlet output — branch counts
  rendered as a fake 100% because `TotalBranches` was 0.
- New `BranchLinesSeen` set on `LineAccumulator` so branches are counted once per source
  line. Without this, lines that appear both under `<methods>/<method>/<lines>` and
  `<class>/<lines>` would double-count their branch totals.
- `CoverageDiff.Compare` switch reordered to remove the unreachable `_ => throw` default.
- `Ansi.EnableOnWindows` marked `[ExcludeFromCodeCoverage]` — its two branches can only be
  exercised on different OSes; the "never throws" contract test still covers it.
- Added `coverlet.runsettings` excluding `[CompilerGenerated]` and `[GeneratedCode]` types
  and `obj/` source-generator output, so async-state-machine phantom branches don't cap us.

Verified:
- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` followed by
  `dotcov report TestResults/ --exclude-generated` reports **Lines 100%** /
  **Branches 100%** across 165 tests.
- Regression tests cover the methods+class-level duplication (with `branch="True"`
  branches that previously double-counted), case-insensitive `branch` attr matching across
  `true`/`True`/`TRUE`, missing/malformed `hits`, and Removed/Added file rendering across
  all three formatters (Table, Markdown, JSON).

Notes:
- Numbering starts at 1; no prior CHANGELOG existed in repo metadata.
- The pre-existing `97.9% line / 100% branch` figure in commit `ee3bf32` was an artefact
  of the casing bug — `branch="True"` lines were ignored, so total branches was 0 and the
  rate trivially rendered as 100%.

## Task 2 — 2026-05-15 — Codecov-CLI 11.2.8 local feature exercise

Codecov CLI already installed via Homebrew (`codecov-cli 11.2.8`, bottled 2026-04-02).
No project file changed; this was a local feature exercise against the existing
`TestResults/coverage.cobertura.xml` (165 tests, 100% line/branch) and a synthetic
JUnit XML under `/tmp/codecov-test/`.

Changed:
- Nothing in repo. Only working-tree files are gitignored (`TestResults/`,
  `tests/DotCov.Tests/TestResults/`) plus this CHANGELOG entry.

Verified (each output captured in the chat transcript):
- `codecovcli --help` enumerates 9 subcommands: `create-commit`, `create-report`,
  `do-upload`, `empty-upload`, `pr-base-picking`, `process-test-results`,
  `send-notifications`, `upload-coverage`, `upload-process`.
- `process-test-results --file /tmp/codecov-test/junit-flat.xml --disable-search`
  parsed the synthetic JUnit (3 testruns, 1 failure correctly classified as
  `Outcome.Failure` by `test_results_parser`).
- `do-upload --dry-run` is the only truly local upload command: with
  `-u http://127.0.0.1:1` it never opens a socket and prints
  `Found 1 coverage files to report` → `dry-run option activated. NOT sending data`.
- `do-upload` auto-discovery from project root found both
  `TestResults/coverage.cobertura.xml` and the duplicate under
  `tests/DotCov.Tests/TestResults/<guid>/`.
- CI env simulation (`--auto-load-params-from githubactions` + `GITHUB_*` env vars)
  threaded the synthesized `name`/`branch`/`sha` into the upload bundle.
- `--report-type test_results` accepted the same JUnit XML as a test-results upload
  candidate.
- `upload-process --dry-run` is **not** fully offline: it sequentially invokes
  `create-commit` → `create-report` → `do-upload`, and only the last respects
  `--dry-run`. A real network call to `https://codecov.io/upload/...` registered
  commit `289dc73…` against `ANcpLua/dotcov` (public-repo tokenless path);
  Codecov v2 API confirms `commitid` set, `totals/state/report = null` — only
  metadata, no coverage payload.
- Same `upload-process` redirected to `-u http://127.0.0.1:1` fails on
  `create-commit` after 3 retries — confirming the pre-steps cannot be bypassed.
- `pr-base-picking` and `empty-upload` are pure-API commands and surface
  identical retry-then-traceback behaviour offline.

Notes:
- **Bug, codecov-cli 11.2.8 + `test-results-parser` Rust ext**: `build_message()`
  always emits "✅ All tests successful" regardless of `payload.failed`. Reproduced
  by patching the Python side to print `payload.failed=1` then calling
  `build_message(payload)` directly — message is unchanged. Worth filing upstream
  against `getsentry/test-results-parser`.
- `--exclude tests/DotCov.Tests/TestResults` did **not** suppress the duplicate
  cobertura under that path; the file-finder matches folder name, not path prefix.
- Homebrew `codecov-cli` symlinks three binaries (`codecov`, `codecov-cli`,
  `codecovcli`) at `/opt/homebrew/bin/`, all resolving to the same `11.2.8` bottle.
- `pip install codecov-cli` from the docs was unnecessary — Homebrew bottle is
  newer and isolates Python deps under `libexec/`. No global `pip` pollution.
