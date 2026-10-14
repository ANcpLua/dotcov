[![Build](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&style=flat-square&label=Build)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)
[![NuGet](https://img.shields.io/nuget/v/DotCov.Tool?style=flat-square&label=DotCov.Tool)](https://www.nuget.org/packages/DotCov.Tool/)

# DotCov

A coverage gate for any Cobertura report: one command tells you, your CI, or your coding agent
whether coverage passes, and exactly which lines and branches to test next.

```bash
dotnet tool install -g DotCov.Tool
```

## Quick start

Run the tests and gate them in one step (.NET):

```bash
dotcov test --min-line 90 --min-branch 80
```

Or gate a report you already have (.NET, TypeScript, Python, anything that writes Cobertura):

```bash
dotcov check TestResults/ --min-line 90 --min-branch 80
```

```
FAIL: line 81.8% (min 90%), branch 50.0% (min 80%) - line and branch coverage below threshold
files below line or branch threshold:
  src/Orders.cs  77.7% lines  33.3% branches
    uncovered 14-15 · branches 12 (1/2), 18 (1/4)
```

The first line is the verdict, and the exit code says the same: `0` pass, `1` fail. Below it,
every file under a threshold, worst first, with the lines no test runs and the branches only
partly taken. Add tests for those lines, run the command again, repeat until it passes.

Thresholds are required: pass `--min-line`, `--min-branch`, or both (0 to 100). A dimension you
leave out is not checked.

## For AI agents

dotcov is built to be driven by a coding agent:

- **One verdict, one place.** Stdout carries only data; the verdict (`PASS`, `FAIL`, `NODATA`,
  `DISABLED`) is always the first line of stderr. The exit code alone is enough to branch on.
- **JSON everywhere.** `--format json` on `report`, `check`, `test`, `crap` and `diff` (and
  `snapshot`, which is JSON) prints one document with `schemaVersion`, `command`, `inputs` and
  `warnings`. `check`, `test` and `crap` add a `gate` object; its `offenders` list is the work
  queue:

  ```bash
  dotcov check TestResults/ --min-line 90 --format json
  ```

  ```json
  "gate": {
    "outcome": "fail", "exitCode": 1,
    "reason": "FAIL: line 81.8% (min 90%), branch 50.0% (min 80%) - line and branch coverage below threshold",
    "offenders": [
      { "path": "src/Orders.cs", "lineRate": 77.77, "branchRate": 33.33, "uncovered": "14-15",
        "partialBranches": [ { "line": 12, "covered": 1, "total": 2 } ] }
    ]
  }
  ```

- **Few tokens.** Uncovered lines are ranges (`"14-15,21"`), `--top N` caps the list (default 20)
  and says how many more there are, and `--no-color` (accepted by every command that reads a
  report) or `NO_COLOR=1` strips ANSI.
- **Help where you are.** `dotcov <command> --help` prints only that command. A mistyped flag
  answers `Did you mean '--min-line'?` and lists the command's valid options, with exit `2`.
- **Nothing silent.** An exclusion, a report that matched no file, a method that could not be
  scored: each is named on stderr and in the JSON, never dropped.

## Commands

| Command | Does |
|---|---|
| `dotcov test [<project>] --min-line N` | Runs `dotnet test` with coverage into a fresh `TestResults/<run>`, then gates that run like `check`. Arguments after `--` go to `dotnet test` |
| `dotcov check <path> --min-line N` | Gates line and branch coverage of a report file or directory |
| `dotcov report <path>` | Shows coverage per file; `--top N` shows the N worst files |
| `dotcov crap <path>` | Gates per-method risk, `comp² · (1 − cov)³ + comp`, worst first with `file:line` |
| `dotcov diff <before> <after>` | Shows the files whose coverage changed |
| `dotcov snapshot <path>` | Writes JSON stamped with `--commit`, `--branch`, `--project` |

A directory is searched for `**/*cobertura*.xml` and every match is merged, so a sharded test
matrix needs no merge step. Other file names need `--pattern` (`--pattern "**/coverage.xml"`).
`dotcov --help` lists every option.

## Exit codes

| Code | Verdict | Meaning |
|---|---|---|
| 0 | `PASS` | Thresholds met (`report`, `diff`, `snapshot`: the command succeeded) |
| 1 | `FAIL` | Below a threshold |
| 2 | `error:` | Usage: unknown command or option, invalid value, no threshold |
| 3 | `NODATA` | Nothing to judge: no lines measured, an unscored method, an unresolved file |
| 4 | `DISABLED` | Every threshold is 0, so the gate cannot fail |
| 5 | `error:` | A report or file could not be read, written or uploaded |
| 6 | `error:` | `dotcov test`: the test run failed; coverage was not evaluated |

## Risky methods: `crap`

```bash
dotcov crap TestResults/ --max-crap 30
```

```
Method              Comp     Cov %      CRAP  Location
------------------------------------------------------
App.Orders.Place       6     66.7%       7.4  src/Orders.cs:12
App.Orders.Cancel      3    100.0%       3.0  src/Orders.cs:30
```

A fully covered method scores its complexity, so a high score on covered code means split it,
not test it. Coverlet embeds complexity in its report; for other emitters pass
`--metrics <file>` from `dotnet msbuild /t:Metrics`. Every method must be scored: one the gate
cannot score makes it `NODATA` rather than a pass.

## Other languages

DotCov reads any Cobertura report. TypeScript with Jest:

```bash
npm test -- --coverage --coverageProvider=v8 --coverageReporters=cobertura
dotcov check coverage/cobertura-coverage.xml --min-line 90 --min-branch 90
```

Python with coverage.py:

```bash
python -m coverage run --branch --source=your_package -m pytest
python -m coverage xml -o coverage.xml
dotcov check coverage.xml --min-line 90 --min-branch 90
```

When a report declares several `<source>` roots, a relative file name does not say which root
it belongs to. DotCov never guesses: the gate answers `NODATA` until you add `--probe-sources`
(resolve against the disk, where the sources are checked out) or `--source-root <root>`. CI
checks DotCov's counts against the producers' own numbers on real upstream projects; see the
[consumer test notes](.github/tests/README.md).

## Exclusions

`--exclude-generated` skips generated code, build output, migrations, `GlobalUsings.cs` and
`Program.cs`. `--exclude-test-code` skips test projects and fixtures. `--keep <substrings>`
exempts paths from both. Every command that reads a report takes them (`diff` applies them to
both sides). Stderr and the JSON (`excluded`, `exclusionRules`) list what was left out.

## GitHub badge

`check` and `test` draw their verdict with `--badge-dir <dir>`. The badge action gates and
publishes it to a `badges` branch in one step:

```yaml
permissions:
  contents: write

steps:
  # … checkout, setup-dotnet, and a test run writing Cobertura to TestResults/
  - uses: ANcpLua/dotcov/.github/actions/badge@v4.1.0
    with:
      reports: TestResults
      min-line: 80
      min-branch: 60
      publish: ${{ github.ref == 'refs/heads/main' }}
```

```markdown
![Coverage](https://raw.githubusercontent.com/<owner>/<repo>/badges/coverage-badge.svg)
```

Green is `PASS`, red `FAIL`, grey `NODATA`, yellow `DISABLED`. The other inputs are in
[action.yml](.github/actions/badge/action.yml). A private repository cannot serve the image
to GitHub's image proxy.

## Packages

| Package | For |
|---|---|
| [DotCov.Tool](src/DotCov.Tool/README.md) | The `dotcov` command; Native AOT on macOS ARM64 and Linux, portable elsewhere |
| [DotCov](src/DotCov/README.md) | The parser and gates as a library, no dependencies |
| [DotCov.Fallout](src/DotCov.Fallout/README.md) | One interface that adds coverage targets to a [Fallout](https://fallout.build) build |

## Upgrading

4.0 moved every verdict to stderr, gave a failed `dotcov test` run its own exit code `6`, and
reshaped the JSON. The [Release notes](docs/releases/4.1.0.md) cover this release and link the
4.0 changes; older ones are in [docs/releases](docs/releases/).

## Contributing

```bash
dotnet tool install -g Fallout.GlobalTool   # once
fallout Test                                # the tests
fallout Coverage                            # the tests plus the 100% coverage gate
```

[Issues](https://github.com/ANcpLua/dotcov/issues) · [MIT](LICENSE)
