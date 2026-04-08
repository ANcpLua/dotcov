using DotCov;
using DotCov.Formatters;

var (command, options) = ParseArgs(args);

return command switch
{
    "report" => await Report(options),
    "check" => await Check(options),
    "diff" => Diff(options),
    "snapshot" => await Snapshot(options),
    "version" => Version(),
    _ => Help()
};

static async Task<int> Report(Dictionary<string, string> opts)
{
    if (!opts.TryGetValue("file", out var path))
    {
        Console.Error.WriteLine("Usage: dotcov report <path> [--format table|json|md] [--threshold N] [--exclude-generated]");
        return 1;
    }

    var report = ApplyExclusions(CoberturaParser.ParsePath(path), opts);
    var format = opts.GetValueOrDefault("format", "table");
    var threshold = opts.TryGetValue("threshold", out var t) ? double.Parse(t) : (double?)null;

    var output = format switch
    {
        "json" => JsonFormatter.Format(report),
        "markdown" or "md" => MarkdownFormatter.Format(report, threshold),
        _ => TableFormatter.Format(report)
    };

    Console.Write(output);

    if (opts.ContainsKey("github-summary"))
        WriteGitHubSummary(MarkdownFormatter.Format(report, threshold));

    return await MaybeUpload(opts, JsonFormatter.Format(report));
}

static async Task<int> Check(Dictionary<string, string> opts)
{
    if (!opts.TryGetValue("file", out var path))
    {
        Console.Error.WriteLine("Usage: dotcov check <path> --min-line N [--min-branch N] [--exclude-generated]");
        return 1;
    }

    var report = ApplyExclusions(CoberturaParser.ParsePath(path), opts);
    var minLine = double.Parse(opts.GetValueOrDefault("min-line", "80"));
    var minBranch = double.Parse(opts.GetValueOrDefault("min-branch", "0"));

    if (report.MeetsThreshold(minLine, minBranch))
    {
        Console.WriteLine($"PASS: line {report.LineRate * 100:F1}% >= {minLine}%, branch {report.BranchRate * 100:F1}% >= {minBranch}%");
        return await MaybeUpload(opts, JsonFormatter.Format(report));
    }

    Console.Error.WriteLine($"FAIL: line {report.LineRate * 100:F1}% < {minLine}% or branch {report.BranchRate * 100:F1}% < {minBranch}%");

    foreach (var f in report.BelowPercent(minLine))
        Console.Error.WriteLine($"  {f.Path}: {f.LineRate * 100:F1}%");

    if (opts.ContainsKey("github-summary"))
        WriteGitHubSummary(MarkdownFormatter.Format(report, minLine));

    return 1;
}

static int Diff(Dictionary<string, string> opts)
{
    if (!opts.TryGetValue("before", out var before) || !opts.TryGetValue("after", out var after))
    {
        Console.Error.WriteLine("Usage: dotcov diff <before> <after> [--format table|json|md]");
        return 1;
    }

    var result = CoverageDiff.Compare(
        CoberturaParser.ParsePath(before),
        CoberturaParser.ParsePath(after));

    var format = opts.GetValueOrDefault("format", "table");

    Console.Write(format switch
    {
        "json" => JsonFormatter.FormatDiff(result),
        "markdown" or "md" => MarkdownFormatter.FormatDiff(result),
        _ => TableFormatter.FormatDiff(result)
    });

    return 0;
}

static async Task<int> Snapshot(Dictionary<string, string> opts)
{
    if (!opts.TryGetValue("file", out var path))
    {
        Console.Error.WriteLine("Usage: dotcov snapshot <path> --commit <sha> --branch <branch> --project <name>");
        return 1;
    }

    var report = ApplyExclusions(CoberturaParser.ParsePath(path), opts);
    var fileHash = File.Exists(path) ? FileHasher.ComputeHash(path) : null;

    var snapshot = new CoverageSnapshot(
        CommitSha: opts.GetValueOrDefault("commit", "unknown"),
        Branch: opts.GetValueOrDefault("branch", "unknown"),
        Project: opts.GetValueOrDefault("project", "unknown"),
        Timestamp: TimeProvider.System.GetUtcNow(),
        FileHash: fileHash,
        Report: report);

    var json = JsonFormatter.FormatSnapshot(snapshot);
    Console.Write(json);

    return await MaybeUpload(opts, json);
}

static int Version()
{
    Console.WriteLine($"dotcov {typeof(CoberturaParser).Assembly.GetName().Version}");
    return 0;
}

static int Help()
{
    Console.WriteLine("""
        dotcov - Cobertura coverage toolkit

        Commands:
          report   <path> [--format table|json|md] [--threshold N]    Parse and display coverage
          check    <path> --min-line N [--min-branch N]               CI gate (exit 1 if below)
          diff     <before> <after> [--format table|json|md]          Compare two reports
          snapshot <path> --commit SHA --branch B --project P         Create pipeline-ready JSON
          version                                                     Show version

        Global flags:
          --exclude-generated       Skip generated files, migrations, state machines
          --upload <url>            POST JSON payload to any endpoint
          --github-summary          Write markdown to $GITHUB_STEP_SUMMARY

        <path> can be a file or directory. Directories are scanned for **/coverage.cobertura.xml.

        Examples:
          dotcov report TestResults/
          dotcov report coverage.cobertura.xml --format json --exclude-generated > coverage.json
          dotcov check TestResults/ --min-line 80 --exclude-generated --github-summary
          dotcov snapshot TestResults/ --commit abc123 --branch main --project MyApp --upload https://qyl/api/v1/coverage
          dotcov diff before.cobertura.xml after.cobertura.xml --format md
        """);
    return 0;
}

// ── Shared helpers ──

static CoverageReport ApplyExclusions(CoverageReport report, Dictionary<string, string> opts) =>
    opts.ContainsKey("exclude-generated") ? report.Exclude(ExclusionRules.WellKnown) : report;

static void WriteGitHubSummary(string markdown)
{
    var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
    if (summaryPath is null) return;
    File.AppendAllText(summaryPath, markdown);
}

static async Task<int> MaybeUpload(Dictionary<string, string> opts, string json)
{
    if (!opts.TryGetValue("upload", out var url)) return 0;

    using var http = new HttpClient();
    var response = await http.PostAsync(url,
        new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    if (response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"Uploaded to {url} ({response.StatusCode})");
        return 0;
    }

    Console.Error.WriteLine($"Upload failed: {url} ({response.StatusCode})");
    return 1;
}

static (string command, Dictionary<string, string> options) ParseArgs(string[] raw)
{
    if (raw.Length is 0) return ("help", []);

    var command = raw[0];
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? pendingKey = null;
    var positional = 0;

    for (var i = 1; i < raw.Length; i++)
    {
        if (raw[i].StartsWith("--"))
        {
            if (pendingKey is not null) parsed[pendingKey] = "true";
            pendingKey = raw[i][2..];
        }
        else if (pendingKey is not null)
        {
            parsed[pendingKey] = raw[i];
            pendingKey = null;
        }
        else
        {
            var key = positional switch
            {
                0 => command is "diff" ? "before" : "file",
                1 => "after",
                _ => $"arg{positional}"
            };
            parsed[key] = raw[i];
            positional++;
        }
    }

    if (pendingKey is not null) parsed[pendingKey] = "true";

    return (command, parsed);
}
