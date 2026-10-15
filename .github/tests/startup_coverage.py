#!/usr/bin/env python3
"""Exercise the tool's startup finalization with real MSTest/Microsoft coverage."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET


def verify(tool: Path, output: Path) -> None:
    output.mkdir(parents=True)
    # The fixture is a consuming repository, even when CI retains it under artifacts/.
    # Stop MSBuild from importing DotCov's own package and analyzer configuration.
    for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"):
        (output / name).write_text("<Project />")
    root = Path(__file__).resolve().parents[2]
    sdk = json.loads((root / "global.json").read_text())["sdk"]
    packages = ET.parse(root / "Directory.Packages.props").getroot()
    mstest = packages.findtext("./PropertyGroup/MSTestVersion")
    framework = ET.parse(root / "Directory.Build.props").findtext("./PropertyGroup/TargetFramework")
    (output / "global.json").write_text(json.dumps({
        "sdk": sdk, "test": {"runner": "Microsoft.Testing.Platform"},
        "msbuild-sdks": {"MSTest.Sdk": mstest},
    }, indent=2))
    (output / "NuGet.Config").write_text(
        '<configuration><packageSources><clear/><add key="nuget.org" '
        'value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
    previous = output / "previous hook"
    previous.mkdir()
    (previous / "PreviousHook.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework>'
        '</PropertyGroup></Project>')
    (previous / "StartupHook.cs").write_text("""
public static class StartupHook
{
    public static void Initialize() =>
        System.Environment.SetEnvironmentVariable("DOTCOV_PREVIOUS_HOOK", "present");
}
""")
    app = output / "StartupProbe"
    app.mkdir()
    (app / "StartupProbe.csproj").write_text(f"""
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
  <TargetFramework>{framework}</TargetFramework><OutputType>Exe</OutputType>
  <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
</PropertyGroup></Project>
""")
    (app / "Program.cs").write_text("""
Console.WriteLine("previous-hook=" + Environment.GetEnvironmentVariable("DOTCOV_PREVIOUS_HOOK"));
Startup.Run(args[0]);
""")
    (app / "Startup.cs").write_text("""
public static class Startup
{
    public static void Run(string mode)
    {
        if (mode == "fail") throw new InvalidOperationException("startup failure sentinel");
        Console.WriteLine("startup ready");
    }
}
""")
    tests = output / "StartupProbe.Tests"
    tests.mkdir()
    (tests / "StartupProbe.Tests.csproj").write_text(f"""
<Project Sdk="MSTest.Sdk"><PropertyGroup><TargetFramework>{framework}</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><ProjectReference Include="../StartupProbe/StartupProbe.csproj"/></ItemGroup></Project>
""")
    (tests / "StartupTests.cs").write_text("""
using System.Diagnostics;

[TestClass]
public sealed class StartupTests(TestContext context)
{
    [TestMethod]
    [DataRow("success")]
    [DataRow("fail")]
    public async Task StartupKeepsItsOutcomeAndDiagnostic(string mode)
    {
        Assert.AreEqual("present", Environment.GetEnvironmentVariable("DOTCOV_PREVIOUS_HOOK"));
        using var child = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList = { typeof(Startup).Assembly.Location, mode },
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start startup probe.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync(context.CancellationToken);
            var stdout = await output;
            var stderr = await error;
            context.WriteLine($"CHILD {mode}: exit={child.ExitCode}\\n{stdout}\\n{stderr}");
            Assert.Contains("previous-hook=present", stdout);
            if (mode == "fail")
            {
                Assert.AreNotEqual(0, child.ExitCode);
                Assert.Contains("startup failure sentinel", stderr);
                Assert.Contains("Unhandled exception", stderr);
            }
            else
            {
                Assert.AreEqual(0, child.ExitCode, stderr);
                Assert.Contains("startup ready", stdout);
            }
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            await Task.WhenAll(output, error);
        }
    }
}
""")
    (output / "coverage.config").write_text(r"""
