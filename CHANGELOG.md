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
- Sealed-hierarchy refactor of `LineDelta` to make illegal `Change`/`BeforeHits`/`AfterHits`
  combinations unrepresentable. Invariants now live in the XML doc and the producer
  (`ComputeLineChanges`), enforced by tests rather than the type.
- `O(n²)` cache on `StrictlyHitLines` + `PartiallyHitLines` (correctness-neutral; defer
  until a 50 MB+ report actually shows up in a benchmark).

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
- Numbering gap (4-6) is intentional: parallel-agent work in sibling worktrees owns those
  task numbers; integration-time merge will sequence them.
