[![Build](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&style=flat-square&label=Build)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)

# DotCov.Tool

`dotcov` gates any Cobertura report: it tells you, your CI, or your coding agent whether coverage
passes, and exactly which lines and branches to test next.

```bash
dotnet tool install -g DotCov.Tool
```

## Quick start

```bash
dotcov test --min-line 90 --min-branch 80                 # .NET: run the tests, then gate them
dotcov check TestResults/ --min-line 90 --min-branch 80   # any Cobertura report
```

```
FAIL: line 81.8% (min 90%), branch 50.0% (min 80%) - line and branch coverage below threshold
files below line or branch threshold:
  src/Orders.cs  77.7% lines  33.3% branches
    uncovered 14-15 · branches 12 (1/2), 18 (1/4)
```

The verdict is the first stderr line and matches the exit code. Below it is every file under a
threshold, worst first, with the lines no test runs and the branches only partly taken.

## For agents

- **Streams.** Stdout carries only data: the table, markdown, or JSON. The verdict of `check`,
  `test` and `crap` is always the first stderr line; warnings, excluded files and merged reports
  follow it.
- **JSON.** `--format json` prints one document that opens with `"schemaVersion": 1`, `"command"`,
  `"inputs"` and `"warnings"`. `check`, `test` and `crap` add `gate`: `outcome`, `exitCode`,
  `reason` (the verdict line), the thresholds, and for `check`/`test` the `offenders`, each with
  `path`, `lineRate`, `branchRate`, `uncovered` (`"14-15,21"`) and `partialBranches`.
- **Size.** `--top N` caps the offenders (default 20) or the files of `report`; uncovered lines
  are ranges. `--no-color` or `NO_COLOR=1` removes ANSI.
- **Help.** `dotcov <command> --help` prints that command's usage, options and exit codes. A
  mistyped option answers with the closest valid one and the full option list, exit `2`.
- **`dotcov test`** prints `reports: TestResults/<run>` on stderr after the verdict. With
  `--format json` or `md` it writes the `dotnet test` log to `TestResults/<run>/dotnet-test.log` and
  shows it on stderr only when the run fails.

## Commands

```bash
dotcov test   [<project>] --min-line 80 [-- <dotnet test arguments>]
dotcov check  TestResults/ --min-line 80 --min-branch 60 --format json
dotcov report TestResults/ --top 10                       # the ten worst files
dotcov crap   TestResults/ --max-crap 30                  # per-method risk gate
dotcov diff   before.xml after.xml --format md
dotcov snapshot TestResults/ --commit SHA --branch main --project MyApp
```

`check` and `test` need `--min-line`, `--min-branch`, or both (0 to 100); a dimension left out is
not checked, and `0` disables one. A path is a file or a directory; a directory is searched for
`**/*cobertura*.xml` and every match is merged. Name other files with `--pattern`
(`--pattern "**/coverage.xml"` for gcovr and coverage.py). A directory without a match gets a
warning naming the `.xml` files it holds.

`dotcov test` passes the coverage options of the runner `dotnet test` uses here
(`DOTNET_TEST_RUNNER`, else `test.runner` in `global.json`, else VSTest). Microsoft.Testing.Platform
projects need `Microsoft.Testing.Extensions.CodeCoverage` (TUnit includes it); VSTest projects get
the collector from `Microsoft.NET.Test.Sdk`.

## Exit codes

| Code | Verdict | Meaning |
|---|---|---|
| 0 | `PASS` | Thresholds met; `report`, `diff`, `snapshot`: the command succeeded |
| 1 | `FAIL` | Below a threshold |
| 2 | `error:` | Usage: unknown command or option, invalid value, missing or extra argument, no threshold, a `--source-root` no report declares |
| 3 | `NODATA` | Nothing measured, a dimension missing, an unscored CRAP method, a file whose `<source>` root is unresolved |
| 4 | `DISABLED` | Every threshold is `0`, or `--max-crap` is infinite |
| 5 | `error:` | A path, report, upload or badge failed; a non-passing gate keeps its own code |
| 6 | `error:` | `dotcov test`: the test run failed, coverage was not evaluated |

## `crap`

`crap` scores every method `comp² · (1 − cov)³ + comp`, worst first, with each method's
`file:line`. A fully covered method scores its complexity, so a high score on covered code means
split it, not test it. Coverlet embeds complexity in its report; for other emitters pass
`--metrics` from `dotnet msbuild /t:Metrics`
([Microsoft.CodeAnalysis.Metrics](https://www.nuget.org/packages/Microsoft.CodeAnalysis.Metrics)).
Every method must be scored: one without complexity or lines makes the gate `NODATA`, and so
does a metrics member no covered method matches.

## Options

| Option | Effect |
|---|---|
| `--min-line <n>`, `--min-branch <n>` | `check`, `test`: thresholds from 0 to 100, no default |
| `--format table\|json\|md` | Output format; `md` is GitHub-flavored markdown |
| `--top <n>` | Show the N worst files (`report`) or methods (`crap`), or cap the offenders (`check`, `test`, default 20) |
| `--exclude-generated` | Skip `.g.cs`, `.designer.cs`, `/obj/`, `/bin/`, `/Migrations/`, `GlobalUsings.cs`, `Program.cs` |
| `--exclude-test-code` | Skip test projects and fixtures: `*.Tests/`, `TestSupport/`, `TestFixtures/` |
| `--exclude-external` | Skip code built elsewhere: SourceLink paths (`/_/...`) of dependencies the coverage tool instrumented; otherwise a warning names them |
| `--keep <subs>` | Comma-separated substrings exempt from the exclusions (`diff` applies them to both reports) |
| `--probe-sources` | Several `<source>` roots: resolve each file against the root that holds it on this disk |
| `--source-root <root>` | Several `<source>` roots: resolve against this declared root |
| `--pattern <glob>` | Report file name to search for; default `**/*cobertura*.xml` |
| `--max-chars <n>` | Per-file XML character cap; default `50000000`, `0` = none |
| `--github-summary` | Append markdown to `$GITHUB_STEP_SUMMARY`, on pass and fail |
| `--upload <url>` | POST the JSON document |
| `--badge-dir <dir>` | `check`, `test`: write `coverage-badge.svg` and `coverage-badge.json` for every outcome |
| `--no-color` | No ANSI colour; accepted by every command that reads a report; `test` passes `NO_COLOR=1` to `dotnet test` |

A command rejects an option it does not take, `--name=value`, and a second path; it never
ignores one. An active exclusion is listed on stderr and in the JSON as `excluded` and
`exclusionRules`. Percentages read `62.0%` on every host.

## Also in this family

[DotCov](https://www.nuget.org/packages/DotCov/) — the parser and gates as a library ·
[DotCov.Fallout](https://www.nuget.org/packages/DotCov.Fallout/) — coverage targets for Fallout builds

[Documentation](https://github.com/ANcpLua/dotcov#readme) ·
[Issues](https://github.com/ANcpLua/dotcov/issues) ·
[MIT](https://github.com/ANcpLua/dotcov/blob/main/LICENSE)
