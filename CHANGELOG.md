# Changelog

Rolling task-session log, oldest → newest, capped at 20 entries.

## Task 1 — 2026-05-15 — Coverlet-cased `branch="True"`, branch dedup, 100% self-coverage

Built on top of `b306779` (which switched `ParseClass` to a full `<class>` subtree walk
with per-source-line Math.Max aggregation across multiple IL-type class blocks).

Changed:
- Case-insensitive match for `branch="true"` / `branch="True"` in `CoberturaParser`. The
  previous literal-pattern compare rejected Coverlet's Pascal-cased value (`XmlConvert
  .ToString(bool)`), silently dropping every branch line in Coverlet output — branch counts
  rendered as a fake 100% because `TotalBranches` was 0.
- New `BranchLinesSeen` set on `LineAccumulator` so branches are counted once per source
  line. Without this, lines that appear both under `<methods>/<method>/<lines>` and
  `<class>/<lines>` would double-count their branch totals.
- `CoverageDiff.Compare` switch reordered to remove the unreachable `_ => throw` default.
- `Ansi.EnableOnWindows` marked `[ExcludeFromCodeCoverage]` — its two branches can only be
  exercised on different OSes; the "never throws" contract test still covers it.
- Added `coverlet.runsettings` excluding `[CompilerGenerated]` and `[GeneratedCode]` types
  and `obj/` source-generator output, so async-state-machine phantom branches don't cap us.

Verified:
- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` followed by
  `dotcov report TestResults/ --exclude-generated` reports **Lines 100%** /
  **Branches 100%** across 165 tests.
- Regression tests cover the methods+class-level duplication (with `branch="True"`
  branches that previously double-counted), case-insensitive `branch` attr matching across
  `true`/`True`/`TRUE`, missing/malformed `hits`, and Removed/Added file rendering across
  all three formatters (Table, Markdown, JSON).

Notes:
- Numbering starts at 1; no prior CHANGELOG existed in repo metadata.
- The pre-existing `97.9% line / 100% branch` figure in commit `ee3bf32` was an artefact
  of the casing bug — `branch="True"` lines were ignored, so total branches was 0 and the
  rate trivially rendered as 100%.

## Task 2 — 2026-05-15 — Codecov-pattern alignment: branch dedup, strict line state, indirect-change diff

Lifted three semantically-valuable patterns from Codecov's coverage model and grafted them
into dotcov's parser/diff library where they fit a CLI tool's scope. The rest of Codecov's
hub-service patterns (PR comments, flags/carryforward, patch coverage, pseudo-comparison,
raw-report retention) were skipped: each would require either GitHub auth, git-tree access,
or persistent server-side state — all out of scope for a stateless parser+CLI.

Changed:
- `FileCoverage.BranchesByLine` — new per-line `(Covered, Total)` dictionary. Parser
  populates it; `MergeWith` reconciles cross-report branches with Math.Max per line instead
  of summing. Fixes a real double-count bug for multi-CI uploads of the same file (unit +
  integration test runs both emitting Cobertura for the same source).
- `LineStatus { Miss, Partial, Hit }` enum plus `FileCoverage.GetLineStatus(line)` and
  `StrictLineRate` (both at file and report scope). Codecov-style: a line whose branches
  are not all exercised is classified `Partial`, downgrading the strict rate. Default
  `LineRate` semantics unchanged — it still matches Coverlet/Cobertura/ReportGenerator's
  hits/total formula so library consumers see consistent numbers.
- `CoverageDiff` now emits line-level `LineDelta` entries for each `FileDelta`. Four kinds:
  `Added`, `Removed`, `NewlyHit`, `NewlyMissed`. Surfaces Codecov's "indirect coverage
  changes" — lines whose hit/miss state flipped between reports, the canonical signal of
  removed tests / upstream regressions / dependency drift.
- Formatters surface the new data: markdown renders an `### Indirect changes` section
  with per-file breakdowns; table emits a one-line summary trailer; JSON includes
  `lineChanges` arrays and an `indirectLineChanges` summary count.

