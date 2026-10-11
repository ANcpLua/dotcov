[![Build](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&style=flat-square&label=Build)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)

# DotCov

Turn Cobertura XML into a build decision — a table, a markdown block, a JSON payload, and an
exit code your CI can act on. Local by default; uploads are opt-in.

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

Pass a report file or a fresh run directory. A directory search finds `**/*cobertura*.xml`
(including timestamped MTP reports and hidden directories) and merges everything it finds, so a sharded test matrix needs
no merge step. Merged reports are listed on stderr. Use a fresh directory for each run to
avoid stale reports. Below threshold it lists offending files and exits `1`.

## Packages

| Package | For | Install |
|---|---|---|
| [DotCov.Tool](src/DotCov.Tool/README.md) | CI scripts and your terminal; Native AOT | `dotnet tool install -g DotCov.Tool` |
| [DotCov](src/DotCov/README.md) | Your own code; zero package references, AOT-clean | `dotnet add package DotCov` |
| [DotCov.Fallout](src/DotCov.Fallout/README.md) | [Fallout](https://fallout.build) builds; one interface, no target wiring | `fallout :add-package DotCov.Fallout` |

Package READMEs cover CLI flags, library APIs, and Fallout parameters.

## Commands

| Command | Effect |
|---|---|
| `dotcov report <path>` | Parse and render as `table`, `json`, or `md`; `--threshold N` highlights files below N% |
| `dotcov check <path>` | CI gate on `--min-line` (default `80`) and `--min-branch` (default `0`); `--badge-dir <dir>` writes the verdict as a [badge](#badge) |
| `dotcov crap <path>` | Per-method risk gate, `comp² · (1 − cov)³ + comp`, worst first; `--max-crap` (default `30`), `--top N`, `--metrics <file>` |
| `dotcov diff <before> <after>` | Per-file deltas plus lines that flipped in files the change never touched |
| `dotcov snapshot <path>` | Versioned JSON with `--commit`, `--branch`, `--project`, and a SHA-256 of the reports it read |
| `dotcov test [<project>]` | Runs `dotnet test` with Microsoft Code Coverage into a fresh `TestResults/<run>`, then reports and gates it like `check`, `--badge-dir` included; arguments after `--` go to `dotnet test` |

`<path>` is a file or a directory. `--github-summary` appends the markdown block to
`$GITHUB_STEP_SUMMARY` on pass **and** fail, `--upload <url>` POSTs the JSON payload to an
endpoint you control, and `dotcov --help` prints the full flag reference with examples. A flag
the command does not use, a misspelled flag, `--name=value`, or a second path is an error, never
a silently applied default.

Run `dotcov version` or `dotcov --version` to print the version.

## Outcomes

Since 1.7, usage errors exit `2` (formerly `5`); execution errors remain `5`.
Omitting a path exits `2`; supplying a nonexistent path exits `5`. Update script branches.

`report`, `diff`, and `snapshot` return `0` when rendering and any requested upload succeed,
including when the input contains no coverage data. That is command success, not a coverage
pass. `check`, `crap`, and `test` return `0` only for a measured pass. Outcomes:

| Token | Meaning | Exit |
|---|---|---|
| `PASS:` | Thresholds met | 0 |
| `FAIL:` | Below a threshold | 1 |
| `NODATA:` | The gate lacks the data needed to evaluate | 3 |
| `DISABLED:` | Both `check` thresholds are `0`, or `crap --max-crap` is infinite | 4 |
| `error:` | Unreadable/nonexistent path, parse/size-cap error, upload or badge write failure, failed test run | 5 |
| `error:` or unknown command | Usage: unknown command/flag, invalid value, missing/extra argument | 2 |

Use `check` when CI must require measured coverage. Branch on the exit code. See the [CLI exit-code contract](src/DotCov.Tool/README.md#exit-codes)
for details. Percentages are invariant-formatted, so CI logs read `62.0%` on every
host, never `62,0%`.

Parsing is streaming `XmlReader`: no full-DOM load, DTDs ignored (never processed), external
resolution disabled, and a 50,000,000-character cap per file (`--max-chars`; `0` disables it).

## Badge

`check` and `test` write their verdict as a badge with `--badge-dir <dir>`: `coverage-badge.svg`
and `coverage-badge.json`, for every outcome. The colour is the gate's outcome: green for `PASS`,
red for `FAIL`, grey for `NODATA`, yellow for `DISABLED`. The message is the line rate, and the
branch rate too when `--min-branch` is above `0` (`100.0% / 98.5%`), rounded down like the
verdict when it falls short. A branch rate below its minimum turns the badge red even at
`100.0%` lines.

This repository enforces 100% line and branch coverage in CI without publishing a badge branch.
The badge action below is optional for consumers who want to publish coverage in their own repository.
It creates a `badges` branch on first use:

```yaml
permissions:
  contents: write

steps:
  # … checkout, setup-dotnet, and a test run writing Cobertura to TestResults/
  - uses: ANcpLua/dotcov/.github/actions/badge@v1.7.0
    with:
      reports: TestResults
      min-line: 80
      min-branch: 60
      # Publish from main only; pull requests render and gate without pushing.
      publish: ${{ github.ref == 'refs/heads/main' }}
```

Then show it in your README:

```markdown
![Coverage](https://raw.githubusercontent.com/<owner>/<repo>/badges/coverage-badge.svg)
```

To render it through shields.io instead, use
`https://img.shields.io/endpoint?url=<raw url of coverage-badge.json>`.

The step commits only when the badge's contents change, then fails unless the gate passed.
`fail-on-gate: false` keeps `FAIL`, `NODATA` and `DISABLED` green; an error such as an unreadable
report still fails. The other inputs are `pattern`, `args` (further `dotcov check` flags, such as
`--exclude-generated`), `branch`, `version` (the `DotCov.Tool` version to install), and `command`
(run a dotcov you built instead); see [action.yml](.github/actions/badge/action.yml).

When your build already gates and draws the badge (`--badge-dir`, or the Fallout component's
`--coverage-badge-directory`), pass that directory as `badge-directory` instead of `reports`: the
action then only publishes, and your gate keeps the verdict. Run the step with
`if: ${{ !cancelled() }}` so a failing gate still publishes its red badge.

A private repository cannot serve the image from its own branch: GitHub's image proxy fetches
README images without credentials, and shields.io cannot read the JSON either. A badge hosted
publicly renders, but shows your coverage number to anyone.

## Contributing

Build, test and coverage targets run through [Fallout](https://fallout.build):

```bash
dotnet tool install -g Fallout.GlobalTool   # once
fallout Test                                # run the tests
fallout Coverage                            # tests plus the coverage gate
```

## Feedback

[Issues](https://github.com/ANcpLua/dotcov/issues) ·
[Release notes](docs/releases/1.7.0.md) ·
[MIT](LICENSE)
