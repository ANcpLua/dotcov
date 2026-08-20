using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace DotCov;

/// <summary>
/// Streaming Cobertura XML parser. Same pattern as AccessReportXml:
/// XmlReader cursor walks the document — XML never held in memory.
/// Secure: DtdProcessing.Ignore + XmlResolver = null (DOCTYPE skipped, entities never
/// resolve, so XXE payloads still throw), character cap.
/// </summary>
public static partial class CoberturaParser
{
    private const long DefaultMaxChars = 50_000_000;
    private const string DefaultPattern = "**/coverage.cobertura.xml";

    public static CoverageReport Parse(Stream stream, long maxChars = DefaultMaxChars)
    {
        using var reader = XmlReader.Create(stream, CreateSecureSettings(maxChars));
        return ParseCore(reader);
    }

    public static async Task<CoverageReport> ParseAsync(
        Stream stream, long maxChars = DefaultMaxChars, CancellationToken ct = default)
    {
        using var reader = XmlReader.Create(stream, CreateSecureSettings(maxChars, async: true));
        return await ParseCoreAsync(reader, ct);
    }

    public static CoverageReport ParseFile(string path, long maxChars = DefaultMaxChars)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Parse(stream, maxChars);
        }
        catch (XmlException ex)
        {
            // XmlException knows line/column but not which file — fatal for directory
            // aggregates, where "Unexpected end of file. Line 2, position 1." names none of
            // the N reports. Rethrow the same exception type (the published contract callers
            // catch) with the path prefixed. The trailing location sentence is stripped from
            // the inner message so the 4-arg ctor — the only one that carries
            // LineNumber/LinePosition, structured data pre-0.0.3 library consumers read —
            // can re-append it exactly once (it appends " Line X, position Y." whenever the
            // line is nonzero). On runtimes with non-English satellite resources the strip
            // is a no-op and the localized sentence appears twice — degraded formatting,
            // still-correct coordinates; culture-aware stripping is deliberately not attempted.
            throw new XmlException(
                $"{path}: {LocationSentencePattern().Replace(ex.Message, "")}",
                ex, ex.LineNumber, ex.LinePosition);
        }
    }

    /// <summary>
    /// Parse and merge every matching report under <paramref name="directory"/>. Only two
    /// pattern shapes are supported: <c>filename</c> (top level only) and <c>**/filename</c>
    /// (recursive). Any other directory component throws instead of silently matching
    /// nothing — a glob that quietly matches zero files flows into
    /// <see cref="CoverageReport.Evaluate"/> as "nothing was measured", the most invisible
    /// possible misconfiguration.
    /// </summary>
    public static CoverageReport ParseDirectory(string directory, string pattern = DefaultPattern) =>
        ParseDirectory(directory, pattern, DefaultMaxChars);

    /// <summary>
    /// <see cref="ParseDirectory(string, string)"/> with an explicit per-file character cap.
    /// A distinct overload rather than an optional parameter on the existing signature —
    /// default arguments are baked into callers at compile time, so widening the published
    /// signature would be a binary-breaking change for compiled consumers.
    /// </summary>
    public static CoverageReport ParseDirectory(string directory, string pattern, long maxChars)
    {
        var name = Path.GetFileName(pattern);
        // name.Length == 0 catches "" and "**/": both produce an empty filename that
        // Directory.GetFiles matches against nothing, silently returning an empty report —
        // the exact invisible misconfiguration this gate exists to reject.
        if (name.Length is 0 || pattern[..^name.Length] is not ("" or "**/") || name.Contains('\\'))
            throw new ArgumentException(
                $"Unsupported pattern '{pattern}': only 'filename' and '**/filename' are supported.",
                nameof(pattern));

        var files = Directory.GetFiles(directory, name,
            // Recurse exactly when the gate above admitted the "**/" prefix — never re-derived
            // from the whole pattern: Contains("**") disagreed with the gate for a '**' inside
            // the NAME portion ('**coverage.xml' is filename-shaped, top level only, yet recursed).
            new EnumerationOptions { RecurseSubdirectories = pattern.StartsWith("**/", StringComparison.Ordinal) });

        if (files.Length is 0)
            return CoverageReport.Empty;

        return files
            .OrderBy(static f => f, StringComparer.Ordinal)
            .Select(f => ParseFile(f, maxChars))
            .Aggregate(CoverageReport.Merge);
    }

    public static CoverageReport ParsePath(string path) => ParsePath(path, DefaultMaxChars);

    /// <summary>
    /// <see cref="ParsePath(string)"/> with an explicit character cap, threaded through to
    /// every file parsed. Same overload-not-optional-parameter rationale as
    /// <see cref="ParseDirectory(string, string, long)"/>.
    /// </summary>
    public static CoverageReport ParsePath(string path, long maxChars)
    {
        if (File.Exists(path))
            return ParseFile(path, maxChars);
        if (Directory.Exists(path))
            return ParseDirectory(path, DefaultPattern, maxChars);

        throw new FileNotFoundException($"No file or directory at '{path}'.");
    }

    private static XmlReaderSettings CreateSecureSettings(long maxChars, bool async = false) => new()
    {
        // Ignore, not Prohibit: reference Cobertura, gcovr, and coverage.py all emit
        // `<!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">`
        // on every report, so Prohibit rejected the format's canonical emitters. Ignore skips
        // the DTD without processing it; with XmlResolver = null external entities can never
        // resolve and an entity reference in content still throws — XXE stays dead. The
        // entity-expansion cap is belt-and-braces for the same threat.
        DtdProcessing = DtdProcessing.Ignore,
        MaxCharactersFromEntities = 1024,
        XmlResolver = null,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = maxChars,
        Async = async
    };

    // ── Aggregation primitive ─────────────────────────────────────────────────
    //
    // Cobertura emits one `<class>` block per IL type. A single source file routinely
    // produces several: the source class itself, each compiler-synthesized state-machine
    // class for async methods, each nested record's Equals/GetHashCode shim, and so on.
    // Within one block, every `<method><lines>` and the class-level summary `<lines>`
    // repeat the same line numbers with the same or different hit counts.
    //
    // We collect into Dictionary<filename, LineAccumulator> and reconcile each per-line
    // datum with Math.Max — both for hit counts and for branch (Covered, Total) pairs.

    private sealed class LineAccumulator
    {
        public readonly Dictionary<int, int> LineHits = new();

        // Per-line branch dedup: Coverlet emits the same branched line under
        // <methods>/<method>/<lines> AND <class>/<lines>, and a single source line may be
        // re-emitted under separate <class> blocks (record + state machine + partials).
        // Keying on line number with Math.Max prevents double-counting in all of those.
        public readonly Dictionary<int, (int Covered, int Total)> BranchesByLine = new();

        // line → (coverlet condition `number` → covered outcomes of that 2-way jump, 0–2).
        // Same Math.Max dedup as BranchesByLine, but keyed per condition so the cross-report
        // merge can union by condition identity instead of collapsing to a single count.
        public readonly Dictionary<int, Dictionary<int, int>> ConditionsByLine = new();

        public void AddCondition(int line, int number, int covered)
        {
            if (!ConditionsByLine.TryGetValue(line, out var conds))
                ConditionsByLine[line] = conds = new Dictionary<int, int>();
            conds[number] = conds.TryGetValue(number, out var existing) ? Math.Max(existing, covered) : covered;
        }
    }

    private static CoverageReport ParseCore(XmlReader reader)
    {
        // Ordinal, not OrdinalIgnoreCase: case-differing filenames are genuinely distinct
        // files on the Linux filesystems the format's native emitters (gcovr, coverage.py,
        // coverlet-on-Linux) run on — linux/net/netfilter really contains both xt_TCPMSS.c
        // and xt_tcpmss.c. Case-insensitive keying silently fused such pairs, erasing the
        // less-covered file's misses. Windows cross-report stability comes from normalizing
        // the key itself (drive-letter casing, separator direction) in ConsumeClass instead.
        var files = new Dictionary<string, LineAccumulator>(StringComparer.Ordinal);
        var warnings = new List<CoverageWarning>();
        var sourceRoots = new List<string>();

        while (reader.Read())
        {
            if (reader is { NodeType: XmlNodeType.Element, LocalName: "source" })
            {
                ConsumeSource(reader, sourceRoots, warnings);
                continue;
            }

            if (reader is not { NodeType: XmlNodeType.Element, LocalName: "class" })
                continue;

            ConsumeClass(reader, files, warnings, sourceRoots);
        }

        return Materialize(files, warnings, sourceRoots);
    }

    private static async Task<CoverageReport> ParseCoreAsync(XmlReader reader, CancellationToken ct)
    {
        var files = new Dictionary<string, LineAccumulator>(StringComparer.Ordinal);
        var warnings = new List<CoverageWarning>();
        var sourceRoots = new List<string>();

        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();

            if (reader is { NodeType: XmlNodeType.Element, LocalName: "source" })
            {
                ConsumeSource(reader, sourceRoots, warnings);
                continue;
            }

            if (reader is not { NodeType: XmlNodeType.Element, LocalName: "class" })
                continue;

            ConsumeClass(reader, files, warnings, sourceRoots);
        }

        return Materialize(files, warnings, sourceRoots);
    }

    /// <summary>
    /// Capture one <c>&lt;source&gt;</c> root. In Cobertura document order <c>&lt;sources&gt;</c>
    /// precedes <c>&lt;packages&gt;</c>, so the roots are complete before the first
    /// <c>&lt;class&gt;</c> arrives — no second pass needed. Leaves the reader on the element's
    /// text node; the caller's next Read lands on the harmless end tag.
    /// </summary>
    private static void ConsumeSource(XmlReader reader, List<string> sourceRoots, List<CoverageWarning> warnings)
    {
        if (reader.IsEmptyElement) return;
        if (!reader.Read() || reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA)) return;

        // Canonical root (PathIdentity.NormalizeRoot): no-op spellings (".", "./" — grcov
        // emits <source>.</source>) collapse to the "" sentinel, which ResolveFileKey reads
        // as "leave relative filenames unprefixed" and Materialize hides when it is the only
        // declared root. The sentinel is RECORDED rather than discarded: a no-op declared
        // alongside real roots is a second resolution convention, so it must count toward
        // the multi-root warning, hold its document-order slot (the FIRST declared root wins
        // resolution, no-op or not), and stay visible to Merge's roots comparison — dropping
        // it made a ('.', '/real') report indistinguishable from a ('/real') one. Dedup keeps
        // respellings of one root ("/repo" twice in ReportGenerator's merged output, "." plus
        // "./", c:\x vs C:/x/) from faking that multiplicity: identical resolution is not
        // identity ambiguity.
        var root = PathIdentity.NormalizeRoot(reader.Value);
        if (sourceRoots.Contains(root)) return;   // List.Contains is Ordinal for strings

        sourceRoots.Add(root);
        if (sourceRoots.Count is 2)
            warnings.Add(new CoverageWarning(
                CoverageWarningKind.FileIdentityAmbiguous,
                "",
                0,
                $"multiple <source> roots - {(sourceRoots[0].Length is 0
                    ? "leaving relative filenames unprefixed (the first declared root is a no-op)"
                    : $"resolving relative filenames against the first ('{sourceRoots[0]}')")}; files may not be attributable to a unique root"));
    }

    private static void ConsumeClass(
        XmlReader reader,
        Dictionary<string, LineAccumulator> files,
        List<CoverageWarning> warnings,
        List<string> sourceRoots)
    {
        var filename = reader.GetAttribute("filename");
        if (filename is null) return;

        // Normalize path separators so the same source file merges across machines/CI jobs
        // regardless of emitter convention (Windows coverlet writes `\`, Linux writes `/`).
        // This string is the file's identity key in Materialize/Merge, so it must be stable.
        filename = filename.Replace('\\', '/');
        filename = ResolveFileKey(filename, sourceRoots);

        if (!files.TryGetValue(filename, out var acc))
        {
            acc = new LineAccumulator();
            files[filename] = acc;
        }

        // Walk the entire `<class>` subtree so that lines emitted under
        // `<methods><method><lines>` AND under the trailing `<lines>` summary
        // both contribute. ReadSubtree leaves the outer reader positioned on
        // the closing `</class>` tag when we're done.
        using var sub = reader.ReadSubtree();
        sub.MoveToContent();

        // Coverlet nests <conditions><condition number= coverage=/></conditions> inside each
        // branched <line>. In document order conditions follow their line, so we attribute them
        // to the most recent branched line; -1 means "current line carries no per-condition detail".
        var conditionLine = -1;

        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;

            if (sub.LocalName == "condition")
            {
                if (conditionLine >= 0 &&
                    int.TryParse(sub.GetAttribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var condNumber) &&
                    TryParseConditionOutcomes(sub.GetAttribute("coverage"), out var condCovered))
                {
                    acc.AddCondition(conditionLine, condNumber, condCovered);
                }
                continue;
            }

            if (sub.LocalName != "line") continue;
            conditionLine = -1;

            if (!int.TryParse(sub.GetAttribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNum))
                continue;

            var hitsAttr = sub.GetAttribute("hits");
            var hits = 0;
            if (hitsAttr is not null)
            {
                // Parse as long and saturate: hit counts above int.MaxValue are real (soak
                // runs, 64-bit-counter emitters like gcovr/llvm-cov), and only >0 matters
                // downstream — degrading overflow to 0 silently flipped covered lines to
                // misses. Present-but-unparseable warns instead of silently recording a miss;
                // an absent attribute stays a warning-free 0 (some emitters omit it).
                if (long.TryParse(hitsAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                    hits = (int)Math.Clamp(h, int.MinValue, int.MaxValue);
                else
                    warnings.Add(new CoverageWarning(
                        CoverageWarningKind.MalformedHits,
                        filename,
                        lineNum,
                        $"hits='{hitsAttr}' could not be parsed - treating as 0"));
            }

            acc.LineHits[lineNum] = acc.LineHits.TryGetValue(lineNum, out var existing)
                ? Math.Max(existing, hits)
                : hits;

            // Cobertura emitters disagree on casing: original Cobertura/JaCoCo write
            // `branch="true"`, Coverlet writes `branch="True"` (XmlConvert.ToString(bool)).
            // A literal-pattern compare silently dropped Coverlet branches and rendered
            // branch coverage as a fake 100% (with TotalBranches=0).
            if (string.Equals(sub.GetAttribute("branch"), "true", StringComparison.OrdinalIgnoreCase) &&
                sub.GetAttribute("condition-coverage") is { } cond)
            {
                if (TryParseConditionCoverage(cond, out var covered, out var total))
                {
                    acc.BranchesByLine[lineNum] = acc.BranchesByLine.TryGetValue(lineNum, out var existingBranch)
                        ? (Math.Max(existingBranch.Covered, covered), Math.Max(existingBranch.Total, total))
                        : (covered, total);
                    conditionLine = lineNum;   // collect this branched line's <condition> children
                }
                else
                {
                    // Surface emitter regressions (malformed condition strings, overflow) as
                    // structured warnings instead of silently dropping the branch entry.
                    warnings.Add(new CoverageWarning(
                        CoverageWarningKind.MalformedConditionCoverage,
                        filename,
                        lineNum,
                        $"condition-coverage='{cond}' could not be parsed"));
                }
            }
        }
    }

    /// <summary>
    /// Resolve a separator-normalized class filename to its identity key by prepending the
    /// report's <c>&lt;source&gt;</c> root. Cobertura emitters (coverage.py, gcovr, cover2cover)
    /// write filenames relative to a source root; discarding the root made two DIFFERENT files
    /// that share a relative name (monorepo <c>svc-a/app/main.py</c> vs <c>svc-b/app/main.py</c>)
    /// collide on one key and silently fuse via Math.Max. Already-rooted filenames are kept as-is
    /// — the rooted check is manual (leading '/' or a drive-letter prefix) because
    /// <c>Path.IsPathRooted("C:/x")</c> is false on Linux and reports cross machines. With
    /// multiple roots the first is chosen deterministically (the analyzing machine cannot probe
    /// the disk the report was produced on) and parse emits a
    /// <see cref="CoverageWarningKind.FileIdentityAmbiguous"/> warning. The root is applied
    /// unconditionally, never only on collision — a conditional prefix would make a file's
    /// identity unstable across runs. A first-declared no-op root (the "" sentinel from
    /// <see cref="ConsumeSource"/>) resolves as "no prefix": first-wins applies to the
    /// DECLARED order, so a later real root must not jump the queue.
    /// </summary>
    private static string ResolveFileKey(string filename, List<string> sourceRoots)
    {
        if (sourceRoots.Count > 0 && sourceRoots[0].Length > 0 && !IsRooted(filename))
        {
            var root = sourceRoots[0];
            filename = root.EndsWith('/') ? root + filename : $"{root}/{filename}";
        }

        // Uppercase a leading drive letter so `c:\x\A.cs` and `C:/x/A.cs` — the same file
        // emitted by different Windows toolchains — produce one Ordinal key. Key normalization,
        // not a case-insensitive comparer: a Dictionary has a single comparer for every key,
        // and per-key conditional comparison would break hash/equality consistency.
        if (filename.Length >= 3 && char.IsAsciiLetterLower(filename[0]) && filename[1] == ':' && filename[2] == '/')
            filename = char.ToUpperInvariant(filename[0]) + filename[1..];

        return filename;
    }

    private static bool IsRooted(string path) =>
        path.StartsWith('/') ||
        (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static CoverageReport Materialize(
        Dictionary<string, LineAccumulator> files,
        List<CoverageWarning> warnings,
        List<string> sourceRoots)
    {
        var result = new List<FileCoverage>(files.Count);
        foreach (var (filename, acc) in files)
        {
            // Keep per-condition detail only where it reconstructs the line aggregate as 2-outcome
            // jumps (the universal case for &&/||/?:/??/?.). If a switch jump-table makes it
            // inconsistent, drop to the line aggregate so merge never invents a total the emitter
            // didn't report — the invariant Merge's per-condition union relies on.
            var conditionsByLine = new Dictionary<int, IReadOnlyDictionary<int, int>>();
            foreach (var (line, conds) in acc.ConditionsByLine)
                // Every line in ConditionsByLine has a BranchesByLine entry by construction —
                // AddCondition only fires after the aggregate is recorded — so index directly
                // (a missing key would be a broken invariant worth throwing on, not silently skipping).
                if (conds.Count * 2 == acc.BranchesByLine[line].Total)
                    conditionsByLine[line] = new Dictionary<int, int>(conds);

            result.Add(FileCoverage.FromLineData(filename, acc.LineHits, acc.BranchesByLine, conditionsByLine));
        }

        return new CoverageReport(result)
        {
            Warnings = warnings,
            // A report whose ONLY declared root is the no-op sentinel exposes no roots at
            // all — lone <source>.</source> adds no identity information (pinned public
            // behavior). Mixed declarations keep the sentinel so Merge can tell
            // ('.', '/real') — relative filenames unprefixed — apart from ('/real').
            SourceRoots = sourceRoots is [""] ? [] : sourceRoots
        };
    }

    private static bool TryParseConditionCoverage(string cond, out int covered, out int total)
    {
        covered = 0;
        total = 0;
        var match = ConditionPattern().Match(cond);
        if (!match.Success) return false;
        return int.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out covered) &&
               int.TryParse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture, out total);
    }

    // coverlet's per-<condition> `coverage` is the percentage of that branch's outcomes hit.
    // Branches are 2-outcome jumps (taken/not-taken): 0% -> 0, 50% -> 1, 100% -> 2 covered.
    // A non-2-way figure (e.g. 33.33% from a switch arm) still parses, but Materialize's
    // 2-outcome consistency gate then drops that line back to the line-level aggregate.
    private static bool TryParseConditionOutcomes(string? coverage, out int covered)
    {
        covered = 0;
        if (coverage is null) return false;
        var span = coverage.AsSpan().TrimEnd('%');
        if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return false;
        // Range-gate before rounding (NaN and ±Infinity fail the pattern too): a percent
        // outside [0,100] would put a covered value outside 0–2 into the per-condition map,
        // which a later merge recompute turns into BranchesHit > BranchesTotal.
        if (percent is not (>= 0 and <= 100)) return false;
        covered = (int)Math.Round(percent / 100.0 * 2.0, MidpointRounding.AwayFromZero);
        return true;
    }

    [GeneratedRegex(@"\((\d+)/(\d+)\)")]
    private static partial Regex ConditionPattern();

    // The trailing " Line X, position Y." sentence XmlException appends to its message when
    // it carries nonzero coordinates. ParseFile strips it from the inner message before the
    // path-prefixing rethrow so the 4-arg ctor can re-append it exactly once.
    [GeneratedRegex(@"\s*Line \d+, position \d+\.$")]
    private static partial Regex LocationSentencePattern();
}
