using System.Buffers;
using System.Text;
using System.Text.Json;
using System;

namespace DotCov.Formatters;

/// <summary>
/// Coverage reports rendered as JSON by writing tokens directly with <see cref="Utf8JsonWriter"/>.
/// No <c>JsonSerializer</c>, no reflection, no anonymous types — so the whole pipeline stays
/// Native-AOT and trim safe (zero IL2026/IL3050). This is the same zero-dependency, streaming
/// ethos as <see cref="CoberturaParser"/>: hand-written, allocation-light, statically analyzable.
/// <para>
/// The wire shape is unchanged from the previous reflection-based projection: camelCase keys,
/// two-space indentation, and the "absent key == clean" contract — a null/empty property is
/// omitted entirely (so consumers detect e.g. a warning-free report with
/// <c>!root.TryGetProperty("warnings", out _)</c>) rather than emitted as <c>null</c>.
/// </para>
/// </summary>
public static class JsonFormatter
{
    private static readonly JsonWriterOptions Options = new() { Indented = true };

    public static string Format(CoverageReport report) => Write(writer =>
    {
        writer.WriteStartObject();

        writer.WritePropertyName("summary");
        WriteSummary(writer, report);

        writer.WriteStartArray("files");
        foreach (var f in report.Files) WriteFile(writer, f);
        writer.WriteEndArray();

        // Omitted entirely when empty — same `Count == 0 ? null : …` shape the WhenWritingNull
        // policy used to give, so consumers can treat a missing "warnings" key as "clean report".
        if (report.Warnings.Count > 0)
        {
            writer.WriteStartArray("warnings");
            foreach (var w in report.Warnings)
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

        writer.WriteEndObject();
    });

    public static string FormatDiff(CoverageDiffResult diff) => Write(writer =>
    {
        writer.WriteStartObject();

        writer.WriteStartObject("summary");
        writer.WriteNumber("before", Pct(diff.BeforeRate));
        writer.WriteNumber("after", Pct(diff.AfterRate));
        writer.WriteNumber("delta", Pct(diff.Delta));
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
            writer.WriteNumber("delta", Pct(d.Delta));
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

        writer.WriteEndObject();
    });

    public static string FormatSnapshot(CoverageSnapshot snapshot) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("commit", snapshot.CommitSha);
        writer.WriteString("branch", snapshot.Branch);
        writer.WriteString("project", snapshot.Project);
        writer.WriteString("timestamp", snapshot.Timestamp);
        if (snapshot.FileHash is { } hash) writer.WriteString("fileHash", hash);

        writer.WritePropertyName("summary");
        WriteSummary(writer, snapshot.Report);

        writer.WriteStartArray("files");
        foreach (var f in snapshot.Report.Files) WriteFile(writer, f);
        writer.WriteEndArray();

        writer.WriteEndObject();
    });

    private static void WriteSummary(Utf8JsonWriter writer, CoverageReport report)
    {
        writer.WriteStartObject();
        writer.WriteNumber("lineRate", Pct(report.LineRate));
        if (report.HasBranchData) writer.WriteNumber("branchRate", Pct(report.BranchRate));
        writer.WriteBoolean("hasBranchData", report.HasBranchData);
        writer.WriteNumber("totalLines", report.TotalLines);
        writer.WriteNumber("coveredLines", report.TotalLinesHit);
        writer.WriteNumber("totalBranches", report.TotalBranches);
        writer.WriteNumber("coveredBranches", report.TotalBranchesHit);
        writer.WriteEndObject();
    }

    private static void WriteFile(Utf8JsonWriter writer, FileCoverage f)
    {
        writer.WriteStartObject();
        writer.WriteString("path", f.Path);
        writer.WriteNumber("lineRate", Pct(f.LineRate));
        if (f.HasBranchData) writer.WriteNumber("branchRate", Pct(f.BranchRate));
        writer.WriteNumber("linesHit", f.LinesHit);
        writer.WriteNumber("linesTotal", f.LinesTotal);
        writer.WriteNumber("branchesHit", f.BranchesHit);
        writer.WriteNumber("branchesTotal", f.BranchesTotal);
        if (f.UncoveredLines.Count > 0)
        {
            writer.WriteStartArray("uncoveredLines");
            foreach (var line in f.UncoveredLines) writer.WriteNumberValue(line);
            writer.WriteEndArray();
        }
        if (f.PartialBranches.Count > 0)
        {
            writer.WriteStartArray("partialBranches");
            foreach (var b in f.PartialBranches)
            {
                writer.WriteStartObject();
                writer.WriteNumber("line", b.Line);
                writer.WriteNumber("covered", b.Covered);
                writer.WriteNumber("total", b.Total);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    // Visitor dispatch over the closed LineDelta hierarchy. Wire format pins all-lowercase
    // `change` strings (added/removed/newlyhit/newlymissed) and the same field omission as the
    // prior projection: Added carries no `beforeHits`, Removed carries no `afterHits`. Switch is
    // the compile-time-exhaustive entry point — a fifth variant breaks this call at compile time.
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

    // Same buffer→string path JsonSerializer uses internally: write UTF-8 into a pooled
    // ArrayBufferWriter, then decode once. No BOM, no intermediate Stream.
    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Options))
            body(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static double Pct(double rate) => Math.Round(rate * 100, 2, MidpointRounding.ToEven);
}
