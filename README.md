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
no merge step. Below threshold it prints the offending files and exits `1`.

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
| `dotcov crap <path>` | Per-method risk gate, `comp² · (1 − cov)³ + comp`, worst first; `--max-crap` (default `6`), `--top N`, `--metrics <file>` |
| `dotcov diff <before> <after>` | Per-file deltas plus lines that flipped in files the change never touched |
| `dotcov snapshot <path>` | Versioned JSON with `--commit`, `--branch`, `--project`, and a SHA-256 of the report |

`<path>` is a file or a directory. `--github-summary` appends the markdown block to
`$GITHUB_STEP_SUMMARY` on pass **and** fail, `--upload <url>` POSTs the JSON payload to an
endpoint you control, and `dotcov --help` prints the full flag reference with examples.

## Outcomes

| Token | Meaning | Exit |
|---|---|---|
| `PASS:` | Thresholds met | 0 |
| `FAIL:` | Below a threshold | 1 |
| `NODATA:` | Reports parsed, but nothing measured | 1 |
| `DISABLED:` | Every threshold is `0` | 1 |
| `error:` | Bad path, parse failure, size cap, bad flag value, upload failure | 1 |
| — | Unknown command | 2 |

Everything that is not a verified pass exits non-zero. The first stderr token is the
discriminator — branch on it, not on the message text. Percentages are invariant-formatted,
so CI logs read `62.0%` on every host, never `62,0%`.

Parsing is streaming `XmlReader`: no full-DOM load, DTDs prohibited and external resolution
disabled, and a 50,000,000-character cap per file (`--max-chars`; `0` disables it).

## Feedback

[Issues](https://github.com/ANcpLua/dotcov/issues) ·
[Release notes](docs/releases/1.0.0.md) ·
[MIT](LICENSE)
