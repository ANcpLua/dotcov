---
name: dotcov
description: >-
  Use DotCov in consuming repositories to collect code coverage, read Cobertura
  reports, enforce coverage thresholds, diagnose uncovered lines and branches,
  compare runs, or interpret .NET CRAP scores. Applies when running or explaining
  coverage with DotCov, not when maintaining or publishing DotCov itself.
---

# DotCov

## Choose the repository's measurement contract

Read the consuming repository's instructions, test commands, coverage configuration,
tool manifest, and threshold policy. Keep its runner, coverage provider, source scope,
and exclusions. A repository with no threshold policy can receive a coverage report;
obtain the intended thresholds before introducing a gate. DotCov's own 100% policy
does not set the policy of a consumer.

Prefer a repository-pinned `DotCov.Tool`: restore its tool manifest and invoke it as
`dotnet dotcov`. Otherwise inspect `dotcov --version`. If installation is needed,
use the repository's preferred scope and version; the global installation command
is `dotnet tool install --global DotCov.Tool`. Record the actual version, and inspect
`dotcov <command> --help` for the installed contract. The guidance here was checked
against DotCov 4.3.0.

## Collect, inspect, and gate

Use existing reports when the task is to interpret them. For a new measurement,
read the matching section of [collecting coverage](references/collecting-coverage.md).
Run the repository's real tests and include its expected source projects/modules,
including code no test imports. Verify that those projects appear in the measured
data before describing a result as repository coverage. A changed test filter or
source scope changes what the result proves.

Give each collection a fresh output directory. Point DotCov at that run's report or
directory: every matching report in a supplied directory is merged. Merge shards
only when they belong to the intended measurement. `**/*cobertura*.xml` is the default
directory pattern; use a direct file for `coverage.xml`, or an explicit pattern such
as `--pattern '**/coverage.xml'`.

Use `report` to inspect, and `check` to enforce the configured line and/or branch
minimum. `dotcov test` combines a compatible .NET test run with a gate. Prefer JSON
for structured interpretation; retain stderr diagnostics and the process exit code.
For example, with an existing line-minimum policy and artifact paths assigned:

```sh
if dotcov check "$coverage_report" --min-line "$minimum_line" --format json \
    > "$result_json" 2> "$diagnostics_log"; then
  dotcov_status=0
else
  dotcov_status=$?
fi
```

Add `--min-branch` when the repository defines a branch minimum; use branch-only
gating when that is its policy. No supplied minimum is a usage error; a dimension
omitted or set to 0 is not checked. Preserve the captured status in automation.

## Interpret the result

| Exit | Meaning | Next action |
| --- | --- | --- |
| 0 | A measured gate passed; `report`, `diff`, and `snapshot` only completed successfully | State exactly which thresholds and scope were evaluated |
| 1 | `FAIL`: measured coverage or CRAP fell outside its limit | Inspect the uncovered code or methods above the CRAP limit |
| 2 | Invalid usage, flag, value, missing threshold, or invalid declared source root | Correct the command using its installed help |
| 3 | `NODATA`: missing measurements, unresolved file identity, or incomplete CRAP scoring | Repair collection, source identity, or complexity inputs before claiming a result |
| 4 | `DISABLED`: no active threshold | Report that the gate did not enforce a limit |
| 5 | Input, parsing, IO, upload, badge, or process-start failure | Diagnose the named failure; retain any separate gate verdict |
| 6 | `dotcov test`'s test process failed; coverage was not evaluated | Inspect the test failure and that run's log |

For gate commands, read `gate.outcome`, `gate.exitCode`, `gate.reason`, and, for
`check`/`test`, `gate.offenders`. Inspect `inputs`, `warnings`, and any `excluded`
and `exclusionRules` as well. JSON/markdown `dotcov test` output keeps test logs in
`TestResults/<run>/dotnet-test.log`; a failed test run can leave no JSON document.
The table format includes test output before the coverage table.

Explain line and branch coverage separately. Report covered/total counts and rates,
then map `offenders[].uncovered` ranges and `partialBranches` to the actual source.
Read the conditions and tests before proposing the missing behavior. `--top N`
limits displayed offenders, not the evaluated gate. Rounded percentages do not
override the structured verdict. `report --threshold` affects display only.

Resolve ambiguous source roots with `--source-root` naming a root declared in the
report, or `--probe-sources` where the source checkout is present. Inspect parser
warnings even when the gate passes. Apply exclusions only when they match the
repository's policy: `--exclude-generated` also removes `Program.cs` and migrations;
`--exclude-external` removes dependency SourceLink paths. `--keep` exempts matching
paths from exclusions. Changing these changes the measured scope.

For .NET risk analysis, `crap` needs complexity for every method: Coverlet can embed
it, or `--metrics` can supply matching .NET metrics. Vitest/c8/Jest and coverage.py
reports have no such complexity; use coverage gates for those reports. A fully
covered method's CRAP score equals its complexity, so reducing a high score there
requires simplifying the method. Keep producer, filters, source identity, and
measurement scope comparable when using `diff` between runs.

## Deliver an actionable result

Report the tool version, actual commands and exit statuses, test outcome, report
paths, measured scope, counts/rates, thresholds, and gate outcome. Name material
warnings and the most useful missing line/branch behaviors to test next. Keep a
failed test run, failed gate, and unavailable measurement distinct.

When asked to close coverage gaps, add tests that exercise those behaviors and
remeasure the affected scope. Retain the existing thresholds and filters; report
focused coverage as focused coverage. Reuse a valid measurement for multiple
reports or gates until its inputs change.

[DotCov command documentation](https://github.com/ANcpLua/dotcov#commands)
and the installed command's help are the reference for supported options.
