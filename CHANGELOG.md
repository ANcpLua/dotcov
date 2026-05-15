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
