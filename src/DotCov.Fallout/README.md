[![Build](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&style=flat-square&label=Build)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)

# DotCov.Fallout

Coverage reporting and threshold gating for [Fallout](https://fallout.build) builds — one
interface, no target wiring.

## Getting started

```bash
fallout :add-package DotCov.Fallout
```

```csharp
using DotCov.Fallout;

class Build : FalloutBuild, ICoverageReport { }
```

```bash
fallout ReportCoverage --coverage-min-line 80 --coverage-exclude-generated-param true
```

That is the whole setup. There is no default threshold: set `--coverage-min-line`,
`--coverage-min-branch`, or both, or the target fails naming them before it reads a report.
`ReportCoverage` searches `RootDirectory / "TestResults"` for
`**/*cobertura*.xml` (including timestamped MTP reports and hidden directories), merges everything it finds, renders
the chosen format, appends a markdown block to `$GITHUB_STEP_SUMMARY` when that variable is
set, and fails the build when line or branch coverage is below threshold.

The target attaches itself to `ICompile` through `TryDependsOn`, so it hooks into an existing
build without requiring you to implement `ICompile`.

## Parameters

| Parameter | Default | Effect |
|---|---|---|
| `--coverage-min-line` | none | Minimum line coverage percentage, 0 to 100 (invariant number); set it, `--coverage-min-branch`, or both |
| `--coverage-min-branch` | none | Minimum branch coverage percentage, 0 to 100; a dimension left unset is not checked |
| `--coverage-format` | `table` | `table`, `json`, `markdown`, or `md`; `json` logs the same document as `dotcov check --format json`, gate included |
| `--coverage-json-path` | empty (off) | Also write that JSON document to this file, relative to the root, for every outcome |
| `--coverage-exclude-generated-param` | `false` | `true`/`false`: apply `ExclusionRules.WellKnown` before gating |
| `--coverage-exclude-test-code-param` | `false` | `true`/`false`: apply `ExclusionRules.TestCode` before gating; the excluded files are logged |
| `--coverage-probe-sources-param` | `false` | `true`/`false`: resolve a relative file name under several `<source>` roots against the root that holds it on this machine's disk |
| `--coverage-source-root` | empty (off) | Resolve such file names against this declared root instead |
| `--coverage-pattern` | `**/*cobertura*.xml` | `filename` (top level) or `**/filename` (recursive) |
| `--coverage-max-chars-param` | `50000000` | Per-file XML character cap; `0` disables it |
| `--coverage-badge-directory` | empty (off) | Write `coverage-badge.svg` and `coverage-badge.json` here, relative to the root, for every outcome |

Every value is validated once, before any report is read; an invalid value fails the target
naming the parameter. Override `CoverageSearchDirectory` in your `Build` class to scan
somewhere other than `RootDirectory / "TestResults"`.

## Outcomes

A failing target exits with the same code `dotcov` would, so a script can tell a failed gate
from one that could not measure.

| Situation | Exit | Result |
|---|---|---|
| Thresholds met | 0 | Target succeeds; the verdict is logged |
| Coverage below a threshold | 1 | `Coverage below threshold: FAIL: …`, then the files below it with their uncovered lines |
| No threshold set, one outside 0 to 100, or `--coverage-source-root` not declared by any report | 2 | Fails before reading a report, naming the parameter |
| No matching report, nothing measured, or a file identity unresolved across several `<source>` roots | 3 | `No files matching '…'` or `Coverage could not be measured: NODATA: …` |
| Every threshold is `0` | 4 | `Coverage gate is disabled …` |
| Search directory missing, a report unreadable, or a passing gate whose badge or JSON file cannot be written | 5 | The error, naming the path |

Parser warnings (malformed hit counts, contradictory branch data, ambiguous source roots, …) are
logged one per line. Only an unresolved file identity changes the verdict, to `NODATA`. The badge is written before a failing verdict fails the target, so
a drop turns it red; its colour is the outcome, and its message the line rate, plus the branch
rate when a branch minimum is set. A non-passing gate keeps its own failure message when the badge
cannot be written. To publish it to a branch, pass the directory to the
[badge action](https://github.com/ANcpLua/dotcov#badge) as `badge-directory`. An unwritable `GITHUB_STEP_SUMMARY` is logged as a warning and
does not fail an otherwise passing gate. Percentages are invariant-formatted, so CI logs read
`62.0%` on every host, never `62,0%`.

Parsing comes from [DotCov](https://www.nuget.org/packages/DotCov/): streaming `XmlReader`,
no full-DOM load, DTDs ignored (never processed), external resolution disabled, bounded memory.

## Also in this family

[DotCov.Tool](https://www.nuget.org/packages/DotCov.Tool/) — the `dotcov` CLI ·
[DotCov](https://www.nuget.org/packages/DotCov/) — the parser as a library

## Feedback

[Documentation](https://github.com/ANcpLua/dotcov#readme) ·
[Issues](https://github.com/ANcpLua/dotcov/issues) ·
[MIT](https://github.com/ANcpLua/dotcov/blob/main/LICENSE)
