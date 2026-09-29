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
        // Flag values are checked before anything runs: a typo must not cost a test run.
        if (!TryParsePercent("min-line", opts.GetValueOrDefault("min-line", "80"), stderr, out var minLine))
            return 1;

        if (!TryParsePercent("min-branch", opts.GetValueOrDefault("min-branch", "0"), stderr, out var minBranch))
            return 1;

        // A fresh directory per run, so the gate reads this run's reports and nothing an earlier
        // run left behind. Created up front: a run that writes no report is NODATA, not a
        // missing path.
        var results = Directory.CreateDirectory(Path.Combine("TestResults", Guid.NewGuid().ToString("N"))).FullName;

        var exitCode = await RunDotNet(TestArguments(opts.GetValueOrDefault("file"), results, forwarded), stdout);

        // Coverage from a failed run measures tests that did not pass; it is not evaluated.
        if (exitCode is not 0)
            throw new CliError($"dotnet test exited with code {exitCode}; coverage was not evaluated.");

        var (merged, inputs) = ParseInput(results, ReportPattern.Default, DefaultMaxChars);
        var report = ApplyExclusions(merged, opts);
        await stdout.WriteAsync(TableFormatter.Format(report, color));
        return await Gate(report, minLine, minBranch, results, inputs, opts, stdout, stderr);
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
    /// <paramref name="stdout"/> line by line, so dotcov's stderr still leads with the verdict.
    /// The command line is echoed first. $DOTNET_HOST_PATH, which the SDK sets for the tools it
    /// runs, selects the same dotnet; otherwise dotnet comes from PATH.
    /// </summary>
    static async Task<int> RunDotNet(List<string> arguments, TextWriter stdout)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        await stdout.WriteLineAsync($"> {string.Join(' ', arguments.Prepend(dotnet).Select(Quote))}");

        var start = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };

        // The two streams raise their events on different threads; a TextWriter is not thread-safe.
        var sync = new Lock();
        void Copy(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (sync) stdout.WriteLine(e.Data);
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
