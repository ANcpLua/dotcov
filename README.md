[![Build](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&style=flat-square&label=Build)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)
[![Coverage](https://raw.githubusercontent.com/ANcpLua/dotcov/badges/coverage-badge.svg)](https://github.com/ANcpLua/dotcov/tree/badges)

# DotCov

Turn Cobertura XML into a build decision — a table, a markdown block, a JSON payload, and an
exit code your CI can act on. No coverage service, no account, no upload unless you ask for one.

## Migrating from 0.x

Version 1.0 replaces `DotCov.Nuke` with `DotCov.Fallout` and separates report discovery from
XML parsing (`ReportResolver.Resolve` replaces the parser's path methods). See the
[1.0 migration notes](docs/releases/1.0.0.md) for both upgrades.

## Getting started

```bash
dotnet tool install -g DotCov.Tool
```

```bash
dotcov check TestResults/ --min-line 80 --min-branch 60 --exclude-generated
```

```
PASS: line 96.5% (min 80%), branch 93.0% (min 60%) - thresholds met
```

That is the whole setup. Point `dotcov` at the directory your test run wrote its Cobertura
reports to, not at a file: it searches for `**/*cobertura*.xml` (including timestamped MTP
reports and hidden directories) and merges everything it finds, so a sharded test matrix needs
no merge step. When it merges more than one report it lists them on stderr; give each test run
a fresh results directory, or an earlier run's report is merged too. Below threshold it prints
the offending files and exits `1`.

## Packages

| Package | For | Install |
|---|---|---|
| [DotCov.Tool](src/DotCov.Tool/README.md) | CI scripts and your terminal; Native AOT | `dotnet tool install -g DotCov.Tool` |
| [DotCov](src/DotCov/README.md) | Your own code; zero package references, AOT-clean | `dotnet add package DotCov` |
| [DotCov.Fallout](src/DotCov.Fallout/README.md) | [Fallout](https://fallout.build) builds; one interface, no target wiring | `fallout :add-package DotCov.Fallout` |

Each package README is the reference for that package: the CLI's commands and flags, the
library API, and the Fallout component's parameters and outcomes.

## Commands

| Command | Effect |
|---|---|
| `dotcov report <path>` | Parse and render as `table`, `json`, or `md`; `--threshold N` highlights files below N% |
| `dotcov check <path>` | CI gate on `--min-line` (default `80`) and `--min-branch` (default `0`) |
| `dotcov crap <path>` | Per-method risk gate, `comp² · (1 − cov)³ + comp`, worst first; `--max-crap` (default `30`), `--top N`, `--metrics <file>` |
| `dotcov diff <before> <after>` | Per-file deltas plus lines that flipped in files the change never touched |
| `dotcov snapshot <path>` | Versioned JSON with `--commit`, `--branch`, `--project`, and a SHA-256 of the reports it read |
| `dotcov test [<project>]` | Runs `dotnet test` with Microsoft Code Coverage into a fresh `TestResults/<run>`, then reports and gates it like `check`; arguments after `--` go to `dotnet test` |

`<path>` is a file or a directory. `--github-summary` appends the markdown block to
`$GITHUB_STEP_SUMMARY` on pass **and** fail, `--upload <url>` POSTs the JSON payload to an
endpoint you control, and `dotcov --help` prints the full flag reference with examples. A flag
the command does not use, a misspelled flag, `--name=value`, or a second path is an error, never
a silently applied default.

## Outcomes

`report`, `diff`, and `snapshot` return `0` when rendering and any requested upload succeed,
including when the input contains no coverage data. That is command success, not a coverage
pass. `check`, `crap`, and `test` return `0` only for a measured pass. Their outcomes and shared CLI
errors are listed below.

| Token | Meaning | Exit |
|---|---|---|
| `PASS:` | Thresholds met | 0 |
| `FAIL:` | Below a threshold | 1 |
| `NODATA:` | The gate lacks the data needed to evaluate | 1 |
| `DISABLED:` | Both `check` thresholds are `0`, or `crap --max-crap` is infinite | 1 |
| `error:` | Missing path, bad path, parse failure, size cap, bad flag value, unknown flag, extra path, upload failure, failed test run | 1 |
| — | Unknown command | 2 |

Use `check` when CI must require measured coverage. For gate outcomes and CLI errors, branch
on the first stderr token, not the message text. See the [CLI exit-code contract](src/DotCov.Tool/README.md#exit-codes)
for empty-report output. Percentages are invariant-formatted, so CI logs read `62.0%` on every
host, never `62,0%`.

Parsing is streaming `XmlReader`: no full-DOM load, DTDs ignored (never processed), external
resolution disabled, and a 50,000,000-character cap per file (`--max-chars`; `0` disables it).

## Contributing

Build, test and coverage targets run through [Fallout](https://fallout.build):

```bash
dotnet tool install -g Fallout.GlobalTool   # once
fallout Test                                # run the tests
fallout Coverage                            # tests plus the coverage gate
```

## Feedback

[Issues](https://github.com/ANcpLua/dotcov/issues) ·
[Release notes](docs/releases/1.2.0.md) ·
[MIT](LICENSE)
