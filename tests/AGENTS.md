# Tests, coverage, and Fallout

The suite uses **MSTest 4.5.1** on **Microsoft.Testing.Platform**, selected by
`global.json`. .NET SDK 10 accepts native platform options directly after
`dotnet test`; no additional `--` separator is required.

## Commands

Use the [Fallout targets](../src/DotCov.Fallout/AGENTS.md) for routine verification.
Direct runner commands are available for discovery and focused diagnostics:

```sh
dotnet test --project tests/DotCov.Tests
dotnet test --project tests/DotCov.Tests --filter "FullyQualifiedName~DotCov.Tests.ReportPatternTests"
dotnet test --project tests/DotCov.Tests --filter "TestCategory=Integration"
dotnet test --project tests/DotCov.Tests --list-tests
```

The Fallout `--filter` parameter forwards the native MSTest test-case expression.
Discover new cases before executing them. Verify the intended scenarios and
their data rows, rather than preserving a historical test count. Branch-origin regressions also cover captured
Microsoft reports, same-line owners, summary deduplication and merge order.
Use `--report-trx` for structured execution results; resolve additional options
from the actual runner's `--help` rather than assuming another runner's flags.

MTP exit codes include **5** for invalid arguments, **8** for no tests,
**9** for fewer than the required tests, and **13** for reaching a failure limit.
For Fallout, inspect the child-process exit in the log; the wrapper may return
another failure code. See [MTP diagnostics](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-troubleshooting).

## Coverage

Coverage remains **coverlet.MTP** (`--coverlet`), enabled with `-p:Coverage=true`.
The arguments live only in [DotCov.Tests.csproj](DotCov.Tests/DotCov.Tests.csproj),
in `TestingPlatformCommandLineArguments`. Fallout and CI enable that property.
Normal runs remain uninstrumented. Do not also enable the separately registered
Microsoft provider's `--coverage` switch when measuring DotCov itself.

```sh
dotnet tool restore
dotnet fallout Coverage
dotnet test --project tests/DotCov.Tests -p:Coverage=true
```

The wrapper writes each measurement into `TestResults/<run-id>` and reads only
that directory. With `--reports`, supply a specific file or single-run directory,
not a tree of old runs. Include/exclude filters are the measurement contract and
must match CI. Every shipping assembly must appear, with 100% of lines and
branches measured and covered. Add tests through public entry points for gaps;
never remove code from measurement to pass. The default report pattern finds
classic and timestamped Cobertura reports; `coverage.xml` needs an explicit pattern.

## Test contracts

- The assembly explicitly runs tests in parallel at method scope. Tests that
  read or write process environment state use
  `[ResourceLock(ProcessState.Environment)]`; the key is MSTest's
  `WellKnownResources.EnvironmentVariables`. Preserve participation of both
  readers and writers. Use `EnvScope` / `CultureScope` for restoration.
- Use native `[TestClass]`, `[TestMethod]`, `[DataRow]`, `[DynamicData]`, and
  `[CombinatorialData]`. Prefer typed tuples or `TestDataRow<T>` for computed
  scenarios; use row metadata for readable names. Introduce a custom data-source
  attribute only when the built-in APIs cannot express the required behavior.
  Discovery must not open streams, start servers, or acquire mutable resources.
- Choose assertions for their failure diagnostics: use collection, type,
  comparison, and exception assertions directly instead of reducing their
  results to booleans or relying on LINQ exceptions. Tests must explain and
  verify behavior; line and branch coverage do not establish assertion quality.
- Native synchronous assertions execute immediately. Await real asynchronous
  operations and async exception assertions. Keep tests of synchronous parser
  contracts on the synchronous API; an async assertion or cleanup does not
  justify changing the operation under test. Preserve exact exception types,
  numeric tolerances, collection ordering, and diagnostic messages.
- Get cooperative cancellation from `TestContext.CancellationToken`. Timeouts
  use `CooperativeCancellation = true`; the test must pass the token to owned
  operations and join those operations before cleanup.
- Resources belong to one test invocation/data row. Use `TestInitialize` /
  `TestCleanup` for the upload endpoint, `TempWorkspace` for temporary files,
  and `ChildProcess` for processes. Reap children and stop/join servers before
  disposing their workspaces. Do not share mutable fixtures or introduce fake
  async initialization for stateless objects.
- `BadgeActionTests` choose Git for Windows' Bash rather than WSL Bash. CI checks
  the composite action and its default installer using locally packed candidates
  on Linux and Windows. All badge pushes target a local bare repository.

## Fallout integration

`build/Build.cs` provides local verification targets. CI uses direct native
`dotnet` commands with the same coverage property. The shipped
`src/DotCov.Fallout` component is tested through the real consuming executable
in `tests/DotCov.Fallout.TestBuild` (plain, compile, and cancellation modes).
Repository-wrapper tests also use real processes. Keep report interpretation
and gate policy in DotCov, and parameter binding/presentation in the adapter.

## Isolation diagnostics

Compare the same build, scope, and settings; vary only one condition at a time.
Disable retries and do not hide exits, stop at a failure limit, or use fail-fast.
Keep normal parallelism. For an isolation investigation, pass `--settings`
with an MSTest runsettings file selecting `Parallelize/Workers` and
`Parallelize/Scope=MethodLevel`; compare one worker with a bounded parallel run.
Use fresh result directories, `--report-trx`, and `--minimum-expected-tests 1`.
A `WaitAsync` timeout only ends the observation: cancellation and joining of the
original operation remain the test's responsibility.
