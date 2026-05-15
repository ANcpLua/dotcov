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