Verified:
- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` followed
  by `dotcov report TestResults/ --exclude-generated` reports **Lines 563/563 (100%)**,
  **Branches 280/280 (100%)** across 193 tests.
- Branch dedup tested with overlapping-line scenario asserting Math.Max semantics, plus
  the original disjoint-line scenario that still sums (now derived from union, not
  positional add).
- Line-level diff tested for all four `LineChangeKind` arms plus the "unchanged" cases
  that must not produce noise: same hit count, different hit counts both still hit, and
  miss-on-both-sides.

Notes:
- `ComputeLineChanges` restructured into guard-then-classify shape (no compound `&&` in
  the inner classifier) so coverlet doesn't penalise short-circuit arms that the union-of-
  keys precondition already rules out. Same trick used earlier for `CoverageDiff.Compare`.
- Patterns explicitly skipped with reasons documented in commit body.

## Task 3 — 2026-05-15 — Polish pass on `e08ddec`: plural-singular bug, doc drift, test gaps

Multi-agent review of `e08ddec` surfaced one real bug, three doc-vs-code drifts, and four
test gaps that left the new public surface "covered" (executed) but undertested. This task
closes the punch list without expanding scope into new public API.

Changed:
- `TableFormatter.FormatDiff` and `MarkdownFormatter.AppendIndirectChanges` now pluralise
  both `line/lines` AND `file/files` independently. Previous output `"1 lines flipped across
  1 file"` corrected to `"1 line flipped across 1 file"`; the corresponding regression test
  had codified the bug verbatim and is now flipped to assert the singular form.
- `LineDelta` XML doc tightened to document the per-`LineChangeKind` invariants explicitly
  (`Added` ⇒ `BeforeHits is null`, `Removed` ⇒ `AfterHits is null`, hit-count-still-hit
  transitions like `100 → 1` are dropped). Tests now pin those payloads for `Added`,
  `Removed`, and `NewlyHit` instead of asserting only `.Change`.
- `BranchesByLine` doc adds a `<para>` note that `MergeWith` uses `Math.Max` per component
  and that a mismatched `Total` for the same line across reports usually means different
  compile targets — the merge keeps the larger total without flagging the divergence.
- `GetLineStatus` doc adds the "tracked-but-zero-hits vs not-tracked" caveat plus a
  pointer at `LineHits.ContainsKey` for callers needing the distinction.
- `StrictLineRate` doc replaced the verbatim `hits/(hits+partials+misses)` formula with
  the actual implementation (`StrictlyHitLines / LinesTotal`) plus a note that the two are
  equal only under the parser's invariant `LineHits.Count == LinesTotal`. Hand-built
  `FileCoverage` records can diverge from the Codecov formula by exactly that gap.
- `ComputeLineChanges` inline comment rewritten to match the actual structure — early-return
  guards for readability — instead of overclaiming a coverlet short-circuit workaround.
- `README.md` Public API surface block extended with the new types: `LineStatus`,
  `LineDelta`, `LineChangeKind`, `FileDelta.LineChanges`, `CoverageDiffResult.WithLineChanges`,
  `TotalLineChanges`, plus `FileCoverage.BranchesByLine`, `GetLineStatus`, `StrictLineRate`,
  `StrictlyHitLines`, `PartiallyHitLines`, `BranchDetail`. README had silently fallen behind
  `e08ddec`'s public surface.

Added tests:
- `Parse_BranchedLines_PopulatesBranchesByLineFromXml` — round-trips the parser's output
  through `BranchesByLine` + `GetLineStatus`. Closes the largest test asymmetry: every
  prior `BranchesByLine` test used a hand-built `FileCoverage`, so a parser regression
  would have left aggregate counts correct while silently zeroing the per-line dict.
- `FileCoverage_MergeWith_BranchesOnlyInOther_PreservesEntry` — single-sided merge.
  A regression that overwrote with an empty dict instead of unioning would flip the
  merged line from `Partial` back to `Hit`; no prior test would have caught it.
- `StrictLineRate_AllLinesPartial_IsZero` — boundary case where every tracked line is
  `Partial`. Asserts `StrictLineRate == 0` while `LineRate == 1.0`.
- `FormatDiff_OneLineFlippedAcrossOneFile_TrailerUsesSingularForBoth` and
  `FormatDiff_MultipleLinesAndFilesAffected_TrailerUsesPluralForBoth` (table) plus
  `FormatDiff_MultipleLinesAndFiles_HeadingUsesPluralForBoth` (markdown) — pin both
  pluralisation branches now that they're independent.

