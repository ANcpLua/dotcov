using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DotCov.Formatters;

/// <summary>
/// Coverage reports rendered as JSON by writing tokens directly with <see cref="Utf8JsonWriter"/>.
/// No <c>JsonSerializer</c>, no reflection, no anonymous types — so the whole pipeline stays
/// Native-AOT and trim safe (zero IL2026/IL3050). This is the same zero-dependency, streaming
/// ethos as <see cref="CoberturaParser"/>: hand-written, allocation-light, statically analyzable.
/// <para>
/// The wire shape: camelCase keys, two-space indentation, and a trailing newline. Every document
/// opens with the same envelope — <c>schemaVersion</c> (<see cref="SchemaVersion"/>),
/// <c>command</c>, <c>inputs</c> (the reports merged), and <c>warnings</c> (the parser's
/// diagnostics) — whose keys are always present, empty arrays included. Inside it, a per-item
/// detail that is empty is omitted rather than written as <c>null</c> or <c>[]</c>: a file
/// without uncovered lines has no <c>uncovered</c> key. Strings use relaxed escaping: only what
/// JSON itself requires is escaped, so paths read as written (<c>Größe&lt;T&gt;.cs</c>, not
/// <c><</c> sequences).
/// </para>
/// </summary>
public static class JsonFormatter
{
    /// <summary>The version of the JSON shape every dotcov document opens with; raised on any breaking change.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,
        // Relaxed, not the default HTML-safe encoder: the output is never embedded in HTML, and
        // escaping '<', '&', '\'' and every non-ASCII character costs readers and tokens alike.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// The <c>report</c> document. With <paramref name="top"/>, <c>files</c> holds that many of
    /// the worst files (lowest line rate first, as the table orders them) and <c>moreFiles</c>
    /// counts the rest; without it, every file in the parser's document order.
    /// </summary>
    public static string Format(CoverageReport report, int? top = null) => Write(writer =>
    {
        WriteHeader(writer, "report", report.Inputs, report.Warnings);
        WriteReport(writer, report, top);
    });

    /// <summary>
    /// The gate document <c>check</c> and <c>test</c> print: the report document plus a
    /// <c>gate</c> object with the <paramref name="gate"/>'s outcome, its exit code
    /// (<see cref="ExitCodes.For"/>), the verdict line as <c>reason</c>, the rate and minimum of
    /// each dimension (a <c>null</c> minimum is a disabled dimension), and the first
    /// <paramref name="top"/> <see cref="CoverageReport.Offenders"/> with their uncovered line
    /// ranges and partially covered branches; <c>moreOffenders</c> counts the rest. A rate below
    /// its minimum floors at two decimals, so it never reads as the minimum it missed.
    /// </summary>
    public static string Format(
        CoverageReport report, GateResult gate, string command, int top = CoverageReport.DefaultOffenderLimit) => Write(writer =>
    {
        WriteHeader(writer, command, report.Inputs, report.Warnings);
        WriteGate(writer, report, gate, top);
        WriteReport(writer, report, top: null);
    });

