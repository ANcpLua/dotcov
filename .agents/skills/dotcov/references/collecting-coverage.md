# Collecting coverage for DotCov

Read the section matching the consuming repository. Start with its documented
coverage command and pinned dependencies; adapt the examples to its project paths,
package manager, source roots, and configured minimums. Check the test process's
exit status before gating its report. A failing test run remains a test failure
even if the producer wrote coverage.

The shell examples use POSIX syntax. For collection commands that accept an output
directory, create a new directory, for example:

```sh
coverage_run=$(mktemp -d "${TMPDIR:-/tmp}/dotcov-coverage.XXXXXX")
```

## .NET

Inspect `global.json`, any `DOTNET_TEST_RUNNER` override, the test projects, coverage
packages, and runsettings. The test framework name alone does not select the SDK's
runner. Keep an established Coverlet or Microsoft coverage pipeline intact and
read its Cobertura output with `dotcov report` or `dotcov check`.

For the one-command path with Microsoft's coverage provider:

```sh
dotcov test tests/App.Tests/App.Tests.csproj --min-line "$minimum_line" \
  --format json -- -c Release
```

DotCov creates `TestResults/<run>` itself and gates only that run. Stderr names the
report directory. Pass additional `dotnet test` arguments after `--`; DotCov adapts
the positional project/solution to the SDK runner's syntax.

- Native Microsoft.Testing.Platform requires
  `Microsoft.Testing.Extensions.CodeCoverage`. DotCov supplies `--coverage` and
  `--coverage-output-format cobertura`.
- VSTest uses the `Code Coverage` collector supplied by `Microsoft.NET.Test.Sdk`.
  DotCov requests its Cobertura output.
- An existing coverlet.MTP or `XPlat Code Coverage` setup should use the repository's
  own collection command, followed by `check` on its fresh report. Keep that
  provider's options separate from Microsoft's `--coverage` options.

If the repository uses Fallout, use targets defined by its own build. The
`DotCov.Fallout` component's target is `ReportCoverage`; it consumes reports from
its configured `CoverageSearchDirectory` and needs `--coverage-min-line` and/or
`--coverage-min-branch`. Run the consuming build's coverage collection first and
ensure its search directory holds only the intended run. Custom targets such as
`Coverage` must be verified in that build before invoking them.

Sources: [DotCov test](https://github.com/ANcpLua/dotcov#commands),
[runner implementation](https://github.com/ANcpLua/dotcov/blob/main/src/DotCov.Tool/DotCovCli.Test.cs),
[Fallout component](https://github.com/ANcpLua/dotcov/blob/main/src/DotCov.Fallout/README.md).

## TypeScript / JavaScript

Use the repository's installed, locked runner and coverage provider. The examples
use `npx --no-install` to select existing packages; adapt to pnpm, Yarn, Bun, or
the repository's scripts. Keep the existing source maps and coverage provider.

With Vitest and its matching installed coverage package:

```sh
npx --no-install vitest run --coverage.enabled \
  --coverage.reporter=cobertura --coverage.include='src/**' \
  --coverage.reportsDirectory="$coverage_run"
```

With c8 wrapping the repository's actual Node test command:

```sh
npx --no-install c8 --all --src src --reporter=cobertura \
  --reports-dir "$coverage_run" node --test
```

Both examples produce `cobertura-coverage.xml` in the selected directory; pass that
file to DotCov. The source include/all settings include modules no test loaded;
match them to the repository's real source scope. Vitest's V8 remapping and c8 can
count branches differently, so preserve the producer when comparing percentages.
For Jest or another configured runner, select its Cobertura reporter and a fresh
output directory rather than forwarding `.NET` test options.

Sources: [DotCov producers](https://github.com/ANcpLua/dotcov#other-languages),
[Vitest coverage options](https://vitest.dev/config/coverage),
[c8 options](https://github.com/bcoe/c8#cli-options--configuration).

## Python

Use the repository's environment, test command, and coverage.py configuration.
Include the intended package or source roots so unimported code is measured. For
pytest with a package named `your_package`:

```sh
COVERAGE_FILE="$coverage_run/.coverage" \
  python -m coverage run --branch --source=your_package -m pytest
COVERAGE_FILE="$coverage_run/.coverage" \
  python -m coverage xml -o "$coverage_run/coverage.xml"
```

Read or gate that exact XML file, for example:

```sh
dotcov report "$coverage_run/coverage.xml" --format json
```

A direct file bypasses the default Cobertura filename pattern. Keep branch
measurement enabled when enforcing a branch threshold. For several declared
`<source>` roots, use the identity-resolution options in the [skill](../SKILL.md).

Sources: [coverage.py execution](https://coverage.readthedocs.io/en/latest/commands/cmd_run.html),
[coverage.py XML](https://coverage.readthedocs.io/en/latest/commands/cmd_xml.html).

## Other producers

Use the repository's own tests and an actual Cobertura XML export. LCOV, raw V8
JSON, and Istanbul JSON need the producer's supported Cobertura conversion before
DotCov can read them. Preserve the test exit status, measured source scope, and
run identity through that conversion; identify any projects omitted by collection.
