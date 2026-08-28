[![Build](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&style=flat-square&label=Build)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)
[![Coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2FANcpLua%2Fdotcov%2Fbadges%2Fcoverage-badge.json&style=flat-square)](https://github.com/ANcpLua/dotcov/tree/badges)

# DotCov.Nuke

Coverage reporting and threshold gating for [NUKE](https://nuke.build) builds — one interface,
no target wiring.

## Getting started

```bash
nuke :add-package DotCov.Nuke
```

```csharp
using DotCov.Nuke;

class Build : NukeBuild, ICoverageReport { }
```

```bash
nuke ReportCoverage --coverage-min-line 80 --coverage-exclude-generated true
```

That is the whole setup. `ReportCoverage` globs
`RootDirectory / "TestResults" / **/coverage.cobertura.xml`, merges everything it finds, renders
the chosen format, writes a markdown block to `$GITHUB_STEP_SUMMARY`, and fails the build when
line or branch coverage is below threshold — naming the files that dragged it there.

The target attaches itself to `ICompile` through `TryDependsOn`, so it hooks into an existing
build without requiring you to inherit `ICompile`.

## Parameters

| Parameter | Default | Effect |
|---|---|---|
| `--coverage-min-line` | `80` | Minimum line coverage percentage |
| `--coverage-min-branch` | `0` | Minimum branch coverage percentage |
| `--coverage-format` | `table` | `table`, `json`, or `markdown` |
| `--coverage-exclude-generated-param` | `false` | Apply `ExclusionRules.WellKnown` before rendering |

Override `CoverageSearchDirectory` in your `Build` class to scan somewhere other than
`RootDirectory / "TestResults"`.

## Notes

A build that measured nothing fails rather than passing quietly — an absent report is not 0% and
not 100%, and a gate that cannot see must not report success. Percentages are invariant-formatted,
so CI logs read `62.0%` on every host, never `62,0%`.

Parsing comes from [DotCov](https://www.nuget.org/packages/DotCov/): streaming `XmlReader`, no
full-DOM load, DTDs prohibited, bounded memory.

## Also in this family

[DotCov.Tool](https://www.nuget.org/packages/DotCov.Tool/) — the `dotcov` CLI ·
[DotCov](https://www.nuget.org/packages/DotCov/) — the parser as a library

## Feedback

[Documentation](https://github.com/ANcpLua/dotcov#readme) ·
[Issues](https://github.com/ANcpLua/dotcov/issues) ·
[MIT](https://github.com/ANcpLua/dotcov/blob/main/LICENSE)
