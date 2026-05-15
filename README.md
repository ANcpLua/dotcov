[![NuGet — DotCov](https://img.shields.io/nuget/v/DotCov?label=DotCov&color=0891B2)](https://www.nuget.org/packages/DotCov/)
[![NuGet — DotCov.Tool](https://img.shields.io/nuget/v/DotCov.Tool?label=DotCov.Tool&color=0891B2)](https://www.nuget.org/packages/DotCov.Tool/)
[![NuGet — DotCov.Nuke](https://img.shields.io/nuget/v/DotCov.Nuke?label=DotCov.Nuke&color=0891B2)](https://www.nuget.org/packages/DotCov.Nuke/)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-7C3AED)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![CI](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&label=CI)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)
[![License](https://img.shields.io/github/license/ANcpLua/dotcov?label=License&color=white)](LICENSE)

# DotCov

Streaming Cobertura XML coverage toolkit — zero-dependency parser, `dotnet` global tool, and NUKE build extension. Handles 50 MB+ reports without loading the DOM.

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

# CI gate — exits 1 if below threshold, writes markdown to $GITHUB_STEP_SUMMARY
dotcov check TestResults/ --min-line 80 --exclude-generated --github-summary

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
if (!report.MeetsThreshold(minLinePercent: 80, minBranchPercent: 60))
    Environment.Exit(1);
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

- **Streaming `XmlReader`** — no `XDocument.Load`, no full-DOM allocation. Per-class cursor walks the document; safe on 50 MB+ files (configurable `maxChars`).
- **Hardened XML** — `DtdProcessing.Prohibit`, `XmlResolver = null`, character cap. No XXE / billion-laughs / external-entity surface.
- **Three output formats** — `table` (terminal), `json` (pipelines, snapshots, `--upload`), `markdown` (PR comments, `$GITHUB_STEP_SUMMARY`).
- **CI gating** — `check` and `ReportCoverage` both exit non-zero when line/branch coverage is below threshold, with the offending files listed in the failure output.
- **Coverage diffs** — added / removed / modified files, per-file delta, aggregate line-rate delta. Drop into PR comments to surface regressions.
- **Snapshots** — versioned JSON with commit SHA, branch, project, timestamp, SHA-256 file hash, and the full report. `--upload <url>` POSTs to any HTTP endpoint.
- **Exclusion rules** — `ExclusionRules.WellKnown` filters `.g.cs`, `.designer.cs`, `/obj/`, `/bin/`, `/Migrations/`, async state machines (`d__`), and `GlobalUsings`. Or pass your own substring patterns to `report.Exclude(...)`.
- **Native-AOT-friendly** — `DotCov` library has zero runtime package references.

## CLI Reference

```text
dotcov - Cobertura coverage toolkit

Commands:
  report   <path> [--format table|json|md] [--threshold N]      Parse and display coverage
  check    <path> --min-line N [--min-branch N]                 CI gate (exit 1 if below)
  diff     <before> <after> [--format table|json|md]            Compare two reports
  snapshot <path> --commit SHA --branch B --project P           Pipeline-ready JSON payload
  version                                                       Show version

Global flags:
  --exclude-generated       Skip generated files, migrations, state machines
  --upload <url>            POST JSON payload to any endpoint
  --github-summary          Write markdown to $GITHUB_STEP_SUMMARY

<path> can be a file or a directory. Directories are scanned for **/coverage.cobertura.xml.
```

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
    CoverageReport ParsePath(string path);   // dispatches on file vs. directory
}

public sealed class CoverageReport
{
    static readonly CoverageReport Empty;
    IReadOnlyList<FileCoverage> Files;
    IReadOnlyList<CoverageWarning> Warnings { get; init; }   // parser/merge anomalies
    double LineRate, BranchRate;
    double StrictLineRate;           // Codecov-style: partials and misses both depress the rate
    bool HasBranchData;
    bool MeetsThreshold(double minLinePercent, double minBranchPercent = 0);
    IEnumerable<FileCoverage> BelowPercent(double linePercent);
    CoverageReport Exclude(IEnumerable<string> patterns);
    CoverageReport Exclude(IEnumerable<string> patterns, IEnumerable<string> keep);
    static CoverageReport Merge(CoverageReport a, CoverageReport b);
}

public readonly record struct FileCoverage(
    string Path, int LinesHit, int LinesTotal, int BranchesHit, int BranchesTotal)
{
    double LineRate, BranchRate, StrictLineRate;
    bool HasBranchData;
    int StrictlyHitLines { get; init; }     // init-only — set by parser / MergeWith / factory
    int PartiallyHitLines { get; init; }    // init-only — set by parser / MergeWith / factory
    IReadOnlyList<int> UncoveredLines { get; init; }
    IReadOnlyList<BranchDetail> PartialBranches { get; init; }
    IReadOnlyDictionary<int, int> LineHits { get; init; }
    IReadOnlyDictionary<int, (int Covered, int Total)> BranchesByLine { get; init; }
    LineStatus GetLineStatus(int line);   // Hit / Partial / Miss
    bool TryGetLineStatus(int line, out LineStatus status);   // false = not tracked
    FileCoverage MergeWith(FileCoverage other);
    (FileCoverage Merged, IReadOnlyList<CoverageWarning> Warnings) MergeWithWarnings(FileCoverage other);

    // Hand-build a FileCoverage with strict/partial counts computed in one pass.
    // Use this instead of the direct constructor when you supply LineHits + BranchesByLine —
    // the direct constructor leaves StrictlyHitLines/PartiallyHitLines at 0.
    static FileCoverage WithComputedClassification(
        string path, int linesHit, int linesTotal, int branchesHit, int branchesTotal,
        IReadOnlyDictionary<int, int> lineHits,
        IReadOnlyDictionary<int, (int Covered, int Total)> branchesByLine,
        IReadOnlyList<int>? uncoveredLines = null,
        IReadOnlyList<BranchDetail>? partialBranches = null);
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
public readonly record struct LineDelta(int Line, int? BeforeHits, int? AfterHits, LineChangeKind Change);
public enum LineChangeKind { Added, Removed, NewlyHit, NewlyMissed }

public sealed record CoverageSnapshot(
    string CommitSha, string Branch, string Project,
    DateTimeOffset Timestamp, string? FileHash, CoverageReport Report);
```

## License

[MIT](LICENSE) — © Alexander Nachtmann.
