[![Build](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&style=flat-square&label=Build)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)
[![Coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2FANcpLua%2Fdotcov%2Fbadges%2Fcoverage-badge.json&style=flat-square)](https://github.com/ANcpLua/dotcov/tree/badges)
[![dotcov](https://img.shields.io/nuget/v/DotCov.Tool?style=flat-square&label=dotcov&color=0891B2)](https://www.nuget.org/packages/DotCov.Tool/)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![MIT](https://img.shields.io/badge/license-MIT-64748B?style=flat-square)](LICENSE)

# DotCov

Turn Cobertura XML into a build decision. Table, markdown, JSON, and an exit code your CI can
act on — no coverage service, no account, no upload unless you ask for one.

```bash
dotnet tool install -g DotCov.Tool
dotcov check TestResults/ --min-line 80
```

| Package | For | Install |
|---|---|---|
| [![DotCov.Tool](https://img.shields.io/nuget/v/DotCov.Tool?style=flat-square&label=DotCov.Tool&color=0891B2)](https://www.nuget.org/packages/DotCov.Tool/) | CI scripts and your terminal. Native AOT | `dotnet tool install -g DotCov.Tool` |
| [![DotCov](https://img.shields.io/nuget/v/DotCov?style=flat-square&label=DotCov&color=0891B2)](https://www.nuget.org/packages/DotCov/) | Your own code. Zero package references, AOT-clean | `dotnet add package DotCov` |
| [![DotCov.Nuke](https://img.shields.io/nuget/v/DotCov.Nuke?style=flat-square&label=DotCov.Nuke&color=0891B2)](https://www.nuget.org/packages/DotCov.Nuke/) | NUKE builds | `nuke :add-package DotCov.Nuke` |

---

## Fail the build under 80%

```yaml
- run: dotnet test --collect:"XPlat Code Coverage" --results-directory TestResults
- run: dotcov check TestResults/ --min-line 80 --min-branch 60 --exclude-generated
```

```console
PASS: line 96.5% (min 80%), branch 93.0% (min 60%) - thresholds met
```

Pass the directory, not a file — dotcov globs `**/coverage.cobertura.xml` beneath it and merges
every match, so a sharded test matrix needs no merge step. Below threshold it prints the
offending files and exits `1`.

**Fails closed.** A run that measured *nothing* also exits `1` (`NODATA:`), as does a run where
you set every threshold to zero (`DISABLED:`). A gate that can't see must not report success.
Discriminate on the first stderr token, never on the prose:

| Token | Meaning | Exit |
|---|---|---|
| `PASS:` | met the bar | 0 |
| `FAIL:` | below the bar | 1 |
| `NODATA:` | nothing measured | 1 |
| `DISABLED:` | no threshold armed | 1 |
| `error:` | bad path / parse / size cap / bad flag / upload | 1 |
| — | unknown command | 2 |

## Put a coverage table in the PR

```yaml
- run: dotcov check TestResults/ --min-line 80 --exclude-generated --github-summary
```

`--github-summary` appends the markdown to `$GITHUB_STEP_SUMMARY` on **pass and fail** — a
green build still shows its number. Want it as a comment instead? `--format md` writes the same
block to stdout:

```console
$ dotcov report TestResults/ --format md
## Coverage Report

**Line coverage:** 58.3% (7/12)
**Branch coverage:** 50.0% (3/6)

| File | Lines | Line % | Branches | Branch % |
|------|------:|-------:|---------:|---------:|
| `src/Unused.cs` | 0/3 | 0.0% | - | - |
| `src/Parser.cs` | 3/5 | 60.0% | 1/4 | 25.0% |
| `src/Calculator.cs` | 4/4 | 100.0% | 2/2 | 100.0% |
```

## Decide what to test next

Coverage tells you *how much*. CRAP tells you *where*. Every method is scored
`comp² · (1 − cov)³ + comp`, worst first:

```console
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

Fully covered code scores its own complexity; fully uncovered code scores `comp² + comp`. So a
100%-covered 43-branch method still scores 43 — the formula is telling you to split it, not to
test it. `Diff` at 50% is the opposite case: test it. That's the whole loop — take the top row,
pull whichever lever it points at, rerun.

That output is dotcov scored against itself, and at the default `--max-crap 6` it does not pass.

```bash
dotcov crap TestResults/ --max-crap 6              # gate; exit 1 above threshold (at-threshold passes)
dotcov crap TestResults/ --top 10 --format md      # worst offenders for a PR comment
dotcov crap cov.xml --metrics MyApp.Metrics.xml    # when the report has no complexity
```

**Where complexity comes from.** Coverlet embeds it per `<method>` and dotcov uses it
automatically. gcovr, grcov and plain Cobertura don't emit it — generate
`dotnet msbuild /t:Metrics` output with the
[`Microsoft.CodeAnalysis.Metrics`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.Metrics)
package and pass `--metrics`. When both exist the embedded value wins, having measured the
assembly that was actually covered.

**Two honest limits.** `cov` is line coverage, not basis-path coverage, so a method whose lines
all ran but whose branch combinations didn't will flatter itself. And lambdas, local functions
and async state machines compile to separate IL methods; dotcov demangles them back into the
source method (`<M>d__3+MoveNext` → `M`) and reconciles complexity with `Math.Max` rather than
summing, so lambda-heavy methods read lower than Roslyn scores them. Anything unscorable, or any
metrics member matching no method, is printed under its own heading rather than dropped.

## See what a PR did to coverage

```bash
dotcov diff before.cobertura.xml after.cobertura.xml --format md
```

```console
File                            Before     After     Delta      Change
-----------------------------------------------------------------------
services/svc-b/app/main.py       80.0%     33.3%    -46.7%    Modified
-----------------------------------------------------------------------
TOTAL                            80.0%     33.3%    -46.7%
Indirect changes: 8 lines flipped across 1 file
```

Added / removed / modified, per-file deltas, and *indirect* changes — lines that flipped in
files the PR never touched.

## Keep your own coverage history

```bash
dotcov snapshot TestResults/ \
  --commit "$GITHUB_SHA" --branch "$GITHUB_REF_NAME" --project MyApp \
  --upload https://collector.example.com/api/v1/coverage
```

Versioned JSON — commit, branch, project, timestamp, SHA-256 of the report, full body — POSTed
to any endpoint you control. Drop `--upload` and it prints to stdout for `jq`. This repo's own
badge works this way: CI runs `dotcov report --format json`, writes shields.io endpoint JSON to
a `badges` branch, and the badge above reads it. No third-party coverage service anywhere.

## Gate a NUKE build

```csharp
using DotCov.Nuke;

class Build : NukeBuild, ICoverageReport { }
```

```bash
nuke ReportCoverage --coverage-min-line 80 --coverage-exclude-generated true
```

Globs `RootDirectory / "TestResults"`, merges, renders, writes the step summary, fails below
threshold. Attaches to `ICompile` through `TryDependsOn`, so inheriting it is optional.
Parameters: `--coverage-min-line` (80), `--coverage-min-branch` (0), `--coverage-format`
(`table`), `--coverage-exclude-generated-param` (false). Override `CoverageSearchDirectory` to
point elsewhere.

## Build it into your own tool

```csharp
using DotCov;
using DotCov.Formatters;

var report = CoberturaParser.ParsePath("TestResults/")   // file or directory
                            .Exclude(ExclusionRules.WellKnown);

var gate = report.Evaluate(minLinePercent: 80, minBranchPercent: 60);
if (!gate.IsPass)
{
    Console.Error.WriteLine(gate);
    if (gate.LineBelowThreshold) { /* branch on flags, not on Reason text */ }
    return 1;
}
Console.WriteLine(TableFormatter.Format(report));
```

Rates are `double?`: `null` means *unanswerable*, which is neither 0.0 nor 1.0. `Evaluate`
returns four outcomes (`Pass`, `Fail`, `NoData`, `Disabled`) and `IsPass` covers only the first.
`CoverageDiff.Compare` gives you the diff model, `CoberturaParser.ParseMethods*` the per-method
data behind `crap`, and `ParseAsync` a cancellable streaming overload.

Parsing is `XmlReader`-streaming with `DtdProcessing.Prohibit`, `XmlResolver = null`, and a
50,000,000-character-per-file cap (`--max-chars`, or the `maxChars` overloads; `0` disables).
The package has no `PackageReference`s at all and compiles with the trim/AOT analyzers on and
warnings as errors.

Full API: IntelliSense, or [the source](https://github.com/ANcpLua/dotcov/tree/main/src/DotCov) —
every public type is documented there.

---

## Flags

| Flag | Effect |
|---|---|
| `--exclude-generated` | Skip `.g.cs`, `.designer.cs`, `/obj/`, `/bin/`, `/Migrations/`, state machines, `Program.cs` |
| `--keep <subs>` | Comma-separated substrings exempt from the above (`--keep Program.cs`) |
| `--pattern <glob>` | Filename to scan for: `name` or `**/name`. Default `**/coverage.cobertura.xml` (gcovr and coverage.py write `coverage.xml`) |
| `--max-chars <n>` | Per-file XML character cap. Default `50000000`; `0` = uncapped |
| `--format` | `table` · `json` · `md` |
| `--threshold <n>` | `report` only: highlight files below n% |
| `--github-summary` | Append markdown to `$GITHUB_STEP_SUMMARY` |
| `--upload <url>` | POST the JSON payload |

Every percentage is invariant-formatted: `62.0%` on every host, never `62,0%`.

`dotcov --help` prints the same reference with examples.

## License

[MIT](LICENSE) — © Alexander Nachtmann