Verified:
- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` followed
  by `dotcov report TestResults/ --exclude-generated` still reports **Lines 100%** /
  **Branches 100%** across the expanded test suite. Test count grew by 6 over `e08ddec`'s
  193.

Explicitly skipped (each would have required new public API or breaking changes):
- Codecov-style `MergeWith` divergence warnings (would need a new `CoverageReport.Warnings`
  collection; deferred until a real consumer asks for it).
- `TryGetLineStatus` sibling for the unknown-line distinction (additive but no in-tree
  caller needs it yet; documented in the XML comment instead).
- `O(n²)` cache on `StrictlyHitLines` + `PartiallyHitLines` (correctness-neutral; defer
  until a 50 MB+ report actually shows up in a benchmark).

## Task 4 — 2026-05-15 — `LineDelta` sealed-hierarchy: compile-time invariant enforcement

Picked up Task 3's explicitly-deferred refactor: the per-`LineChangeKind` invariants
documented on the flat `LineDelta` record struct (Added ⇒ `BeforeHits is null`, etc.) were
runtime conventions — `new LineDelta(42, BeforeHits: null, AfterHits: null, NewlyHit)`
compiled and survived every consumer. Closed sealed-hierarchy collapses the discriminator
+ payload into one type per variant so the illegal combinations literally don't exist on
the type.

Breaking change to the `DotCov` public API; the JSON wire format stays compatible.

Changed:
- `LineDelta` is now an `abstract record` with a **private** constructor and four nested
  sealed records: `Added(int Line, int AfterHits)`, `Removed(int Line, int BeforeHits)`,
  `NewlyHit(int Line, int BeforeHits, int AfterHits)`, `NewlyMissed(int Line, int BeforeHits,
  int AfterHits)`. Private base ctor restricts derivation to the four nested types so the
  set is closed at the type system level.
- `LineChangeKind` enum **deleted** — fully redundant with the variant discriminator.
- `CoverageDiff.ComputeLineChanges` constructs the right variant at each guard arm instead
  of building a flat record with positional nulls. `new LineDelta.Added(line, afterHits)`
  replaces `new LineDelta(line, null, afterHits, LineChangeKind.Added)`, etc.
- `JsonFormatter` discriminates the variants via a typed `is`-pattern chain and emits the
  same all-lowercase `change` strings (`"added"`, `"removed"`, `"newlyhit"`, `"newlymissed"`)
  the previous code produced via `enum.ToString().ToLowerInvariant()`. `beforeHits` /
  `afterHits` are omitted on the variants that don't carry them, preserving the existing
  `WhenWritingNull` contract.
- `MarkdownFormatter.AppendIndirectChanges` aggregates per-variant counts via the same
  type-pattern chain instead of four enum-`.Count` calls. Per-file fragment order
  (newly missed → newly hit → added → removed) is preserved verbatim.
- `TableFormatter` was unaffected (it never per-variant-rendered, only counts the
  aggregate `TotalLineChanges`); verified by re-running its 19 tests unchanged.
- Test assertions like `Assert.Equal(LineChangeKind.Added, d.Change)` rewritten as
  `Assert.IsType<LineDelta.Added>(d)`; the existing payload-pinning lines
  (`Assert.Equal(4, removed.BeforeHits); Assert.Null(removed.AfterHits);`) collapse to a
  single non-nullable `Assert.Equal(4, removed.BeforeHits)` because the variant has no
  AfterHits field to assert against.
- README `Public API surface` block updated: `LineChangeKind` removed, `LineDelta` rewritten
  as the sealed-hierarchy declaration.

Added tests (3 new, in `JsonFormatterTests.cs`):
- `FormatDiff_AddedLine_EmitsAddedChangeWithOnlyAfterHits` — pins JSON wire format for the
  Added variant: `change="added"`, `afterHits` populated, `beforeHits` key absent.
- `FormatDiff_RemovedLine_EmitsRemovedChangeWithOnlyBeforeHits` — symmetric pin for Removed.
- `FormatDiff_NewlyHitLine_EmitsNewlyHitChangeWithBothHits` — pins the all-lowercase
  `"newlyhit"` wire-format string. (`"newlymissed"` was already pinned.)

Verified:
- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` —
  **200 tests, 0 failed**.
- `dotcov report tests/DotCov.Tests/TestResults --exclude-generated` — **Lines 577/577
  (100%) / Branches 300/300 (100%)** across every file in the library, including the
  four files modified for this task (`CoverageDiff.cs`, `JsonFormatter.cs`,
  `MarkdownFormatter.cs`, `TableFormatter.cs`).

Notes:
- The JSON wire format **stays binary-compatible**: same property names (`line`,
  `beforeHits`, `afterHits`, `change`), same omit-when-null contract, same all-lowercase
  `change` discriminator strings. Downstream consumers don't need to change.
- Roslyn doesn't currently prove exhaustiveness for switch-expressions over an abstract
  record + private-ctor + nested-sealed-derivatives shape; a `c switch { … }` would force
  an unreachable `_ => throw` arm that breaks the 100%-coverage gate. Consumers use
  `is`-pattern chains ending in an unguarded cast instead — equivalent at the IL level,
  reachable for coverage, and a future variant would surface as an `InvalidCastException`
  rather than slipping through silently.
- `LineDelta`'s public surface intentionally exposes a `Line` get-only property on the base
  rather than primary-constructor positional deconstruction; positional records on the base
  would have leaked an inherited `Line` parameter conflicting with each variant's own
  `Line`. The current shape mirrors the standard C# sealed-hierarchy idiom.

