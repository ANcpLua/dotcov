[![Build](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&style=flat-square&label=Build)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)
[![Coverage](https://raw.githubusercontent.com/ANcpLua/dotcov/badges/coverage-badge.svg)](https://github.com/ANcpLua/dotcov/tree/badges)

# DotCov.Tool

`dotcov` turns Cobertura XML into a build decision — a table, a markdown block, a JSON payload,
and an exit code your CI can act on. No coverage service, no account, no upload unless you ask
for one.

## Getting started

```bash
dotnet tool install -g DotCov.Tool
```

```bash
dotnet test --collect:"XPlat Code Coverage" --results-directory TestResults
dotcov check TestResults/ --min-line 80 --min-branch 60 --exclude-generated
```

```
PASS: line 96.5% (min 80%), branch 93.0% (min 60%) - thresholds met
```

Pass the directory, not a file: `dotcov` globs `**/coverage.cobertura.xml` beneath it and merges
every match, so a sharded test matrix needs no merge step. gcovr and coverage.py name their file
`coverage.xml` — point at it with `--pattern "**/coverage.xml"`.

## Commands

```bash
dotcov report   TestResults/ --format table|json|md              # parse and render
dotcov check    TestResults/ --min-line 80 --exclude-generated   # CI gate, exit 1 if below
dotcov crap     TestResults/ --max-crap 6                        # per-method risk gate
dotcov diff     before.xml after.xml --format md                 # compare two reports
dotcov snapshot TestResults/ --commit SHA --branch main --project MyApp
```

`--github-summary` writes the markdown table to `$GITHUB_STEP_SUMMARY` on pass **and** fail, so a
green build still shows its number.

## Exit codes

Everything that is not a verified pass exits non-zero, including a run that measured nothing.
The first stderr token is the discriminator — branch on it, not on the message text:

| Token | Meaning | Exit |
|---|---|---|
| `PASS:` | met the threshold | 0 |
| `FAIL:` | below the threshold | 1 |
| `NODATA:` | nothing was measured | 1 |
| `DISABLED:` | every threshold was 0, so nothing was checked | 1 |
| `error:` | bad path, parse failure, size cap, bad flag value, upload failure | 1 |
| — | unknown command | 2 |

## Deciding what to test next

`crap` scores every method `comp² · (1 − cov)³ + comp` and sorts worst-first:

```
$ dotcov crap TestResults/ --exclude-generated --top 5
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
test it. Complexity comes from coverlet's per-method attribute automatically; for emitters that
write none, pass `--metrics` from `dotnet msbuild /t:Metrics`
([Microsoft.CodeAnalysis.Metrics](https://www.nuget.org/packages/Microsoft.CodeAnalysis.Metrics)).

## Flags

| Flag | Effect |
|---|---|
| `--exclude-generated` | Skip `.g.cs`, `.designer.cs`, `/obj/`, `/bin/`, `/Migrations/`, state machines, `Program.cs` |
| `--keep <subs>` | Comma-separated substrings exempt from the above |
| `--pattern <glob>` | Filename to scan for. Default `**/coverage.cobertura.xml` |
| `--max-chars <n>` | Per-file XML character cap. Default `50000000`; `0` = uncapped |
| `--format` | `table`, `json`, `md` |
| `--github-summary` | Append markdown to `$GITHUB_STEP_SUMMARY` |
| `--upload <url>` | POST the JSON payload |

`dotcov --help` prints the full reference with examples. Percentages are invariant-formatted
everywhere: `62.0%` on every host, never `62,0%`.

## Also in this family

[DotCov](https://www.nuget.org/packages/DotCov/) — the parser as a library, zero dependencies ·
[DotCov.Nuke](https://www.nuget.org/packages/DotCov.Nuke/) — NUKE build component

## Feedback

[Documentation](https://github.com/ANcpLua/dotcov#readme) ·
[Issues](https://github.com/ANcpLua/dotcov/issues) ·
[MIT](https://github.com/ANcpLua/dotcov/blob/main/LICENSE)
