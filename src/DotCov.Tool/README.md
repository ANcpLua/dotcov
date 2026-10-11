[![Build](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&style=flat-square&label=Build)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)
[![Coverage](https://raw.githubusercontent.com/ANcpLua/dotcov/badges/coverage-badge.svg)](https://github.com/ANcpLua/dotcov/tree/badges)

# DotCov.Tool

`dotcov` turns Cobertura XML into a build decision — a table, a markdown block, a JSON payload,
and an exit code your CI can act on. Local by default; uploads are opt-in.

## Getting started

```bash
dotnet tool install -g DotCov.Tool
```

```bash
dotnet test --results-directory TestResults --coverlet --coverlet-output-format cobertura
dotcov check TestResults/ --min-line 80 --min-branch 60 --exclude-generated
```

```
PASS: line 96.5% (min 80%), branch 93.0% (min 60%) - thresholds met
```

Pass a file or fresh run directory. A directory search matches `**/*cobertura*.xml` beneath it
(hidden directories included) and merges every match, so a sharded test matrix needs no merge step.
Merged reports are listed on stderr. Use a fresh directory for each run to avoid stale reports.
Timestamped coverlet.MTP files match. For generic names such as gcovr's `coverage.xml`, select
the filename explicitly with `--pattern`.

## Commands

```bash
dotcov report   TestResults/ --format table                      # also json or md
dotcov check    TestResults/ --min-line 80 --exclude-generated   # CI gate, exit 1 if below
dotcov crap     TestResults/ --max-crap 6                        # per-method risk gate
dotcov diff     before.xml after.xml --format md                 # compare two reports
dotcov snapshot TestResults/ --commit SHA --branch main --project MyApp
dotcov test     tests/MyApp.Tests --min-line 80 -- -c Release    # dotnet test, then report and gate
```

`--github-summary` writes the markdown table to `$GITHUB_STEP_SUMMARY` on pass **and** fail, so a
green build still shows its number. `--badge-dir <dir>` on `check` and `test` writes the verdict as
`coverage-badge.svg` and `coverage-badge.json`, coloured by the outcome; the
[badge action](https://github.com/ANcpLua/dotcov#badge) publishes them in one CI step.

## Testing and gating in one step

`dotcov test` runs `dotnet test` with Microsoft Code Coverage writing Cobertura into a fresh
`TestResults/<run>` directory, then prints the table and gates that run like `check`:

```bash
dotcov test tests/MyApp.Tests --min-line 80 --exclude-generated -- -c Release
```

It passes the coverage options of the runner `dotnet test` uses in the current directory:
`DOTNET_TEST_RUNNER`, else `test.runner` in the nearest `global.json`, else VSTest. On
Microsoft.Testing.Platform the test project needs `Microsoft.Testing.Extensions.CodeCoverage`
(TUnit includes it); on VSTest, `Microsoft.NET.Test.Sdk` brings the Code Coverage collector.
Arguments after `--` go to `dotnet test` unchanged. Its output goes to stdout, so stderr still
starts with the verdict. A failed test run exits `5` with `error:` and no coverage verdict.

## Exit codes

Since 1.7, usage errors exit `2` instead of `5`; update scripts accordingly. A missing
path argument exits `2`; a supplied nonexistent path exits `5`. Invalid upload URLs
are usage errors; HTTP/connection failures remain execution errors.

`check`, `crap`, and `test` return `0` only for a measured pass:

| Token | Meaning | Exit |
|---|---|---|
| `PASS:` | met the threshold | 0 |
| `FAIL:` | below the threshold | 1 |
| `NODATA:` | the gate lacks the data needed to evaluate | 3 |
| `DISABLED:` | both `check` thresholds are 0, or `crap --max-crap` is infinite, so the gate cannot fail | 4 |
| `error:` | unreadable/nonexistent path, parse/size-cap error, upload or badge write failure, failed test run | 5 |
| `error:` or unknown command | usage: unknown command/flag, invalid value, missing/extra argument | 2 |

`report`, `diff`, and `snapshot` return `0` when rendering and any requested upload succeed.
A passing gate whose badge cannot be written returns `5`; a non-passing gate keeps its code if an
upload or badge write also fails.
Reporting commands do not enforce thresholds. `report --threshold` affects
presentation, not the exit code. Use `check` when CI must require measured coverage.

An existing directory with no matching reports is valid empty input for reporting commands;
a supplied nonexistent path or malformed report exits `5`. An empty `report` displays `no data` in
Markdown and `-` for percentages in the table. Its JSON has `files: []`, zero totals, and no
`lineRate` or `branchRate` fields. These represent missing measurements, not 0% coverage.
For that same empty directory, `check --min-line 1` returns `NODATA:` and exits `3`.

## Deciding what to test next

`crap` scores every method `comp² · (1 − cov)³ + comp` and sorts worst-first:

```
$ dotcov crap TestResults/ --exclude-generated --top 5 --max-crap 6
Method                                              Comp     Cov %      CRAP
----------------------------------------------------------------------------
DotCov.Formatters.MarkdownFormatter.Render            43    100.0%      43.0
DotCov.Tool.DotCovCli.RunAsync                        40    100.0%      40.0
DotCov.Tool.DotCovCli.Diff                            14     50.0%      38.5
DotCov.CoberturaParser.ConsumeClass                   34    100.0%      34.0
DotCov.FileCoverage.MergeWith                         32    100.0%      32.0
----------------------------------------------------------------------------
... 250 more methods below (--top 5)
FAIL: worst CRAP 43.0 (max 6) - 69 of 255 methods above threshold
```

Fully covered code scores its own complexity, fully uncovered code scores `comp² + comp` — so a
100%-covered 43-branch method still scores 43 and the formula is telling you to split it, not to
test it. The default `--max-crap` is 30, the original CRAP threshold; the `6` above also caps
complexity, failing any method above complexity 6 however well it is tested. Complexity comes
from coverlet's per-method attribute automatically; for emitters that write none, pass
`--metrics` from `dotnet msbuild /t:Metrics`
([Microsoft.CodeAnalysis.Metrics](https://www.nuget.org/packages/Microsoft.CodeAnalysis.Metrics)).

## Flags

| Flag | Effect |
|---|---|
| `--exclude-generated` | Skip `.g.cs`, `.designer.cs`, `/obj/`, `/bin/`, `/Migrations/`, `GlobalUsings.cs`, `Program.cs` |
| `--keep <subs>` | Comma-separated substrings exempt from `--exclude-generated`; requires it |
| `--pattern <glob>` | Filename to scan for. Default `**/*cobertura*.xml`, including timestamped MTP reports |
| `--max-chars <n>` | Per-file XML character cap. Default `50000000`; `0` = uncapped |
| `--format` | `table`, `json`, `md` |
| `--github-summary` | Append markdown to `$GITHUB_STEP_SUMMARY` |
| `--upload <url>` | POST the JSON payload |
| `--badge-dir <dir>` | `check`, `test`: write `coverage-badge.svg` and `coverage-badge.json` for every outcome; created if missing |

Commands reject unused flags, `--name=value`, and extra paths; see `dotcov --help`.

`dotcov version` and `dotcov --version` print the version. Percentages use `62.0%` on every host.

## Also in this family

[DotCov](https://www.nuget.org/packages/DotCov/) — the parser as a library, zero dependencies ·
[DotCov.Fallout](https://www.nuget.org/packages/DotCov.Fallout/) — Fallout build component

## Feedback

[Documentation](https://github.com/ANcpLua/dotcov#readme) ·
[Issues](https://github.com/ANcpLua/dotcov/issues) ·
[MIT](https://github.com/ANcpLua/dotcov/blob/main/LICENSE)