    public static string FormatDiff(CoverageDiffResult diff) => Write(writer =>
    {
        WriteHeader(writer, "diff", diff.Inputs, diff.Warnings);

        writer.WriteStartObject("summary");
        if (diff.BeforeRate is { } bRate) writer.WriteNumber("before", Pct(bRate));
        if (diff.AfterRate is { } aRate) writer.WriteNumber("after", Pct(aRate));
        if (diff.Delta is { } dRate) writer.WriteNumber("delta", Pct(dRate));
        writer.WriteNumber("indirectLineChanges", diff.TotalLineChanges);
        writer.WriteEndObject();

        writer.WriteStartArray("files");
        foreach (var d in diff.Files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", d.Path);
            // Added files have no "before", removed files have no "after" — omit, don't null.
            if (d.Before.HasValue) writer.WriteNumber("before", Pct(d.Before.Value));
            if (d.After.HasValue) writer.WriteNumber("after", Pct(d.After.Value));
            if (d.Delta is { } fileDelta) writer.WriteNumber("delta", Pct(fileDelta));
            writer.WriteString("change", d.Change.ToString().ToLowerInvariant());
            if (d.LineChanges.Count > 0)
            {
                writer.WriteStartArray("lineChanges");
                foreach (var c in d.LineChanges) WriteLineDelta(writer, c);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });

    public static string FormatSnapshot(CoverageSnapshot snapshot) => Write(writer =>
    {
        // Inputs and warnings travel with the snapshot: remote sinks are exactly the consumers
        // the provenance and anomaly channels exist for.
        WriteHeader(writer, "snapshot", snapshot.Report.Inputs, snapshot.Report.Warnings);
        writer.WriteString("commit", snapshot.CommitSha);
        writer.WriteString("branch", snapshot.Branch);
        writer.WriteString("project", snapshot.Project);
        writer.WriteString("timestamp", snapshot.Timestamp);
        if (snapshot.FileHash is { } hash) writer.WriteString("fileHash", hash);
        WriteReport(writer, snapshot.Report, top: null);
    });

    /// <summary>
    /// The envelope every document opens with. Shared with <see cref="CrapFormatter.FormatJson"/>,
    /// so each command's document starts with the same four keys.
    /// </summary>
    internal static void WriteHeader(
        Utf8JsonWriter writer, string command, IReadOnlyList<string> inputs, IReadOnlyList<CoverageWarning> warnings)
    {
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("command", command);

        writer.WriteStartArray("inputs");
        foreach (var input in inputs) writer.WriteStringValue(input);
        writer.WriteEndArray();

        writer.WriteStartArray("warnings");
        foreach (var w in warnings)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", w.Kind.ToString());
            writer.WriteString("file", w.File);
            writer.WriteNumber("line", w.Line);
            writer.WriteString("detail", w.Detail);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteReport(Utf8JsonWriter writer, CoverageReport report, int? top)
    {
        writer.WritePropertyName("summary");
        WriteSummary(writer, report);

        // Files keep the parser's document order — machines sort as they need — unless --top
        // asks for the worst ones, which come in the order the table shows them.
        IEnumerable<FileCoverage> files = top is { } t ? report.Files.WorstFirst().Take(t) : report.Files;
        writer.WriteStartArray("files");
        foreach (var f in files) WriteFile(writer, f);
        writer.WriteEndArray();
        if (top is { } shown) writer.WriteNumber("moreFiles", Math.Max(0, report.Files.Count - shown));

        WriteExclusion(writer, report.Exclusion);
    }

    private static void WriteSummary(Utf8JsonWriter writer, CoverageReport report)
    {
        writer.WriteStartObject();
        // Omitted rather than zeroed when unmeasured: a consumer that sees no key can tell
        // it has no data; one that sees 0 cannot distinguish that from total failure.
        if (report.LineRate is { } lr) writer.WriteNumber("lineRate", Pct(lr));
        if (report.BranchRate is { } br) writer.WriteNumber("branchRate", Pct(br));
        writer.WriteBoolean("hasBranchData", report.HasBranchData);
        writer.WriteNumber("totalLines", report.TotalLines);
        writer.WriteNumber("coveredLines", report.TotalLinesHit);
        writer.WriteNumber("totalBranches", report.TotalBranches);
        writer.WriteNumber("coveredBranches", report.TotalBranchesHit);
        writer.WriteEndObject();
    }

    private static void WriteGate(Utf8JsonWriter writer, CoverageReport report, GateResult gate, int top)
    {
        writer.WriteStartObject("gate");
        writer.WriteString("outcome", gate.Outcome.ToString().ToLowerInvariant());
        writer.WriteNumber("exitCode", ExitCodes.For(gate.Outcome));
        writer.WriteString("reason", gate.ToString());
        WriteDimension(writer, "line", gate.LineRate, gate.MinLinePercent, gate.LineBelowThreshold);
        WriteDimension(writer, "branch", gate.BranchRate, gate.MinBranchPercent, gate.BranchBelowThreshold);

        var offenders = report.Offenders(gate);
        writer.WriteStartArray("offenders");
        foreach (var f in offenders.Take(top))
        {
            writer.WriteStartObject();
            writer.WriteString("path", f.Path);
            WriteRate(writer, "lineRate", f.LineRate, OffenderText.LineFloors(f, gate));
            WriteRate(writer, "branchRate", f.BranchRate, OffenderText.BranchFloors(f, gate));
            writer.WriteString("uncovered", LineRanges.Format(f.UncoveredLines));
            WritePartialBranches(writer, f.PartialBranches);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteNumber("moreOffenders", Math.Max(0, offenders.Count - top));
        writer.WriteEndObject();
    }

    /// <summary>One gate dimension: its rate (null when unmeasured) and minimum (null when the dimension is disabled).</summary>
    private static void WriteDimension(Utf8JsonWriter writer, string name, double? rate, double minPercent, bool below)
    {
        writer.WriteStartObject(name);
        WriteRate(writer, "rate", rate, below);
        // "Not above 0" rather than "== 0", so a NaN minimum — which JSON cannot spell — is a
        // disabled dimension too.
        if (minPercent > 0) writer.WriteNumber("min", minPercent);
        else writer.WriteNull("min");
        writer.WriteEndObject();
    }

    private static void WriteRate(Utf8JsonWriter writer, string name, double? rate, bool floor)
    {
        if (rate is not { } r) writer.WriteNull(name);
        else writer.WriteNumber(name, floor ? GateResult.FloorToHundredth(r * 100) : Pct(r));
    }

    /// <summary>
    /// What an active exclusion left out: <c>excluded</c> (the removed paths) and
    /// <c>exclusionRules</c> (the patterns applied). Both keys are absent when no exclusion was
    /// applied and present — <c>excluded</c> possibly empty — whenever one was, so a consumer can
    /// tell "nothing excluded" from "nothing matched".
    /// </summary>
    internal static void WriteExclusion(Utf8JsonWriter writer, FileExclusion exclusion)
    {
        if (!exclusion.IsActive) return;

        writer.WriteStartArray("excluded");
        foreach (var path in exclusion.ExcludedFiles) writer.WriteStringValue(path);
        writer.WriteEndArray();

        writer.WriteStartArray("exclusionRules");
        foreach (var pattern in exclusion.Patterns) writer.WriteStringValue(pattern);
        writer.WriteEndArray();
    }

    private static void WriteFile(Utf8JsonWriter writer, FileCoverage f)
    {
        writer.WriteStartObject();
        writer.WriteString("path", f.Path);
        if (f.LineRate is { } flr) writer.WriteNumber("lineRate", Pct(flr));
        if (f.BranchRate is { } fbr) writer.WriteNumber("branchRate", Pct(fbr));
        writer.WriteNumber("linesHit", f.LinesHit);
        writer.WriteNumber("linesTotal", f.LinesTotal);
        writer.WriteNumber("branchesHit", f.BranchesHit);
        writer.WriteNumber("branchesTotal", f.BranchesTotal);
        if (f.UncoveredLines.Count > 0) writer.WriteString("uncovered", LineRanges.Format(f.UncoveredLines));
        if (f.PartialBranches.Count > 0) WritePartialBranches(writer, f.PartialBranches);
        writer.WriteEndObject();
    }

    private static void WritePartialBranches(Utf8JsonWriter writer, IReadOnlyList<BranchDetail> branches)
    {
        writer.WriteStartArray("partialBranches");
        foreach (var b in branches)
        {
            writer.WriteStartObject();
            writer.WriteNumber("line", b.Line);
            writer.WriteNumber("covered", b.Covered);
            writer.WriteNumber("total", b.Total);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    // Visitor dispatch over the closed LineDelta hierarchy. Wire format pins all-lowercase
    // `change` strings (added/removed/newlyhit/newlymissed) and field omission: Added carries
    // no `beforeHits`, Removed carries no `afterHits`. Switch is the compile-time-exhaustive
    // entry point — a fifth variant breaks this call at compile time.
    private static void WriteLineDelta(Utf8JsonWriter writer, LineDelta c) => c.Switch(
        added: a =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("line", a.Line);
            writer.WriteNumber("afterHits", a.AfterHits);
            writer.WriteString("change", "added");
            writer.WriteEndObject();
        },
        removed: r =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("line", r.Line);
            writer.WriteNumber("beforeHits", r.BeforeHits);
            writer.WriteString("change", "removed");
            writer.WriteEndObject();
        },
        newlyHit: h =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("line", h.Line);
            writer.WriteNumber("beforeHits", h.BeforeHits);
            writer.WriteNumber("afterHits", h.AfterHits);
            writer.WriteString("change", "newlyhit");
            writer.WriteEndObject();
        },
        newlyMissed: m =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("line", m.Line);
            writer.WriteNumber("beforeHits", m.BeforeHits);
            writer.WriteNumber("afterHits", m.AfterHits);
            writer.WriteString("change", "newlymissed");
            writer.WriteEndObject();
        });

    /// <summary>
    /// One document: <paramref name="body"/> writes its properties into the root object. Same
    /// buffer→string path JsonSerializer uses internally — UTF-8 into an ArrayBufferWriter,
    /// decoded once, no BOM, no intermediate Stream — closed with a newline, so the document
    /// ends like any other line of output. Shared with <see cref="CrapFormatter.FormatJson"/>.
    /// </summary>
    internal static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan) + Environment.NewLine;
    }

    private static double Pct(double rate) => Math.Round(rate * 100, 2, MidpointRounding.ToEven);
}
