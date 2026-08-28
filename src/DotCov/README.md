[![Build](https://img.shields.io/github/actions/workflow/status/ANcpLua/dotcov/nuget-publish.yml?branch=main&style=flat-square&label=Build)](https://github.com/ANcpLua/dotcov/actions/workflows/nuget-publish.yml)
[![Coverage](https://img.shields.io/endpoint?url=https%3A%2F%2Fraw.githubusercontent.com%2FANcpLua%2Fdotcov%2Fbadges%2Fcoverage-badge.json&style=flat-square)](https://github.com/ANcpLua/dotcov/tree/badges)

# DotCov

Read Cobertura XML from your own code — parse, merge, threshold, and diff coverage reports.
Zero package references, Native-AOT clean, and streaming rather than DOM-loading.

## Getting started

```bash
dotnet add package DotCov
```

```csharp
using DotCov;
using DotCov.Formatters;

var report = CoberturaParser.ParsePath("TestResults/")   // file or directory
                            .Exclude(ExclusionRules.WellKnown);

Console.WriteLine(TableFormatter.Format(report));

var gate = report.Evaluate(minLinePercent: 80, minBranchPercent: 60);
if (!gate.IsPass)
{
    Console.Error.WriteLine(gate);   // "NODATA: line n/a (min 80%) - …"
    return 1;
}
```

`ParsePath` takes a file or a directory; a directory is globbed for
`**/coverage.cobertura.xml` and every match merged, so a sharded test matrix needs no merge step.

## Two things the API insists on

**Rates are `double?`.** `null` means *unanswerable* — not `0.0`, not `1.0`. An empty report has
no rate, and treating that as 0% would fail a build that simply measured nothing.

**A threshold check has four outcomes, not two.** `Evaluate` returns a `GateResult` whose
`Outcome` is `Pass`, `Fail`, `NoData`, or `Disabled`; `IsPass` covers `Pass` alone. Branch on the
structured flags (`LineBelowThreshold`, `BranchBelowThreshold`, `IsInconclusive`) rather than
parsing `Reason`.

## What else is in the box

```csharp
// Compare two reports — added / removed / modified, per-file deltas, line-level flips
var diff = CoverageDiff.Compare(
    CoberturaParser.ParseFile("before.xml"),
    CoberturaParser.ParseFile("after.xml"));

foreach (var r in diff.Regressions)
    Console.WriteLine($"{r.Path}: {r.Before:P1} → {r.After:P1}");

// Async streaming, cancellable
await using var stream = File.OpenRead("coverage.cobertura.xml");
var report = await CoberturaParser.ParseAsync(stream, ct: cancellationToken);

// Per-method detail — complexity and line hits, the input behind the CRAP gate
var methods = CoberturaParser.ParseMethodsPath("TestResults/");
```

`TableFormatter`, `MarkdownFormatter`, and `JsonFormatter` render a report for a terminal, a PR
comment, or a pipeline. Every numeric rendering is invariant-formatted: `62.0%` on every host,
never `62,0%`. `CoverageSnapshot` wraps a report with commit, branch, project, timestamp, and a
SHA-256 of the source file.

## Parsing and safety

`CoberturaParser` walks the document with `XmlReader` — no `XDocument.Load`, no full-DOM
allocation, bounded memory. DTDs are prohibited and `XmlResolver` is null, so there is no XXE or
entity-expansion surface, and each file is capped at 50,000,000 characters by default. Raise or
remove the cap with the `maxChars` overloads (`0` disables it).

The package has no `PackageReference`s at all and builds with the trim/AOT analyzers on and
warnings as errors, so AOT-cleanliness is enforced by the compiler rather than by convention.

Every public type carries XML docs — read them in IntelliSense or in
[the source](https://github.com/ANcpLua/dotcov/tree/main/src/DotCov).

## Also in this family

[DotCov.Tool](https://www.nuget.org/packages/DotCov.Tool/) — the `dotcov` CLI ·
[DotCov.Nuke](https://www.nuget.org/packages/DotCov.Nuke/) — NUKE build component

## Feedback

[Documentation](https://github.com/ANcpLua/dotcov#readme) ·
[Issues](https://github.com/ANcpLua/dotcov/issues) ·
[MIT](https://github.com/ANcpLua/dotcov/blob/main/LICENSE)
