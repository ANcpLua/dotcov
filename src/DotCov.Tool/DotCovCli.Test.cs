using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using DotCov.Formatters;

namespace DotCov.Tool;

/// <summary>
/// `dotcov test`: one step from `dotnet test` to a coverage verdict. It runs the tests with
/// Microsoft Code Coverage writing Cobertura into a results directory of its own, then renders
/// and gates exactly that run's reports with the same gate as check.
/// </summary>
public static partial class DotCovCli
{
    const string TestingPlatformRunner = "Microsoft.Testing.Platform";

    static readonly JsonDocumentOptions GlobalJsonOptions = new() { CommentHandling = JsonCommentHandling.Skip };

    static async Task<int> Test(
        Dictionary<string, string> opts, string[] forwarded, TextWriter stdout, TextWriter stderr, bool color)
    {
        // Flag values are checked before anything runs: a typo, or a missing threshold, must not
        // cost a test run.
        if (!TryGetThresholds("test", opts, stderr, out var minLine, out var minBranch)) return UsageExitCode;
        if (!TryGetGateOutput(opts, stderr, out var output)) return UsageExitCode;
        if (!TryGetBadgeDir(opts, stderr)) return UsageExitCode;

        // A fresh directory per run, so the gate reads this run's reports and nothing an earlier
        // run left behind. Created up front: a run that writes no report is NODATA, not a
        // missing path. Named relative to the working directory after the verdict.
        var run = Path.Combine("TestResults", Guid.NewGuid().ToString("N"));
        var results = Directory.CreateDirectory(run).FullName;

        // JSON and markdown keep stdout to the document and stderr to the verdict, so the dotnet
        // test log is held back and shown only when the run fails.
        var log = output.Format is "table" ? stdout : new StringWriter();
        var exitCode = await RunDotNet(
            TestArguments(opts.GetValueOrDefault("file"), results, forwarded), log, opts.ContainsKey("no-color"));

        // Coverage from a failed run measures tests that did not pass; it is not evaluated. Its
        // own exit code keeps a failing test apart from a report dotcov could not read.
        if (exitCode is not 0)
        {
            await stderr.WriteLineAsync($"error: dotnet test exited with code {exitCode}; coverage was not evaluated.");
            if (log is StringWriter held) await stderr.WriteAsync(held.ToString());
            return TestRunFailedExitCode;
        }

        var (merged, inputs) = ParseInput(results, ReportPattern.Default, DefaultMaxChars, Identity(opts));
        var report = ApplyExclusions(merged, opts);
        // The table is rendered from the same evaluation as the verdict, so a failing dimension's
        // TOTAL floors like the verdict does: 19999/20000 against --min-line 100 reads 99.9%
        // above "FAIL: line 99.9% (min 100%)", never 100.0%.
        var gate = report.Evaluate(minLine, minBranch);
        if (output.Format is "table")
            await stdout.WriteAsync(TableFormatter.Format(report, gate, color));
        return await Gate("test", report, gate, results, pattern: null, inputs, output, opts, stdout, stderr,
            reports: $"reports: {run}");
    }

    /// <summary>
    /// The `dotnet test` arguments for the runner it will use here. MTP takes the target through
    /// --project or --solution and coverage through the Microsoft.Testing.Extensions.CodeCoverage
    /// options; VSTest takes the target positionally and coverage through the Code Coverage data
    /// collector. Both write Cobertura, and forwarded arguments follow unchanged.
    /// </summary>
    static List<string> TestArguments(string? target, string results, string[] forwarded)
    {
        var testingPlatform = UsesTestingPlatform(Environment.CurrentDirectory);

        List<string> arguments = ["test"];
        if (target is not null)
        {
            if (testingPlatform)
                arguments.Add(Path.GetExtension(target) is ".sln" or ".slnx" or ".slnf" ? "--solution" : "--project");
            arguments.Add(target);
        }

        arguments.AddRange(["--results-directory", results]);
        arguments.AddRange(testingPlatform
            ? ["--coverage", "--coverage-output-format", "cobertura"]
            : ["--collect", "Code Coverage;Format=cobertura"]);
        arguments.AddRange(forwarded);
        return arguments;
    }

    /// <summary>
    /// Whether `dotnet test` runs Microsoft.Testing.Platform here, decided as the SDK decides it
    /// (TestCommandDefinition): a recognized DOTNET_TEST_RUNNER wins, then the test.runner of the
    /// nearest global.json from <paramref name="directory"/> up, then VSTest.
    /// </summary>
    static bool UsesTestingPlatform(string directory)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("DOTNET_TEST_RUNNER")?.Trim();
        if (string.Equals(fromEnvironment, "VSTest", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(fromEnvironment, TestingPlatformRunner, StringComparison.OrdinalIgnoreCase)) return true;

        return string.Equals(GlobalJsonRunner(directory), TestingPlatformRunner, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// test.runner of the nearest global.json, or null. Like the SDK, only the nearest file counts,
    /// and a file that is unreadable, malformed, or lacks a string test.runner selects no runner.
    /// </summary>
    static string? GlobalJsonRunner(string? directory)
    {
        for (; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            var path = Path.Combine(directory, "global.json");
            if (!File.Exists(path)) continue;

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path), GlobalJsonOptions);
                return document.RootElement.GetProperty("test").GetProperty("runner").GetString();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
                                           or IOException or UnauthorizedAccessException)
            {
                // KeyNotFoundException: no test or runner property. InvalidOperationException: one
                // of them has another JSON type. Either way the SDK falls back to VSTest.
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs dotnet with <paramref name="arguments"/> and copies both of its streams to
    /// <paramref name="log"/> line by line: stdout for the table, so dotcov's stderr still leads
    /// with the verdict; stderr for JSON and markdown, so stdout holds only the document.
    /// The command line is echoed first. $DOTNET_HOST_PATH, which the SDK sets for the tools it
    /// runs, selects the same dotnet; otherwise dotnet comes from PATH. --no-color reaches it as
    /// NO_COLOR=1, so its output is as plain as dotcov's.
    /// </summary>
    static async Task<int> RunDotNet(List<string> arguments, TextWriter log, bool noColor)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        await log.WriteLineAsync($"> {string.Join(' ', arguments.Prepend(dotnet).Select(Quote))}");

        var start = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (noColor) start.Environment["NO_COLOR"] = "1";

        using var process = new Process { StartInfo = start };

        // The two streams raise their events on different threads; a TextWriter is not thread-safe.
        var sync = new Lock();
        void Copy(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (sync) log.WriteLine(e.Data);
        }

        process.OutputDataReceived += Copy;
        process.ErrorDataReceived += Copy;

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new CliError(ex.Message, ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Also waits until both streams reach end of file, so every line is copied on return.
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    /// <summary>Display quoting for the echoed command line, not shell escaping.</summary>
    static string Quote(string argument) => argument.Contains(' ') ? $"\"{argument}\"" : argument;
}
