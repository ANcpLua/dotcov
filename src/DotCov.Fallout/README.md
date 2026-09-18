[![Build](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&style=flat-square&label=Build)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)
[![Coverage](https://raw.githubusercontent.com/ANcpLua/dotcov/badges/coverage-badge.svg)](https://github.com/ANcpLua/dotcov/tree/badges)

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

That is the whole setup. `ReportCoverage` searches `RootDirectory / "TestResults"` for
`**/*cobertura*.xml` (including timestamped MTP reports and hidden directories), merges everything it finds, renders
the chosen format, appends a markdown block to `$GITHUB_STEP_SUMMARY` when that variable is
set, and fails the build when line or branch coverage is below threshold.

The target attaches itself to `ICompile` through `TryDependsOn`, so it hooks into an existing
build without requiring you to implement `ICompile`.

## Parameters

| Parameter | Default | Effect |
|---|---|---|
| `--coverage-min-line` | `80` | Minimum line coverage percentage (invariant number) |
| `--coverage-min-branch` | `0` | Minimum branch coverage percentage |
| `--coverage-format` | `table` | `table`, `json`, `markdown`, or `md` |
| `--coverage-exclude-generated-param` | `false` | `true`/`false`: apply `ExclusionRules.WellKnown` before gating |
| `--coverage-pattern` | `**/*cobertura*.xml` | `filename` (top level) or `**/filename` (recursive) |
| `--coverage-max-chars-param` | `50000000` | Per-file XML character cap; `0` disables it |

Every value is validated once, before any report is read; an invalid value fails the target
naming the parameter. Override `CoverageSearchDirectory` in your `Build` class to scan
somewhere other than `RootDirectory / "TestResults"`.

## Outcomes

| Situation | Result |
|---|---|
| Search directory missing | Target fails: `Coverage search directory '…' does not exist` |
| Directory exists, no matching report | Target fails: `No files matching '…' found in …` |
| Reports parsed, but nothing measured | Target fails: `Coverage could not be measured: NODATA: …` |
| Coverage below a threshold | Target fails: `Coverage below threshold: FAIL: …` |
| Every threshold is `0` | Target fails: `Coverage gate is disabled …` |
| Thresholds met | Target succeeds; the verdict is logged |

Parser warnings (malformed hit counts, ambiguous source roots, …) are logged one per line
and never change the verdict. An unwritable `GITHUB_STEP_SUMMARY` is logged as a warning and
does not fail an otherwise passing gate. Percentages are invariant-formatted, so CI logs read
`62.0%` on every host, never `62,0%`.

Parsing comes from [DotCov](https://www.nuget.org/packages/DotCov/): streaming `XmlReader`,
no full-DOM load, DTDs ignored and never resolved, bounded memory.

## Also in this family

[DotCov.Tool](https://www.nuget.org/packages/DotCov.Tool/) — the `dotcov` CLI ·
[DotCov](https://www.nuget.org/packages/DotCov/) — the parser as a library

## Feedback

[Documentation](https://github.com/ANcpLua/dotcov#readme) ·
[Issues](https://github.com/ANcpLua/dotcov/issues) ·
[MIT](https://github.com/ANcpLua/dotcov/blob/main/LICENSE)
