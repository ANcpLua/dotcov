# Changelog

Release entries are newest first; historical task entries retain their original order.

## 4.6.0 — 2026-10-15

- Verify every runtime and library package is publicly indexed, registered and downloadable before publishing the tool pointer.
- Reject wrong package IDs, versions and source commits; retain verification attempts and downloaded artifacts.
- Install the exact public native tool, compare its binary with the public package, and check real report/gate behavior before creating the GitHub Release.
- Bound each availability check to four minutes, restart with fresh requests when still pending, and fail after five windows.
- Preserve all existing tests, measurement scope, exclusions and 100% coverage gates; retain the 4.5.0 branch ownership fix.
- Full notes: [4.6.0](docs/releases/4.6.0.md).

## 4.5.0 — 2026-10-15

- Preserve package, class, method and signature ownership for branch observations through parsing and merging.
- Count distinct owners sharing a source line independently, while deduplicating repeated class summaries and repeated reports.
- Fix the captured async/lambda false pass: 4/6 branch outcomes now remain 4/6, instead of collapsing to 3/4.
- Expose scoped branch observations through `FileCoverage.BranchesByOrigin`; retain same-owner mismatch warnings and conservative merge state.
- Preserve the existing measurement scope, exclusions, 100% gates, CLI exit codes and JSON fields.
- Full notes: [4.5.0](docs/releases/4.5.0.md).

## 4.4.0 — 2026-10-15

- Migrate the full test suite to MSTest 4.5.1 on native Microsoft.Testing.Platform.
- Preserve all 1,999 existing test cases and add two scenario-provider contract tests plus a Windows workspace-cleanup regression.
- Use native assertions, data rows, combinations, resource locks, cooperative cancellation, and per-test upload fixtures.
- Remove the previous framework completely and update repository commands and guidance; retain the same Coverlet coverage scope and 100% gates.
- Full notes: [4.4.0](docs/releases/4.4.0.md).

## 4.3.2 — 2026-10-15

- Address JetBrains inspection findings in shipping code, build entry points, documentation, and tests.
- Reuse the upload HTTP client and dispose upload request content and responses.
- Materialize nullable signature inputs once and make asynchronous test resource disposal explicit.
- Preserve published APIs and intentional synchronous parser tests with narrowly documented inspection annotations.
- Full notes: [4.3.2](docs/releases/4.3.2.md).

## 4.3.1 — 2026-10-15

- Add the repository-hosted `dotcov` skill for coverage collection and result interpretation in consuming repositories.
- Refresh CI and release skills for the current workflows, release notes, package set, and published-tool verification.
- Update ANcpLua.Analyzers from 2.1.3 to 2.1.5; CLI behavior and coverage contracts are unchanged.
- Full notes: [4.3.1](docs/releases/4.3.1.md).

## Task 6 — 2026-05-15 — `TryGetLineStatus` sibling (additive)

Changed:
- `FileCoverage.TryGetLineStatus(int line, out LineStatus status)` — try-pattern sibling of
  `GetLineStatus`. Returns `false` (with `status = Miss`) for lines absent from `LineHits`;
  `true` (with the actual classification) when the line is tracked. Resolves the
  "tracked but zero hits" vs "not tracked at all" ambiguity that `GetLineStatus` collapses,
  without changing the iterate-any-range ergonomics callers rely on.
- `GetLineStatus` XML doc updated to drop the caveat (now resolved by the sibling) and point
  at `TryGetLineStatus` instead. `README.md` Public API surface block updated to list the
  new method on `FileCoverage`.

