[![NuGet — DotCov](https://img.shields.io/nuget/v/DotCov?label=DotCov&color=0891B2)](https://www.nuget.org/packages/DotCov/)
[![NuGet — DotCov.Tool](https://img.shields.io/nuget/v/DotCov.Tool?label=DotCov.Tool&color=0891B2)](https://www.nuget.org/packages/DotCov.Tool/)
[![NuGet — DotCov.Nuke](https://img.shields.io/nuget/v/DotCov.Nuke?label=DotCov.Nuke&color=0891B2)](https://www.nuget.org/packages/DotCov.Nuke/)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-7C3AED)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![CI](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&label=CI)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)
[![Coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2FANcpLua%2Fdotcov%2Fbadges%2Fcoverage-badge.json)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)
[![License](https://img.shields.io/github/license/ANcpLua/dotcov?label=License&color=white)](LICENSE)

# DotCov

Streaming Cobertura XML coverage toolkit — zero-dependency parser, `dotnet` global tool, and NUKE build extension. Streams without loading the DOM; per-file input is capped at 50,000,000 XML characters by default (~50 MB), raisable via the CLI's `--max-chars` flag or the library's `maxChars` overloads.

## Packages

| Package | Purpose | Install |
|---|---|---|
| `DotCov` | Streaming Cobertura parser + diff/snapshot library. Zero runtime deps. | `dotnet add package DotCov` |
| `DotCov.Tool` | `dotnet` global CLI: `report`, `check`, `diff`, `snapshot`. | `dotnet tool install -g DotCov.Tool` |
| `DotCov.Nuke` | NUKE build component `ICoverageReport` for CI gating. | `nuke :add-package DotCov.Nuke` |

## Quick Start

### CLI

```bash
dotnet tool install -g DotCov.Tool

# Parse and render (auto-discovers **/coverage.cobertura.xml under a directory)
dotcov report TestResults/

# Non-Coverlet report names (gcovr, coverage.py, ReportGenerator): override the scan filename
dotcov report gcovr-output/ --pattern "**/coverage.xml"

# CI gate — exits 1 if below threshold, writes markdown to $GITHUB_STEP_SUMMARY
dotcov check TestResults/ --min-line 80 --exclude-generated --github-summary

# CRAP gate — per-method change risk: comp²·(1−cov)³+comp, exit 1 above --max-crap
dotcov crap TestResults/ --max-crap 6 --github-summary

# Compare two reports
dotcov diff before.cobertura.xml after.cobertura.xml --format md

# Pipeline-ready JSON snapshot, optionally POSTed to a collector
dotcov snapshot TestResults/ \
  --commit "$GITHUB_SHA" --branch "$GITHUB_REF_NAME" --project MyApp \
  --upload https://collector.example.com/api/v1/coverage
```

### NUKE

```csharp
using DotCov.Nuke;

class Build : NukeBuild, ICoverageReport { }
```

```bash
nuke ReportCoverage --coverage-min-line 80 --coverage-exclude-generated true
```

The `ICoverageReport` target globs `RootDirectory / "TestResults" / **/coverage.cobertura.xml`, merges everything it finds, renders the chosen format, writes a markdown block to `$GITHUB_STEP_SUMMARY`, and fails the build if line/branch coverage is below threshold. It hooks `ICompile` opportunistically via `TryDependsOn` — no requirement that you inherit it.

### Library

```csharp
using DotCov;
using DotCov.Formatters;

var report = CoberturaParser.ParsePath("TestResults/");        // file or directory
report = report.Exclude(ExclusionRules.WellKnown);             // strip generated code

Console.WriteLine(TableFormatter.Format(report));
var gate = report.Evaluate(minLinePercent: 80, minBranchPercent: 60);
if (!gate.IsPass)
{
    Console.Error.WriteLine(gate);   // e.g. NODATA: line n/a (min 80%) - report carries no line data
    if (gate.LineBelowThreshold)     // structured verdicts: branch on these, never parse Reason prose
        Console.Error.WriteLine("line coverage is the offender");
    Environment.Exit(1);
}
```

```csharp
// Diff
var diff = CoverageDiff.Compare(
    CoberturaParser.ParseFile("before.xml"),
    CoberturaParser.ParseFile("after.xml"));

foreach (var r in diff.Regressions)
    Console.WriteLine($"{r.Path}: {r.Before:P1} → {r.After:P1} ({r.Delta:+0.0%;-0.0%})");
```

```csharp
// Async streaming for very large files
await using var stream = File.OpenRead("coverage.cobertura.xml");
var report = await CoberturaParser.ParseAsync(stream, ct: cancellationToken);
```

## Features

- **Streaming `XmlReader`** — no `XDocument.Load`, no full-DOM allocation. Per-class cursor walks the document with bounded memory; the per-file character cap defaults to 50,000,000 chars (~50 MB) and is configurable via the `maxChars` overloads and the CLI's `--max-chars` flag (`0` = no cap).
- **Hardened XML** — `DtdProcessing.Prohibit`, `XmlResolver = null`, character cap. No XXE / billion-laughs / external-entity surface.
- **Three output formats** — `table` (terminal), `json` (pipelines, snapshots, `--upload`), `markdown` (PR comments, `$GITHUB_STEP_SUMMARY`).
- **CI gating** — `check` and `ReportCoverage` both exit non-zero when line/branch coverage is below threshold, with the offending files listed in the failure output.
- **CRAP gate** — `crap` scores every method with `comp²·(1−cov)³+comp` (worst-first table, JSON, or markdown) and exits non-zero when any method is strictly above `--max-crap`. Complexity comes from coverlet's per-method attribute or a `dotnet msbuild /t:Metrics` file; whatever can't be scored or matched is listed, never silently dropped.
- **Coverage diffs** — added / removed / modified files, per-file delta, aggregate line-rate delta. Drop into PR comments to surface regressions.
- **Snapshots** — versioned JSON with commit SHA, branch, project, timestamp, SHA-256 file hash, and the full report. `--upload <url>` POSTs to any HTTP endpoint.
- **Exclusion rules** — `ExclusionRules.WellKnown` filters `.g.cs`, `.designer.cs`, `/obj/`, `/bin/`, `/Migrations/`, async state machines (`d__`), and `GlobalUsings`. Or pass your own substring patterns to `report.Exclude(...)`.
- **Locale-proof output** — every numeric rendering (table, markdown, gate summaries) is invariant-formatted. CI logs and scripts see `62.0%` on every host, never `62,0%`.
- **Self-measured badge** — CI runs dotcov on its own `TestResults` and publishes shields.io endpoint JSON to the [`badges` branch](https://github.com/ANcpLua/dotcov/tree/badges); the Coverage badge above reads it. No external coverage service.
- **Native-AOT-friendly** — `DotCov` library has zero runtime package references.

## CLI Reference

```text
dotcov - Cobertura coverage toolkit

Commands:
  report   <path> [--format table|json|md] [--threshold N]      Parse and display coverage
  check    <path> --min-line N [--min-branch N]                 CI gate (exit 1 if below)
  crap     <path> [--metrics <file>] [--max-crap N] [--top N]   CRAP gate: comp^2*(1-cov)^3+comp per
           [--format table|json|md]                             method (exit 1 if any method is
                                                                strictly above --max-crap; default 6)
  diff     <before> <after> [--format table|json|md]            Compare two reports
  snapshot <path> [--commit SHA] [--branch B] [--project P]     Pipeline-ready JSON payload
                                                                (identity defaults to 'unknown')
  version                                                       Show version

Global flags:
  --exclude-generated       Skip generated files, migrations, state machines
  --keep <substrings>       Exempt comma-separated paths from --exclude-generated
  --pattern <glob>          Report filename to scan directories for: 'filename'
                            (top level only) or '**/filename' (recursive)
                            (default **/coverage.cobertura.xml)
  --max-chars <N>           Per-file XML character cap (default 50000000; 0 = no cap)
  --upload <url>            POST JSON payload to any endpoint
  --github-summary          Write markdown to $GITHUB_STEP_SUMMARY

<path> can be a file or a directory. Directories are scanned for **/coverage.cobertura.xml;
override the filename with --pattern (gcovr and coverage.py emit coverage.xml).
```

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success; for `check` and `crap`, the gate passed. |
| `1` | The gate failed or was inconclusive (`NODATA` — nothing measured, `DISABLED` — all thresholds 0), or the command could not run: parse/IO/size-cap error, invalid flag value, upload failure. |
| `2` | Unknown command. |

Exit 1 deliberately fails closed across all of those conditions. The first stderr token (`FAIL:` / `NODATA:` / `DISABLED:` / `error:`) is the machine-readable discriminator between a genuine coverage failure, an inconclusive gate, and a could-not-measure error.

## The CRAP gate (`dotcov crap`)

CRAP — Change Risk Anti-Patterns (Alberto Savoia & Bob Evans, popularized by Uncle Bob):

```
CRAP(m) = comp(m)² · (1 − cov(m))³ + comp(m)
```

where `comp` is the method's cyclomatic complexity and `cov` approximates its basis-path
coverage by the method's **line**-coverage ratio (0..1). Fully covered code scores its
complexity (`comp 5, cov 1 → 5`); fully uncovered code scores `comp² + comp`
(`comp 5, cov 0 → 30`; `comp 1, cov 0 → 2`). The score explodes quadratically with
complexity and cubically with missing coverage — exactly the two levers a refactoring can
pull: split the method, or test it.

```bash
dotcov crap TestResults/                                   # coverlet-embedded complexity
dotcov crap coverage.xml --metrics MyApp.Metrics.xml       # external complexity
dotcov crap TestResults/ --max-crap 6 --top 10 --format md --github-summary
```

The gate exits `1` when any method scores **strictly above** `--max-crap` (default 6).
A method exactly at the threshold passes — the same at-threshold-passes,
epsilon-tolerant comparison policy as `check`. Zero scorable methods is `NODATA`,
exit 1: a gate that cannot see must not exit 0.

**The loop.** The threshold exists for agents as much as humans: run `dotcov crap`, take the
worst-first table's top row, either split the method (comp ↓ quadratic payoff) or cover its
missed branches (cov ↑ cubic payoff), rerun, repeat until exit 0. The exit code — not
judgment — decides when the loop stops.

### Where complexity comes from (and what each source measures)

| Source | How | What it measures |
|---|---|---|
| Coverage report (preferred, zero extra files) | coverlet writes `complexity` per `<method>` | Cyclomatic complexity **per IL method**: a lambda, local function, or async state machine is a *separate* IL method. `dotcov` folds those back into their source method (lines merged; complexity reconciled with `Math.Max`, not summed — so a method with heavy lambdas can under-report vs. Roslyn). |
| `--metrics <file>` | `dotnet msbuild /t:Metrics` with the [`Microsoft.CodeAnalysis.Metrics`](https://www.nuget.org/packages/Microsoft.CodeAnalysis.Metrics) package | Roslyn's cyclomatic complexity **per source method**, nested lambdas and local functions included. Used only for methods the coverage report carries no usable complexity for — the embedded value wins because it measured the exact assembly that was covered. |

Not every emitter measures complexity: gcovr/grcov/cover2cover write a placeholder `0`
(cyclomatic complexity is ≥ 1 by construction, so values below 1 are treated as "not
measured", never as a measurement), and the original Cobertura omits the attribute — for
those, pass `--metrics`.

`cov` is line coverage, not basis-path coverage: a line-covered method with unexercised
branch combinations scores better than it strictly deserves. That is the standard CRAP
approximation; it is stated here so nobody mistakes the number for path coverage.

### Matching and honesty

Metrics members are matched to coverage methods by (type name + method name), with a
normalization table for compiler name mangling: `Ns.Type/<M>d__3` + `MoveNext` → `Ns.Type.M`
(async/iterator state machines), `<>c__DisplayClass…` + `<M>b__…` → `M` (lambdas),
`<M>g__Local|…` → `M` (local functions), `` Type`1 `` → `Type` (generic arity),
`get_X`/`set_X` ↔ property accessors (falling back to the property's aggregate complexity
when the metrics file predates `<Accessors>` output), constructors ↔ `.ctor`, operators ↔
`op_*` names.

What cannot be scored or matched is **listed, never silently dropped**:

- methods with coverage but no usable complexity appear under *Unscored* (they never fail
  the gate — and never vanish);
- metrics methods/accessors that matched no coverage method appear under *Unmatched metrics
  members* (a normalization gap, compiled-out code, or an uninstrumented member).

Both lists ride along in every format (table trailer, markdown sections, JSON arrays).

## NUKE Parameters

`ICoverageReport` exposes the following parameters (prefix `--coverage-`):

| Parameter | Default | Description |
|---|---|---|
| `--coverage-min-line` | `80` | Minimum line coverage percentage |
| `--coverage-min-branch` | `0` | Minimum branch coverage percentage |
| `--coverage-format` | `table` | `table`, `json`, or `markdown` |
| `--coverage-exclude-generated-param` | `false` | Apply `ExclusionRules.WellKnown` before rendering |

Override `CoverageSearchDirectory` in your `Build` class to scan somewhere other than `RootDirectory / "TestResults"`.

## Public API surface

```csharp
namespace DotCov;

public static class CoberturaParser
{
    CoverageReport Parse(Stream stream, long maxChars = 50_000_000);
    Task<CoverageReport> ParseAsync(Stream stream, long maxChars = 50_000_000, CancellationToken ct = default);
    CoverageReport ParseFile(string path, long maxChars = 50_000_000);
    CoverageReport ParseDirectory(string directory, string pattern = "**/coverage.cobertura.xml");
    CoverageReport ParseDirectory(string directory, string pattern, long maxChars);   // cap override
    CoverageReport ParsePath(string path);   // dispatches on file vs. directory
    CoverageReport ParsePath(string path, long maxChars);                             // cap override

    // Opt-in method-level detail (CRAP gate input) — raw per-<method> entries, kept distinct;
    // the class-level Parse family and its dedupe-into-file-sets semantics are untouched.
    IReadOnlyList<MethodCoverage> ParseMethods(Stream stream, long maxChars = 50_000_000);
    IReadOnlyList<MethodCoverage> ParseMethodsFile(string path, long maxChars = 50_000_000);
    IReadOnlyList<MethodCoverage> ParseMethodsDirectory(string directory, string pattern = "**/coverage.cobertura.xml");
    IReadOnlyList<MethodCoverage> ParseMethodsDirectory(string directory, string pattern, long maxChars);
    IReadOnlyList<MethodCoverage> ParseMethodsPath(string path);
    IReadOnlyList<MethodCoverage> ParseMethodsPath(string path, long maxChars);
}

public sealed class CoverageReport
{
    static readonly CoverageReport Empty;
    IReadOnlyList<FileCoverage> Files;
    IReadOnlyList<CoverageWarning> Warnings { get; init; }   // parser/merge anomalies
    // null == unanswerable (no data), which is NOT 0.0 and NOT 1.0. An empty report has no rate.
    double? LineRate, BranchRate;
    double? StrictLineRate;          // Codecov-style: partials and misses both depress the rate
    bool HasLineData, HasBranchData;
    GateResult Evaluate(double minLinePercent, double minBranchPercent = 0);
    IEnumerable<FileCoverage> BelowPercent(double linePercent);   // omits unmeasured files
    CoverageReport Exclude(IEnumerable<string> patterns);
    CoverageReport Exclude(IEnumerable<string> patterns, IEnumerable<string> keep);
    static CoverageReport Merge(CoverageReport a, CoverageReport b);
}

public readonly record struct FileCoverage(
    string Path, int LinesHit, int LinesTotal, int BranchesHit, int BranchesTotal)
{
    double? LineRate, BranchRate, StrictLineRate;   // null when the file carries no such data
    bool HasBranchData;
    int StrictlyHitLines { get; init; }     // init-only — fill via ClassifyLines
    int PartiallyHitLines { get; init; }    // init-only — fill via ClassifyLines
    IReadOnlyList<int> UncoveredLines { get; init; }
    IReadOnlyList<BranchDetail> PartialBranches { get; init; }
    IReadOnlyDictionary<int, int> LineHits { get; init; }
    IReadOnlyDictionary<int, (int Covered, int Total)> BranchesByLine { get; init; }
    LineStatus GetLineStatus(int line);   // Hit / Partial / Miss
    bool TryGetLineStatus(int line, out LineStatus status);   // false = not tracked
    (FileCoverage Merged, IReadOnlyList<CoverageWarning> Warnings) MergeWith(FileCoverage other);

    // Single-pass classifier — fill StrictlyHitLines / PartiallyHitLines when hand-building.
    static (int Strict, int Partial) ClassifyLines(
        IReadOnlyDictionary<int, int> lineHits,
        IReadOnlyDictionary<int, (int Covered, int Total)> branchesByLine);
}

// A threshold check has four outcomes, not two. Collapsing them into a bool is how a build
// that measured nothing comes to look identical to one that measured everything and passed.
public enum GateOutcome { Pass, Fail, NoData, Disabled }

public readonly record struct GateResult(
    GateOutcome Outcome, double? LineRate, double? BranchRate,
    double MinLinePercent, double MinBranchPercent, string Reason)
{
    bool IsPass;                 // Pass only — Disabled is not a pass, nothing was verified
    bool IsInconclusive;         // NoData or Disabled
    bool LineBelowThreshold;     // structured verdicts — branch on these, never parse Reason
    bool BranchBelowThreshold;   // false when the branch threshold was never armed
}

public enum LineStatus { Miss, Partial, Hit }     // Codecov-style three-state
public readonly record struct BranchDetail(int Line, int Covered, int Total);

public enum CoverageWarningKind { BranchTotalMismatch, MalformedConditionCoverage }
public readonly record struct CoverageWarning(
    CoverageWarningKind Kind, string File, int Line, string Detail);

public static class CoverageDiff
{
    CoverageDiffResult Compare(CoverageReport before, CoverageReport after);
}

public sealed class CoverageDiffResult
{
    IReadOnlyList<FileDelta> Files;
    double BeforeRate, AfterRate, Delta;
    IEnumerable<FileDelta> Regressions, Improvements, Added, Removed;
    IEnumerable<FileDelta> WithLineChanges;   // files with at least one line-level flip
    int TotalLineChanges;
}

public readonly record struct FileDelta(
    string Path, double? Before, double? After, double Delta, FileChangeKind Change)
{
    IReadOnlyList<LineDelta> LineChanges { get; init; }   // Codecov-style indirect changes
}

public enum FileChangeKind { Unchanged, Added, Removed, Modified }

// Closed sealed-hierarchy: every variant carries exactly the data the diff actually has,
// so illegal combinations (Added with BeforeHits, Removed with AfterHits…) are
// compile-time-unrepresentable. Base constructor is private — only the four nested sealed
// records can derive. Match<T> / Switch are abstract: adding a fifth variant breaks every
// callsite at compile time.
public abstract record LineDelta
{
    int Line { get; }
    abstract T Match<T>(Func<Added, T> added, Func<Removed, T> removed,
                        Func<NewlyHit, T> newlyHit, Func<NewlyMissed, T> newlyMissed);
    abstract void Switch(Action<Added> added, Action<Removed> removed,
                         Action<NewlyHit> newlyHit, Action<NewlyMissed> newlyMissed);

    public sealed record Added(int Line, int AfterHits) : LineDelta;
    public sealed record Removed(int Line, int BeforeHits) : LineDelta;
    public sealed record NewlyHit(int Line, int BeforeHits, int AfterHits) : LineDelta;
    public sealed record NewlyMissed(int Line, int BeforeHits, int AfterHits) : LineDelta;
}

public sealed record CoverageSnapshot(
    string CommitSha, string Branch, string Project,
    DateTimeOffset Timestamp, string? FileHash, CoverageReport Report);

// ── CRAP gate ──

public readonly record struct MethodCoverage(
    string ClassName, string MethodName, string Signature, string File,
    int StartLine, int EndLine, int LinesHit, int LinesTotal, int? Complexity)
{
    double? LineRate;                                    // null when the method carries no lines
    IReadOnlyDictionary<int, int> LineHits { get; init; }
}

public static class CrapAnalysis
{
    double Score(int complexity, double coverage);       // comp²·(1−cov)³+comp
    CrapReport Analyze(IReadOnlyList<MethodCoverage> methods,
                       IReadOnlyList<CodeMetricsMember>? metricsMembers = null);
    IReadOnlyList<MethodCoverage> ExcludeFiles(          // same substring semantics as Exclude
        IReadOnlyList<MethodCoverage> methods, IEnumerable<string> patterns, IEnumerable<string> keep);
}

public sealed class CrapReport
{
    IReadOnlyList<CrapMethod> Methods;                   // scored; formatters order worst-first
    IReadOnlyList<CrapUnscoredMethod> Unscored;          // no complexity source — listed, never gated
    IReadOnlyList<string> UnmatchedMetricsMembers;       // metrics members matching no coverage method
    CrapGateResult Evaluate(double maxCrap);             // at-threshold passes; NoData is not a pass
}

public readonly record struct CrapMethod(
    string Method, string File, int StartLine, int Complexity,
    double Coverage, double Score, CrapComplexitySource ComplexitySource);
public readonly record struct CrapUnscoredMethod(string Method, string File, string Reason);
public readonly record struct CrapGateResult(
    GateOutcome Outcome, double MaxCrap, int ScoredMethods,
    int AboveThreshold, double? WorstScore, string Reason);
public enum CrapComplexitySource { CoverageReport, MetricsFile }

// Microsoft.CodeAnalysis.Metrics XML (dotnet msbuild /t:Metrics) — complexity source
public static class CodeMetricsReader
{
    IReadOnlyList<CodeMetricsMember> Parse(Stream stream, long maxChars = 50_000_000);
    IReadOnlyList<CodeMetricsMember> ParseFile(string path, long maxChars = 50_000_000);
}
public readonly record struct CodeMetricsMember(
    string TypeName, string MemberName, CodeMetricsMemberKind Kind,
    int? Arity, int CyclomaticComplexity, string DisplayName);
public enum CodeMetricsMemberKind { Method, Accessor, Property, Field, Event }
```

## License

[MIT](LICENSE) — © Alexander Nachtmann.