<Configuration><IncludeTestAssembly>False</IncludeTestAssembly><CodeCoverage>
  <ModulePaths><Include><ModulePath>.*[\\/](StartupProbe)\.dll$</ModulePath></Include></ModulePaths>
  <CollectFromChildProcesses>True</CollectFromChildProcesses>
  <EnableStaticManagedInstrumentation>True</EnableStaticManagedInstrumentation>
  <EnableStaticManagedInstrumentationRestore>True</EnableStaticManagedInstrumentationRestore>
</CodeCoverage></Configuration>
""")
    environment = os.environ.copy()
    for key in list(environment):
        if key.upper().startswith("TESTINGPLATFORM_") or key in (
                "DOTNET_TEST_RUNNER", "MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBUILD_EXE_PATH",
                "GITHUB_STEP_SUMMARY"):
            environment.pop(key, None)
    environment.update({"UseSharedCompilation": "false", "MSBUILDDISABLENODEREUSE": "1"})
    commands = []

    def run(label, argv):
        commands.append(argv)
        completed = subprocess.run(argv, cwd=output, env=environment, capture_output=True, text=True, timeout=180)
        (output / f"{label}.stdout").write_text(completed.stdout)
        (output / f"{label}.stderr").write_text(completed.stderr)
        (output / "commands.json").write_text(json.dumps(commands, indent=2))
        if completed.returncode:
            raise RuntimeError(f"{label} exited {completed.returncode}: {completed.stdout[-3000:]} {completed.stderr[-3000:]}")
        return completed

    run("previous-hook-build", ["dotnet", "build", str(previous / "PreviousHook.csproj"), "-c", "Release", "--nologo"])
    prior_hook = previous / "bin/Release/netstandard2.0/PreviousHook.dll"
    environment["DOTNET_STARTUP_HOOKS"] = os.pathsep.join(filter(None, [environment.get("DOTNET_STARTUP_HOOKS"), str(prior_hook)]))
    tool_command = ["dotnet", str(tool)] if tool.suffix.lower() == ".dll" else [str(tool)]
    completed = run("dotcov-test", tool_command + [
        "test", str(tests / "StartupProbe.Tests.csproj"), "--min-line", "100", "--min-branch", "100",
        "--format", "json", "--", "-c", "Release", "--minimum-expected-tests", "2", "--report-trx",
        "--coverage-settings", str(output / "coverage.config"), "--coverage-output", "coverage.cobertura.xml",
        "--no-ansi", "--progress", "off",
    ])
    report = json.loads(completed.stdout)
    summary = report["summary"]
    assert (summary["coveredBranches"], summary["totalBranches"]) == (2, 2), report
    assert summary["coveredLines"] == summary["totalLines"] > 0, report
    assert report["warnings"] == [], report
    assert completed.stderr.startswith("PASS:"), completed.stderr
    raw = list(output.glob("TestResults/*/coverage.cobertura.xml"))
    assert len(raw) == 1, raw
    coverage = ET.parse(raw[0]).getroot()
    measured = coverage.findall("./packages/package")
    assert [package.get("name") for package in measured] == ["StartupProbe"], [p.attrib for p in measured]
    branches = [line for cls in coverage.findall(".//class")
                if cls.get("filename", "").replace("\\", "/").endswith("/Startup.cs")
                for line in cls.findall("./lines/line") if line.get("branch", "").lower() == "true"]
    assert len(branches) == 1 and branches[0].get("condition-coverage") == "100% (2/2)", [line.attrib for line in branches]
    trx = list(output.glob("TestResults/*/*.trx"))
    assert len(trx) == 1, trx
    results = ET.parse(trx[0]).getroot()
    ns = {"t": results.tag.split("}")[0][1:]}
    counters = results.find(".//t:Counters", ns).attrib
    assert counters["total"] == counters["passed"] == "2" and counters["failed"] == "0", counters
    evidence = {"tool": str(tool), "summary": summary, "rawReport": str(raw[0]), "tests": counters,
                "previousHookRetained": True, "originalFailureAsserted": True}
    (output / "verification.json").write_text(json.dumps(evidence, indent=2))
    print(json.dumps(evidence, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    verify(args.tool.resolve(), args.output.resolve())