Verified:
- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` followed by
  `dotcov report TestResults/ --exclude-generated` reports **Lines 575/575 (100%)** /
  **Branches 288/288 (100%)** across 201 tests (+4 over Task 3). `CoverageReport.cs` itself
  is 120/120 lines and 56/56 branches.

Notes:
- Picked up the deferred item from Task 3's "explicitly skipped" list — a subsequent code
  review classified the unknown-line conflation as a HIGH severity API trap, so the
  additive try-pattern landing here.
## Task 7 — 2026-05-15 — Strict/Partial classification cached at construction (single-pass)

Closes the punch-list item from Task 3: `FileCoverage.StrictlyHitLines` and `PartiallyHitLines`
were getters that each re-walked `LineHits.Keys` invoking `GetLineStatus(line)` per entry, and
`CoverageReport.StrictLineRate` did a third walk via `f.StrictlyHitLines` per file. Three
independent O(n) passes over the same dict — wasted CPU on 50 MB+ reports where every walk
costs millions of lookups. Replaced with a single classification pass at construction time.
Breaking change to the public surface: both properties are now init-only (user authorized
breaking changes for this task).

Changed:
- `FileCoverage.StrictlyHitLines` / `PartiallyHitLines` flipped from computed getters to
  `{ get; init; }`. XML docs spell out: "init-only; use `WithComputedClassification` factory
  or set explicitly when hand-building" — a default of `0` will silently disagree with the
  keyspace of `LineHits` otherwise.
- New `FileCoverage.ClassifyLines(lineHits, branchesByLine)` internal helper performs the
  single pass that bins each executed line into the strict or partial bucket — same logic
  the old getters used, but called once instead of three times.
- New public `FileCoverage.WithComputedClassification(path, ..., lineHits, branchesByLine,
  uncoveredLines = null, partialBranches = null)` factory. Wraps the direct constructor with
  a single `ClassifyLines` call so hand-built records stay self-consistent — the only safe
  way for in-tree callers to build a `FileCoverage` from raw line/branch dicts now.
- `CoberturaParser.Materialize` and `FileCoverage.MergeWith` both call `ClassifyLines` once
  after assembling the merged dicts, then pass the counts into the init block.
- `CoverageReport.StrictLineRate` collapses to a one-liner that sums the precomputed per-file
  `StrictlyHitLines` instead of re-walking every file's `LineHits` dict.
- Dropped the optional `ComputeIfMissing` fallback the requirements floated — every in-tree
  test now uses the factory, so a fallback would be dead code. Future external consumers who
  build `FileCoverage` records by hand get the documented footgun in the XML doc and a
  consistent counts-mismatch failure rather than silently-correct-by-accident behavior.

Tests:
- Updated every `new FileCoverage(...) { LineHits = ..., BranchesByLine = ... }` site in
  `ExclusionAndReportTests.cs` and `CoberturaParserTests.cs` to call
  `FileCoverage.WithComputedClassification(...)`. Tests in other files (`TableFormatter`,
  `JsonFormatter`, `MarkdownFormatter`, `CoverageDiff`, `Reports` infra) don't read the
  strict counts, so they were left untouched.
- Added `ClassifyLines_SinglePass_MatchesPreviousGetterLogic` — a `[Theory]` over seven
  fixtures (mixed, all-hit-no-branches, all-partial, all-missed, empty, branch-Total-equals-
  Covered, branch-on-missed-line). Each row asserts both the precomputed `StrictlyHitLines`/
  `PartiallyHitLines` AND the count derived by re-running `GetLineStatus` over
  `LineHits.Keys` — the body of the original getter — so a regression in either path would
  surface immediately.
- Added `MergeWith_PrecomputedCountsMatchClassifyLines` — merges two files with overlapping
  + disjoint branched lines, then asserts the merged struct's cached counts equal what a
  fresh `WithComputedClassification` call over the merged dicts would produce (the "did
  MergeWith do the same work the factory would have done?" round-trip).
- Added `WithComputedClassification_OmitsOptionalLists_DefaultsToEmpty` — pins the optional
  `uncoveredLines`/`partialBranches` defaulting to empty collections, matching the direct
  constructor's field initialisers.

Verified:
- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` →
  **206/206 passing** (test count grew by 3 over Task 3's 203).
- `dotcov report tests/DotCov.Tests/TestResults --exclude-generated` reports
  **Lines 580/580 (100%)** / **Branches 288/288 (100%)** across the suite, with
  `CoverageReport.cs` at **122/122 lines / 56/56 branches** and `CoberturaParser.cs` at
  **119/119 lines / 56/56 branches** — both files clean at 100% per the acceptance criteria.

Notes:
- README's "Public API surface" block updated: `StrictlyHitLines`/`PartiallyHitLines` are
  flagged init-only and the `WithComputedClassification` factory signature is added.

## Task 8 — 2026-05-16 — Eliminate three deferred trade-offs from Tasks 4–7

The four parallel-agent merges (Tasks 4-7) closed the original review punch-list but each
left one documented compromise (`[Δ]` markers in the integration write-up). User authorised
breaking changes, so this task removes all three. Result: smaller and stricter public
surface, all invariants compile-time-enforced.

Changed:

- **Δ#1 — `LineDelta` exhaustiveness becomes compile-time.** Added two abstract visitor
  methods on the closed hierarchy: `T Match<T>(Func<Added,T>, Func<Removed,T>,
  Func<NewlyHit,T>, Func<NewlyMissed,T>)` for value-returning dispatch and
  `void Switch(Action<Added>, Action<Removed>, Action<NewlyHit>, Action<NewlyMissed>)` for
  side-effecting consumers. Each variant overrides both. A fifth variant requires
  extending both signatures, which breaks every callsite — the actual sum-type
  guarantee. Replaced the `is`-pattern chains in `JsonFormatter.FormatLineDelta` (now
  `c.Match<object>(…)`) and `MarkdownFormatter.AppendIndirectChanges` (now
  `c.Switch(…)`); the former unguarded `(LineDelta.NewlyMissed)c` cast and the
  `is`-chain rationale comments are gone.
- **Δ#2 — `MergeWithWarnings` renamed to `MergeWith`, lossy overload deleted.** The
  tuple-returning `(FileCoverage Merged, IReadOnlyList<CoverageWarning> Warnings)`
  signature is now the only `MergeWith` on `FileCoverage`. Callers who don't care about
  warnings discard with `var (merged, _) = a.MergeWith(b)`; the divergence-is-observable
  contract can no longer be silently bypassed by picking the wrong overload.
  `CoverageReport.Merge` updated to call the renamed method.
- **Δ#3 — `WithComputedClassification` factory deleted from public API.**
  `FileCoverage.ClassifyLines(...)` is now `public` (was `internal`) so external
  hand-builders compute the strict / partial counts inline:
  `var (s,p) = FileCoverage.ClassifyLines(hits, branches); new FileCoverage(...) { …,
  StrictlyHitLines = s, PartiallyHitLines = p }`. Test fixtures get a private
  `Reports.ClassifiedFile(...)` helper in `tests/DotCov.Tests/Infrastructure/` that
  encodes the same two-step dance — kept the tests readable without re-introducing the
  factory on the library.

Tests:
- Two regression tests deleted as redundant: `WithComputedClassification_OmitsOptional
  Lists_DefaultsToEmpty` (factory is gone — would now test `Reports.ClassifiedFile`,
  which is test infrastructure) and `MergeWith_LegacyConvenience_DropsWarningsBut
  PreservesMergedCounts` (parity check between two methods that are now the same one).
- All `MergeWith(b)` callsites that previously assigned to a `FileCoverage` updated to
  `var (merged, _) = a.MergeWith(b)` deconstruction. All `MergeWithWarnings_…` test
  method names renamed to `MergeWith_…`.
- All `FileCoverage.WithComputedClassification(...)` callsites in the test project
  switched to `Reports.ClassifiedFile(...)`. `Reports.ClassifiedFile` accepts
  `IReadOnlyDictionary<…>` (not just `Dictionary<…>`) so callers can pass the dicts
  out of an existing `FileCoverage` directly without re-materialising.

Verified:
- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` —
  **232/232 passing** (test count dropped by 2 from Task 7 because the two redundant
  tests above were deleted, no functionality was lost).
- `dotcov report tests/DotCov.Tests/TestResults --exclude-generated` reports
  **Lines 645/645 (100%)** / **Branches 306/306 (100%)** — `CoverageReport.cs` at
  142/142 lines + 56/56 branches (lost ~25 lines from the deleted factory and lossy
  overload), `CoverageDiff.cs` at 68/68 lines + 10/10 branches (gained 8 lines from
  the abstract Match/Switch + four overrides each), the three `Format*.cs` files all
  100%/100%.

Notes:
- `Match<T>` for value-returning dispatch + `Switch` for fold-style side effects is
  intentional duplication: `Match<T>` can't return `void`, and JSON consumers want a
  return value while Markdown consumers want a counter increment. Both abstract methods
  pull weight in formatter code, both are 100%-covered by the existing diff tests.
- `ClassifyLines` is now `public` rather than wrapped behind a factory because the
  factory was strictly less expressive: it forced a fixed parameter order, hid the
  manual init-block from external readers, and added one more name to the public
  surface to maintain. The two-line idiom (call ClassifyLines, init the struct) reads
  the same in both library and consumer code.
- The previous `[Δ]` markers from the Task 4/5/7 integration report are now resolved
  in code rather than documented as known limitations.

## Task 9 — 2026-05-16 — Line-diff filtering before stable ordering

Changed:
- Reworked `CoverageDiff.ComputeLineChanges` to scan `before.LineHits` and `after.LineHits`
  directly, emit only real added/removed/hit-state-flip deltas, then sort the emitted
  changes by line number for stable output.
- Added a regression test that verifies mixed added, removed, newly-hit, and newly-missed
  line changes still render in ascending line order after unchanged lines are filtered out.

Verified:
- `dotnet test tests/DotCov.Tests/DotCov.Tests.csproj --filter FullyQualifiedName~CoverageDiffTests`
  — 17 passed.
- `dotnet test DotCov.slnx` — 233 passed under SDK 10.0.300.

Notes:
- Addresses the review finding that the old implementation sorted the full before/after
  line-number union before discarding unchanged lines; large mostly-stable reports now avoid
  that per-file O(n log n) sort and only pay sorting cost for emitted changes.

## Task 10 — 2026-05-16 — GitHub Actions SDK resolution from `global.json`

Changed:
- Updated the CI build and test jobs to install .NET from `global.json` via
  `actions/setup-dotnet@v5`'s `global-json-file` input instead of floating on
  `dotnet-version: 10.0.x` plus `dotnet-quality: preview`.

Verified:
- `ruby -e 'require "yaml"; YAML.load_file(".github/workflows/nuget-publish.yml")'` —
  workflow YAML parses.
- `dotnet test DotCov.slnx` — 233 passed under SDK 10.0.300.

Notes:
- Root cause was Windows CI resolving `10.0.100-rc.2...` from the floating preview channel
  while the repository now requires SDK `10.0.300`; `dotnet test` then failed before
  running tests.
- Trusted publishing itself already uses the required OIDC pieces: `id-token: write`,
  `environment: nuget`, `NuGet/login@v1`, and `dotnet nuget push` with the short-lived key.

## Task 11 — 2026-05-16 — Update Coverlet collector to 10.0.0

Changed:
- Bumped the central `coverlet.collector` package version from 8.0.1 to 10.0.0.
- Applied CA1859 narrowly by changing the private `ComputeLineChanges` helper to return
  `List<LineDelta>` while keeping the public `FileDelta.LineChanges` surface as
  `IReadOnlyList<LineDelta>`.
- Removed a stale unused `using DotCov;` from the Nuke component.

Verified:
- `dotnet test DotCov.slnx` — 233 passed.
- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` —
  233 passed and produced Cobertura output with Coverlet 10.
- `dotnet run --project src/DotCov.Tool/DotCov.Tool.csproj -- report tests/DotCov.Tests/TestResults --exclude-generated`
  — total coverage remained **669/669 lines (100%)** and **324/324 branches (100%)**.

Notes:
- This was not the root cause of the earlier GitHub Actions failure; CI failed because the
  workflow installed a floating preview SDK while `global.json` required exact SDK 10.0.300.
- The upgrade is still correct because Coverlet 10.0.0 targets .NET 8.0 and is compatible
  with the repo's net10 test project.

## Task 12 — 2026-05-18 — Codecov badge + coverage upload from CI

Changed:
- `nuget-publish.yml` test job now collects Cobertura via
  `--collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory TestResults`,
  so the existing `coverlet.runsettings` (previously unreferenced) is finally honored under
  both ubuntu and windows matrix legs.
- Added a `codecov/codecov-action@v5` step that uploads `TestResults/**/coverage.cobertura.xml`,
  flagged by matrix OS so Codecov unions the two runs instead of double-counting.
  `fail_ci_if_error: false` and `token: ${{ secrets.CODECOV_TOKEN }}` keep CI green if the
  token is unset or Codecov is down.
- README gains a Codecov badge between CI and License, matching the existing shields.io row.

Verified:
- Local `dotnet test DotCov.slnx -c Release --collect:"XPlat Code Coverage" --settings coverlet.runsettings --results-directory TestResults`
  passes 233 tests and lands `TestResults/<guid>/coverage.cobertura.xml`, which the workflow's
  glob matches.
- `dotcov report TestResults --exclude-generated` self-reports **552/552 lines (100%)** /
  **316/316 branches (100%)** on the tested library surface — the number the badge will display
  after the first successful upload.
- `python3 -c "import yaml; yaml.safe_load(...)"` accepts the modified workflow.

Notes:
- Requires a `CODECOV_TOKEN` repo secret. Until that secret is set on
  `github.com/ANcpLua/dotcov`, uploads no-op and the badge stays at "unknown" while CI still
  passes.

## Task 13 — 2026-07-18 — Per-package README embedded in every nupkg

nuget.org showed a "Your package is missing a README" banner on all three package pages and
rendered only the `<Description>` block as body text. The root `README.md` was never packed —
no project set `PackageReadmeFile`, so nothing shipped inside the nupkg.

Changed:
- New minimal `README.md` next to each shipping csproj (`src/DotCov`, `src/DotCov.Tool`,
  `src/DotCov.Nuke`) — install command, the commands themselves, and absolute links back to
  the repo, the sibling packages, and the licence. Deliberately not a copy of the root README:
  nuget.org is an install surface, so each page carries only what you type, and links out for
  the rest. Relative links are inert on nuget.org, hence absolute URLs throughout.
- `Directory.Build.props` sets `PackageReadmeFile` under an
  `Exists('$(MSBuildProjectDirectory)/README.md')` guard, so the convention self-limits to the
  three shipping projects and no-ops for `DotCov.Tests`.
- New `Directory.Build.targets` attaches `Pack="true" PackagePath="/"` via `None Update`.
  `Update` rather than `Include`, and in `.targets` rather than `.props`: `Directory.Build.props`
  is imported before the SDK's default item globs exist, so `Update` there would be a no-op,
  while `Include` would add a second `None` for a file `EnableDefaultNoneItems` already globbed
  in — NETSDK1022.
- Root `README.md` left untouched; it stays the GitHub landing page.

Verified:
- All five pack shapes carry `README.md` at the package root with `<readme>README.md</readme>`
  in the nuspec: `DotCov`, `DotCov.Nuke`, the `DotCov.Tool` pointer, the `any` CoreCLR
  fallback (`-r any -p:PublishAot=false`), and a RID-specific Native AOT build
  (`-r osx-arm64`) — the shape CI's `fail-fast: true` `pack-tool-native` job builds.
- No NETSDK1022 (duplicate `None`) and no NU5039 (readme not found) on any pack.
- `dotnet test DotCov.slnx -c Release` passes 240/240.
- All five outbound links return HTTP 200.

Notes:
- Docs-only; no API or behaviour change. Released as v0.3.1 so nuget.org re-renders — the
  banner clears on the new version only, 0.3.0's page keeps it permanently.

## Task 14 — 2026-07-18 — Honest core: a rate that cannot be computed is null, not 1.0

`LineRate`/`BranchRate`/`StrictLineRate` returned `1.0` when their denominator was zero. An empty
report — a glob that matched nothing, a test step that emitted no Cobertura, a search directory
pointing somewhere stale — therefore reported **100% line and 100% branch and cleared every
threshold**. Same for a branch threshold checked against a report carrying no branch data.
Reproduced against 0.3.1 before the change:

    check <empty dir>   --min-line 95 --min-branch 75  ->  PASS: line 100.0%, branch 100.0%   exit 0
    check <line-only>   --min-line 50 --min-branch 75  ->  PASS: branch 100.0% >= 75%         exit 0

Six existing tests asserted exactly this (`..._ReturnsOne`, `..._HasNoFilesAndPerfectRates`,
`Parse_NoBranches_ReportsFullBranchRate`). The behaviour was specified, not overlooked — which is
why a 240-test suite at 100% self-coverage stayed green over it. Coverage cannot catch a wrong
specification; only reproducing the claim against real input can.

Changed (pure core only — no side-effecting behaviour was redesigned):
- Rates are `double?` across `CoverageReport` and `FileCoverage`. Null means unanswerable, and is
  distinct from `0.0` (measured, nothing hit). Added `HasLineData` alongside `HasBranchData`.
- `MeetsThreshold` → `Evaluate`, returning `GateResult` with a four-state `GateOutcome`:
  `Pass` / `Fail` / `NoData` / `Disabled`. A bool collapsed "too low", "nothing measured", and
  "no threshold set" into one `false`, though CI owes each a different response.
- `Disabled` reports thresholds of 0/0 explicitly: a gate that cannot fail is indistinguishable
  from a passing one from the outside, which is how `--coverage-min-line 0` survives in a pipeline
  looking like enforcement.
- `BelowPercent` no longer lists unmeasured files as offenders — they are not "below" anything.
- `ExclusionRules.TestCode`, separate from `WellKnown` because excluding test code changes the
  number rather than corrects it. Includes `TestSupport` spellings: a `.Tests` rule does not match
  a `TestSupport` assembly, and that gap silently counts shared fixtures as product code.
- Formatters render unmeasured as `-` / `n/a` / omitted JSON keys, never as a number. `AnsiPen.Rate`
  takes `double?` so no colour verdict is asserted over absent data. Markdown gains a `⚠️` badge and
  a "No verdict" note for inconclusive gates, instead of showing ✅.
- `CoverageDiffResult.Delta` and `FileDelta.Delta` are `double?`: a diff against an empty report is
  not a 100-point regression, it is not a comparison.

Deliberately NOT changed — these are effectful decisions, left for the shell pass:
- exit codes: `NoData`/`Disabled` currently exit 1 (conservative: a gate that cannot verify must not
  vouch). Whether they warrant a distinct code is a CLI contract change affecting every consumer.
- `ParseDirectory` on zero matches still returns `Empty` rather than throwing.
- The NUKE component still fails the build on inconclusive outcomes rather than warning.
Both call sites carry a POLICY comment marking the open question.

Verified:
- 252/252 tests pass (240 before; 12 added, 6 rewritten from asserting the old behaviour).
- Rebuilt CLI against the original reproductions: empty dir → `NODATA ... exit 1`; line-only with
  `--min-branch 75` → `NODATA ... exit 1`; `0/0` → `DISABLED ... exit 1`; genuine pass → exit 0;
  genuine fail → exit 1.

Breaking: `double` → `double?` on every rate, `MeetsThreshold` removed. Next release is 0.4.0.

## Task 15 — 2026-07-30 — Invariant output, structured gate verdicts, prose pinned once

Changed:
- All human-readable numeric rendering (`GateResult.ToString`, `TableFormatter`,
  `MarkdownFormatter`, the CLI's below-threshold listing, and the NoData branch-threshold
  `Reason`) now formats with `FormattableString.Invariant`. CI logs, PR summaries, and
  scripts parse these strings; a de-AT host must not turn `62.0%` into `62,0%`. Pinned by
  `ToString_IsCultureInvariant`, which runs the render under `de-AT`.
- New structured verdict properties `GateResult.LineBelowThreshold` /
  `BranchBelowThreshold` (same comparisons `Evaluate` uses). Tests and consumers branch on
  these instead of parsing `Reason` prose.
- `Reason` wording is asserted in exactly one golden test
  (`Reason_CanonicalProse_IsPinnedHereOnly`); all other tests assert structure, so a
  rewording is a one-test change instead of a scatter of false failures.
- `GateResultTests.Report` helper lost its `bHit = 0, bTotal = 0` defaults — branch data
  presence decides Pass/Fail vs NoData, so it is now explicit at every call site.

256/256 green; self-measured 99.4% line / 97.1% branch, `GateResult.cs` at 100/100.

## Task 16 — 2026-08-21 — CRAP gate: `dotcov crap`, method-level parse, Metrics reader

Added (all additive — no existing public behavior or merge semantics changed):
- `CoberturaParser.ParseMethods/-File/-Directory/-Path`: opt-in per-`<method>` parse
  returning raw `MethodCoverage` records (class/method/signature, file key resolved through
  the same `<source>`-root arithmetic as `FileCoverage.Path`, line range, per-line hits with
  the same union-with-max merge, and coverlet's per-method `complexity` when usable —
  cyclomatic complexity is ≥ 1 by construction, so gcovr/grcov's placeholder `0`/`0.0` and
  ReportGenerator's `NaN` surface as null, never as a measurement).
- `CodeMetricsReader`: streaming reader for the Microsoft.CodeAnalysis.Metrics XML
  (`dotnet msbuild /t:Metrics`); shape verified against the writer in dotnet/roslyn
  (`MetricsOutputWriter.cs`) and real generated reports. Normalizes MinimallyQualified
  display strings to coverage spellings (`.ctor`, `get_X`, `op_Equality`, `Item`, generic
  stripping) with type identity taken from the element nesting, never parsed out of the
  display string.
- `CrapAnalysis` + `CrapReport`: CRAP(m) = comp²·(1−cov)³+comp per method, with the
  compiler-mangling fold table (`<M>d__N`+`MoveNext`, `<>c…`+`<M>b__…`, `<M>g__…`,
  arity ticks) in one place (`MethodIdentity`). Embedded coverlet complexity wins;
  `--metrics` fills gaps; overloads disambiguate by arity. Unscored methods and unmatched
  metrics members are carried on the report — listed in every format, never dropped.
- `dotcov crap <path> [--metrics <file>] [--max-crap N] [--top N] [--format table|json|md]
  [--github-summary]`: worst-first table; exit 0 all at-or-under, 1 any strictly above
  (at-threshold passes via the same `GateResult.RateEpsilon` policy) or NODATA; summary
  written on pass AND fail from the same gate as the exit code. `--exclude-generated`
  shares the one exclusion predicate (`ExclusionRules.Excluded`) with report/check.

Verified: 630/630 green (84 new tests: hand-computed scores incl. the comp-2-cov-0 = 6
boundary, normalization table, metrics-shape pins, CLI exit-code matrix, comma-decimal
culture invariance), `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` green, 0 warnings with
trim/AOT analyzers, and an osx-arm64 Native-AOT publish runs `crap` end-to-end on the
corpus fixtures.

## Task 17 — 2026-08-21 — CRAP fix round: nested mangled names, same-arity overloads

Fixed (validator findings against Task 16, all reproduced on real coverlet output):

- **Async-lambda state machines were silently dropped** (major). Roslyn nests mangled
  names — an async lambda compiles to `Type/<>c/<<M>b__0_0>d` + `MoveNext` — and
  `MethodIdentity`'s `IndexOf('>')` parses found the INNER close bracket, so the segment
  parsed as neither state machine nor mangled member and the entry vanished from every
  channel, turning the gate falsely green. All bracket parsing is now depth-tracked to the
  matching outer close, the synthetic-container scan continues past `<>c`/`<>c__DisplayClass…`
  to find state machines nested inside, and bracketed origins re-normalize recursively.
  The same root cause fixed top-level-statements (`Program/<<Main>$>d__0` and sync
  `<Main>$` → `Program.Main`; `CodeMetricsReader` normalizes the metrics-side `<Main>$`
  display to `Main` so the two match) and async/iterator local functions
  (`<<M>g__Local|0_0>d` → `M`).
- **Same-arity overloads silently merged into one logical method** (major). Logical methods
  were keyed by parameter COUNT, so `Frob(Int32)` and `Frob(String)` collapsed into one
  row and an uncovered comp-20 overload's CRAP 420 was diluted below threshold by its
  covered sibling's lines. Direct entries now key by the raw IL signature; folded groups
  (which have no signature) key by name and, as before, are absorbed only by an
  UNAMBIGUOUS origin — a same-arity overload set is now correctly ambiguous, so the
  folded state machine stands as its own row instead of merging.

640/640 green (10 new regression tests: nested-mangled fold table incl. capturing display
classes and infrastructure drops, top-level statements on both sides of the metrics match,
same-arity dilution, ambiguous-origin standalone rows), `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
green, Debug+Release builds 0 warnings, validator repros re-run: real-coverlet async-lambda
probe now FAILs at CRAP 42.0 (was falsely PASS), dilute fixture FAILs at 420.0, ambiguous
`<Run>d__2` stands alone at 30.0, real top-level coverage rows read `Program.Main`.

## Task 18 — 2026-08-21 — Dogfood round: the CRAP gate applied to its own implementation

Ran the gate on this repo (`dotnet test --collect:"XPlat Code Coverage"` → `dotcov crap`)
and refactored until no method introduced by the CRAP feature ranks in the repo's top-10
worst and every one of them is fully covered. No gate semantics, thresholds, or output
contracts changed; one real parser bug fell out of writing the table tests.

Fixed:

- **Comparison/shift operators never reached the `op_*` table** (real bug, found while
  pinning the operator mapping). `operator <`, `>`, `<=`, `>=`, `<<`, `>>`, `>>>` carry
  unbalanced angle brackets, which derailed the depth-tracked parameter-list split — the
  token parsed as `<(Calculator a, Calculator b)` and fell to the raw-spelling arm, so
  those members could never match their coverage rows. Operator displays are now located
  by their `.operator ` marker (no operator token contains a parenthesis); the full token
  table is pinned by tests through real MinimallyQualifiedFormat display strings.

Refactored (design, not gymnastics — before → after CRAP on this repo's own report):

- `CodeMetricsReader.OperatorMethodName` 3369.3 (comp 78, cov 18.5%) → 14.0: the token →
  `op_*` mapping is mechanical, so it is now a `FrozenDictionary` table with only the
  arity disambiguation of unary/binary `+`/`-` left as code; accessor suffix → prefix
  (`get`/`set`/`init`/`add`/`remove`) got the same table treatment.
- `CodeMetricsReader.ParseCore` 76.0 (comp 76) → 6.0: split into `OnElementStart`/
  `OnElementEnd` scope handlers with `OpenMember`/`CloseMember`/`RecordComplexity`, and
  the member-element name test shared via `IsMemberElement` (it appears on both sides).
- `CrapAnalysis.Analyze` 60.0 (comp 60) → 1.0: split along its already-numbered seams —
  `FoldRawEntries`, `FoldSyntheticGroups`, `ScoreMethods`, `CollectUnmatched`; line-stat
  and complexity-merge arithmetic moved onto `LogicalMethod`.
- `CodeMetricsReader.ParseDisplay` 52.2 (comp 46, cov 85.7%) → 6.0: element-kind dispatch
  stays; `ParseMethodDisplay`, `TryAccessorName`, `TryOperatorName`, `PropertyName` each
  own one rule.
- `CrapFormatter.FormatMarkdown` 60.9 (comp 24, cov 60%) → 1.0: header/table/honesty-
  trailer sections extracted, mirroring the terminal formatter's existing shape.
- `CoberturaParser.ConsumeClassMethods` 38.0 (comp 38) → per-method/per-line loops split
  into `ConsumeMethod` + `ConsumeMethodLine` (18.0 worst).
- New `BalancedText` (internal): the one depth-tracked "at top level" scanner
  (`IndexOfTopLevel`/`LastIndexOfTopLevel`/`CountTopLevelItems`, one `DepthDelta` rule) —
  replaces four near-identical loops in `MethodIdentity.SignatureArity` and
  `CodeMetricsReader` (`SplitParameterList`/`CountParameters`/`FindTopLevelSpace`/
  `LastDotSegment`).
- `DotCovCli.Crap` 28.0 → 14.0: `--top` parsing, `--metrics` loading, and the format
  dispatch extracted (`TryGetTop`/`LoadMetrics`/`RenderCrap`), matching the other
  commands' helper shape.

Noted, not split: `ConsumeMethod` (comp 18) and `FindTopLevel` (comp 18) are each one
coherent loop at 100% coverage — further cuts would read worse than the originals.

679/679 green (39 new test cases: the full operator-token table incl. the checked-operator
raw-spelling arm, field/event members, init/add/remove accessors, explicit conversions,
markdown NODATA/truncation/honesty sections, method-level malformed-line policy,
path-prefixed rethrows for the ParseMethods family, CLI directory dispatch + pattern-gate
error, unparenthesized-signature arity, unbalanced-mangled-name tolerance). All 86
feature methods now score ≤ 18.0 at 100% line coverage — the repo's top-10 worst is
pre-existing code only. `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` green, 0 warnings.

## Task 19 — 2026-09-18 — Native MTP tests, ReportResolver, structured parse errors, DotCov.Fallout

Changed:
- Test project moved to native Microsoft.Testing.Platform
  (`global.json` test runner). Coverage now comes from `coverlet.MTP` (`dotnet test --coverlet
  --coverlet-output-format cobertura --coverlet-file-prefix dotcov --coverlet-include "[DotCov*]*"
  --coverlet-exclude-by-attribute … --coverlet-exclude-by-file "**/obj/**/*.cs"`); the file is
  `TestResults/dotcov.coverage.cobertura.<timestamp>.xml` and the badge step selects it with
  `--pattern`. `coverlet.runsettings` is gone.
- Input resolution lives in `ReportResolver` (file, directory, `ReportPattern`; hidden directories
  searched; missing paths throw; no matches → empty set) producing `ReportInput`s.
  `CoberturaParser.Parse(ReportInput…)`/`ParseMethods(ReportInput…)` own their streams; the
  `ParseFile`/`ParseDirectory`/`ParsePath` and `ParseMethods*` path overloads are removed.
- `ParseMethods` returns `MethodCoverageReport` (`Methods`, `Warnings`, `SourceRoots`); the
  method aggregation lives in an internal `MethodCollector` keyed by `MethodKey`. Malformed
  input surfaces as `ReportParseException` (source name, line, position, inner `XmlException`);
  the regex rethrow is gone and messages are rendered at the CLI/build boundary. `dotcov crap`
  prints every parser warning to stderr.
- `CrapAnalysis`: a metrics member that matches a method counts as matched even when the
  embedded complexity wins; `UnmatchedMetricsMembers` lists only members without a coverage
  counterpart.
- `CoverageDiff`: `FileDelta` derives `Delta`, `Change`, `IsRegression`, `IsImprovement` from the
  change kind and rates (factories `Removed`/`Added`/`Compared`). A removed measured file is a
  regression whatever its rate (delta is positive zero, never `+-0.0%`); an added 0% file is
  neither; unmeasured (`null`) never counts as movement; `MovementEpsilon` still gates both-sided
  files.
- `DotCov.Nuke` is replaced by `DotCov.Fallout` (Fallout.Common/Components 10.4.0,
  `ICoverageReport : IFalloutBuild`). Parameters are validated once (`CoverageParameters`), the
  gate policy is explicit (only `Pass` succeeds; `Fail`/`NoData`/`Disabled` fail distinctly),
  warnings are logged in full, markdown is rendered once for terminal and step summary, and an
  unwritable summary only warns. `CoverageReportHelpers` and the `NuGetAuditMode=direct`
  exceptions are gone; the full transitive audit is clean.

Verified:
- 752/752 tests (679 baseline cases migrated, forwarding tests replaced by
  `ReportPatternTests`, `ReportResolverTests`, `ParserContractTests`, `FalloutBuildTests` — the
  latter run `tests/DotCov.Fallout.TestBuild` as a real Fallout process). Also green under
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.
- `dotcov report TestResults --pattern "**/dotcov.coverage.cobertura.*.xml" --format json` on the
  coverlet.MTP output: 27 files, line 99.08%, branch 94.41% → badge `99.1%` brightgreen.

## Task 20 — 2026-10-13 — 3.0.0: explicit thresholds, complete CRAP scoring, deterministic file identity

Three gate contracts change and seven counting bugs are fixed, so every number a gate decides
on may move. Release notes: `docs/releases/3.0.0.md`.

Changed (contracts):
- Thresholds are explicit. The `"80"` defaults are gone from `check`, `test`
  (`TryGetThresholds`), the Fallout component (`CoverageParameters.DefaultMinLine`/
  `DefaultMinBranch` removed; `ICoverageReport.MinLine`/`MinBranch` are `string?`) and the badge
  action (`min-line`/`min-branch` default empty; `badge.sh` passes only what was set). Neither
  threshold → usage error before any report is read or `dotnet test` starts (Fallout: build
  error naming both parameters); one → the other is disabled; both 0 → `Disabled`; outside
  0–100, NaN or infinity → exit 2. `report --threshold` and the repo wrapper's badge threshold
  parse follow the same range; `--max-crap` keeps its own grammar (infinity disables).
- `CrapReport.Evaluate` needs complete scoring: any `Unscored` method → `NoData` naming the
  count and `--metrics`; zero scored stays `NoData`; otherwise `Fail` above the limit, `Pass`.
- File identity is lexical by default (`FileIdentity.Lexical`): several roots and a relative
  filename → first-root key, one `FileIdentityAmbiguous` warning per file naming the candidate
  roots, `AmbiguousFiles` on `CoverageReport`/`MethodCoverageReport`/`CrapReport`, and `NoData`
  from `CoverageReport.Evaluate` and `CrapReport.Evaluate`. 2.0's disk probe is opt-in
  (`FileIdentity.ProbeSources`, `--probe-sources`, `--coverage-probe-sources-param`); an
  explicit root (`FileIdentity.FromSourceRoot`, `--source-root`, `--coverage-source-root`) must
  be declared, else `UndeclaredSourceRootException` → exit 2. The mode is a parser parameter
  (`IdentityScope` per call), never process state; `ReportResolver` resolves report files only
  and needs no mode.
- `--exclude-test-code` (`report`, `check`, `crap`, `snapshot`, `test`) and
  `ExcludeTestCodeParam` select `ExclusionRules.TestCode` through `ExclusionRules.Excluded`;
  `--keep` exempts from either exclusion. `CoverageReport.Exclude` and the new
  `MethodCoverageReport.Exclude` (replacing `CrapAnalysis.ExcludeFiles`) record a
  `FileExclusion`; stderr lists rules and files for table/md output, JSON carries `excluded`
  and `exclusionRules`, the Fallout target logs them.

Fixed (numbers):
- `ParseAsync` finishes method-only classes through the same `ClassContext.Complete` as the
  sync walk (A1).
- CRAP rows key by file (A2) and synthesized material by its origin's method ordinal (A3,
  `SyntheticOrigin`); a folded group attaches only to the one same-named method of its file and
  type, and a method's own state machine never joins an entry with lines.
- Condition sets that differ between class blocks of one document poison the line like a merge
  does (A4); per-`<line>` detail must reproduce its own covered count, and `branch="true"`
  without `condition-coverage` or an unparseable condition warns (A5). Detail that is not 2-way
  shaped (switch tables, grcov's per-outcome counts) is left out silently, as before.
- An empty class-level `<lines>` element still decides the file's lines (A6).
- `PathIdentity.RemoveDotSegments` normalizes `./`, `/./` and `/.` in file names and roots alike;
  `..` is kept (A7).

Also: `dotcov --version` prints three components (`3.0.0`); `RejectUnusedArguments` split into
`UnknownOption`/`RejectInvalidValues` so the repo's own CRAP gate stays at or below 30; CI's
consumer and README-badge jobs pass `--probe-sources` (itsdangerous declares two roots);
`tests/AGENTS.md` documents the native runner.

Verified:
- `fallout Coverage`: 100% lines and branches; full suite green. Each A-fix's regression test
  fails on `5a50ddf` and passes here; each policy has a contract test per command through the
  CLI entry point and through the Fallout target (`FalloutBuildTests`, `RepositoryBuildTests`);
  `dotcov test` contracts run against a stand-in dotnet (`FakeDotnet`).

## Task 21 — 2026-10-14 — 3.0.1: CRAP completeness and exclusion corrections

- Missing executable metrics members prevent PASS; ambiguous overloads and known arity
  mismatches remain unmatched. Indexer accessors retain index and setter-value counts.
- Metrics preserve executable-line counts and source paths. Non-executable declarations
  neither require coverage nor provide executable complexity. Exclusions apply to
  metrics-only files, preserve ordered keep rules, and never consume retained siblings.
- Async bodies retain their own identity in either input order. Merged exclusion metadata
  lists only absent files. Indexed metrics matching avoids repeated candidate scans.
- Patched Fallout cryptography dependencies to 10.0.13 without suppressing NuGet audit.
- A metrics complexity below 1 on an executable member supplies no complexity (reader input
  and library callers alike) and the row still counts as unmatched, so the gate is NODATA.
  The reader keeps rows with a missing or malformed complexity as 0 instead of dropping them.
- Visual Basic metrics displays (`Sub`/`Function … As T`, generic `M(Of T)`, `Sub New`,
  `ByRef`, `Optional`, `ParamArray`, `(Of T)`, `T()`) are normalized for matching while the
  raw display is kept for listings; tuple syntax is validated there too, so a
  malformed member names the metrics file and member.
- Parentheses in a Cobertura signature (only a truncated function pointer produces them)
  make it unknown instead of an error; malformed tuple syntax stays an input error in
  metrics displays, where source tuple syntax really occurs.
- Coverage signatures with a function pointer (Coverlet truncates them to `(System.Int32))`)
  and metrics `delegate*` parameters are unknown signatures; `crap` no longer throws
  `Unbalanced tuple type`. Escaped identifiers (`@int`) drop the escape after aliasing;
  a generic `T?` that is a value type in one method and a reference type in another stays
  unresolved instead of being forced to `Nullable<T>` report-wide.
- Metrics `delegate*` parameters lower to one canonical identity (calling convention dropped,
  `void` aliased to `System.Void`), so they conflict with other known signatures while
  Coverlet's truncated spelling stays unknown.
- State-machine-only async overloads consume their metrics rows when the body and row counts
  of a member in one file agree (rows without a source file only when one file holds such
  bodies); `--source-root` now also roots relative metrics paths.
- `GateAwareTableWiringTests` use the shared `FakeDotnet`, so their two `dotcov test` cases
  run on Windows too; one Linux/macOS-only test remains (Unix file modes).
- Aligned package/action/README versions and rewrote `docs/releases/3.0.1.md` around
  user-visible behaviour.

Validation: each fix has a regression test that fails before it; PR #79's CI is the
full-suite and 100/100 coverage record.

## Task 22 — 2026-10-14 — 4.0.0: dotcov for coding agents

- Stream contract: stdout is data only; the verdict of check, test and crap leads stderr in
  every format. Help is a catalog (`DotCovCli.Help.cs`) that renders full and per-command help
  and decides accepted options, did-you-mean corrections and unknown-command advice.
- Exit codes live once in `ExitCodes` (CLI, Fallout, `gate.exitCode`); a failed `dotnet test`
  run exits 6; the Fallout target exits with dotcov's codes.
- JSON envelope (`schemaVersion`, `command`, `inputs`, `warnings`), `gate` objects on check,
  test and crap, `uncovered` range strings, relaxed escaping, trailing newline.
- Offenders worst first with uncovered ranges and partial branches (`--top`, default 20);
  `report --top`; crap `Location` column; empty-directory warnings; `--no-color`; diff lists
  changed files only; `dotcov test` holds the dotnet log back for json/md unless the run fails.
- `dotcov test --format json|md` writes the dotnet log to `TestResults/<run>/dotnet-test.log`
  and streams it to stderr only on failure, never into memory.
- Fallout reads reports behind one boundary: a report that cannot be listed or opened exits 5.
- Fallout `--coverage-json-path`. README rebuilt around usage, with an agent section; package
  READMEs and release notes rewritten; the oldest changelog entry dropped to keep 20.

Validation: `fallout Coverage` 1,977 tests, 100.0% line and branch; CI on the PR.

## Task 23 — 2026-10-14 — 4.1.0: diff exclusions, --no-color everywhere, Rider entry points

- `diff` accepts the exclusion flags, applies them to both reports, lists the exclusion once
  on stderr and carries `excluded`/`exclusionRules` in JSON (`CoverageDiffResult.Exclusion`).
- `--no-color` is accepted by `check` and `snapshot` too.
- `build/Build.cs` marks Fallout's reflection entry points with `[UsedImplicitly]`
  (JetBrains.Annotations as a private build-only package). Upstream Fallout dropped JetBrains
  annotations (Fallout-build/Fallout#76), so the fix lives here; InspectCode reports no unused
  member in `Build.cs`.

Validation: `fallout Coverage` 1,985 tests, 100.0% line and branch.

## Task 24 — 2026-10-14 — 4.2.0: name and exclude code built elsewhere

- Found by adding the badge to ANcpLua/TourPlanner: coverlet instrumented 11 dependency
  assemblies (~560 files under `/_/src/...`), so dotcov read 27.3% where the repository's own
  code is at 92.6% lines / 77.1% branches.
- `CoverageWarningKind.ExternalSources`, derived from the files a report holds (recomputed
  by Exclude, Merge and the method report constructor), unless `/_/` is a declared root.
- `ExclusionRules.External` (`//_/`, anchored: only paths starting with `/_/`; not applied to a
  report declaring `/_/` as its root, whose warnings a merge keeps), CLI `--exclude-external`, Fallout
  `--coverage-exclude-external-param`; `--keep` accepts it as its exclusion.
- README: exclusion and badge sections name the cause and both fixes.

Validation: `fallout Coverage` 1,997 tests, 100.0% line and branch.

## Task 25 — 2026-10-14 — 4.3.0: TypeScript producers and the crap scope

- Tried Vitest 5.0.4 and c8 12.0.0 on one TypeScript project (Node 24): both write
  `coverage/cobertura-coverage.xml` with one source root; Vitest counts 5 of 8 source branches,
  c8 5 of 6 (it leaves out branches of functions no test called).
- README "Other languages": Vitest first, c8 second, Jest kept as one line; crap sections in the
  README, tool README and `--help` say crap is for .NET and Istanbul/coverage.py write no complexity.
- `CrapReport.Evaluate`: when no method has complexity, or the report has no method entries
  (coverage.py, Microsoft Code Coverage), the NODATA reason names those emitters and `check`
  instead of `dotnet msbuild`.
- The TypeScript recipes include untested modules (`--coverage.include`, `--all --src`); without
  them the trial read 83.3% lines instead of 62.5%.

Validation: `fallout Coverage` 1,999 tests, 100.0% line and branch.