## Task 5 — 2026-05-15 — `CoverageWarning` surface for silent merge/parse anomalies

Lifts the "defer-until-asked" warnings collection Task 3 explicitly skipped into a real
public surface. Two CRITICAL/HIGH findings from a multi-agent review of the parser/merger
become structured observable signals instead of silent drops:

1. `FileCoverage.MergeWith` reconciles cross-report `BranchesByLine` with `Math.Max` per
   `(Covered, Total)` component. When two CI jobs disagree on `Total` for the same line
   (e.g. Release vs. Debug compile targets), the merge previously fabricated the larger
   tuple with zero indication. It now emits a `BranchTotalMismatch` warning so divergent
   uploads stop being a silent bug.

2. `CoberturaParser.TryParseConditionCoverage` returns `false` on regex miss or int
   overflow. The caller previously dropped the branch entry silently — a malformed
   Coverlet emitter regression would have zeroed out branch coverage with no signal. The
   parser now records a `MalformedConditionCoverage` warning for every malformed string,
   keeping the parse robust while making the omission observable.

Changed:
- New `CoverageWarningKind` enum (`BranchTotalMismatch`, `MalformedConditionCoverage`)
  and `CoverageWarning(Kind, File, Line, Detail)` record-struct in `CoverageReport.cs`.
- `CoverageReport.Warnings { get; init; } = []` — init-only collection, defaults empty.
- `FileCoverage.MergeWithWarnings(FileCoverage other)` returns a tuple
  `(Merged, IReadOnlyList<CoverageWarning>)`. Original `MergeWith` kept as the lossy
  convenience that drops warnings — every caller in the codebase that just wants the
  merged file stays unchanged. `CoverageReport.Merge` now uses the tuple variant so it
  can append divergences alongside per-side warnings.
- `CoberturaParser` threads a `List<CoverageWarning>` through `ParseCore` /
  `ParseCoreAsync` → `ConsumeClass` → branch parsing. Both regex-miss and int-overflow
  paths now emit the same warning kind, with the raw `condition-coverage` string in
  `Detail`.
- `CoverageReport.Exclude` carries warnings forward — filtering files for display
  doesn't unobserve a parser anomaly. Same `Warnings` propagation in `Merge`.
- Formatters surface warnings additively (silent for clean reports, structured when
  populated):
  - `MarkdownFormatter.Format` appends `### Warnings` section with per-entry bullets
    matching `- `{File}:{Line}` — {Kind}: {Detail}`.
  - `JsonFormatter.Format` includes a `"warnings"` array (omitted when empty via the
    existing `WhenWritingNull` policy), serialised as `{ kind, file, line, detail }`.
  - `TableFormatter.Format` emits a one-line `Warnings: N` trailer after `TOTAL`,
    dimmed via the existing `AnsiPen.Dim` helper. Detailed list is markdown's job.
- README Public API surface block extended with `Warnings`, `CoverageWarning`,
  `CoverageWarningKind`, and the new `MergeWithWarnings` overload.

Verified:
- `dotnet test --collect:"XPlat Code Coverage" --settings coverlet.runsettings` followed
  by `dotcov report TestResults/ --exclude-generated` reports **Lines 625/625 (100%)**,
  **Branches 296/296 (100%)** across 218 tests (was 199 before this task).
- New regression tests cover: default-empty `Warnings`, `MergeWith`/`MergeWithWarnings`
  parity for the merged counts, `BranchTotalMismatch` emission with file/line/detail
  payload, matching-totals silence, disjoint-line silence, `CoverageReport.Merge`
  carrying per-side warnings plus appending new ones, distinct-file merges fabricating
  no false anomalies, `Exclude` preserving warnings even when the source file is
  filtered out, parser warnings for both regex-miss and int-overflow paths (sync +
  async parity), markdown heading/bullet shape, JSON null-omission contract + four-
  field round-trip, table trailer position + dim ANSI escape.

Notes:
- `FormatDiff` paths in all three formatters intentionally left alone — Agent A owns
  those sections per the parallel-agents scope split. Warnings flow on the `Format`
  path only.
- `MergeWith` (single-return) preserved as the public lossy overload rather than
  removed, even though the user authorised breaking changes. Rationale: the codebase has
  ~6 internal callers (parser, diff producer, the convenience overload in
  `CoverageReport.Merge`) that only ever needed the merged file; forcing every callsite
  to discard the warnings tuple would have added noise without value. The two-overload
  shape mirrors `Exclude(patterns)` / `Exclude(patterns, keep)`.

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
