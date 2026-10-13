"""Compare real upstream coverage with DotCov, then verify the composite badge action.

Only the upstream producers read coverage data here. Expected counts come from their
native JSON, independently of DotCov's Cobertura parser. CI pins source and dependencies.
"""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET


def run(args, *, cwd=None, env=None, expected=0):
    result = subprocess.run(
        [str(arg) for arg in args], cwd=cwd, env=env, text=True,
        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, encoding="utf-8", check=False,
    )
    print(result.stdout, end="", flush=True)
    if result.returncode != expected:
        raise AssertionError(f"{args[0]} exited {result.returncode}, expected {expected}")
    return result.stdout


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def counts(summary, language):
    if language == "typescript":
        return (summary["lines"]["covered"], summary["lines"]["total"],
                summary["branches"]["covered"], summary["branches"]["total"])
    return (summary["covered_lines"], summary["num_statements"],
            summary["covered_branches"], summary["num_branches"])


def file_key(path, root):
    return os.path.normcase(os.path.abspath(root / path))


def measure(args):
    root, output, tool = args.project.resolve(), args.output.resolve(), args.tool.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = output / "coverage.xml"
    if args.language == "typescript":
        env = {**os.environ, "NODE_OPTIONS": "--experimental-vm-modules"}
        # The full upstream suite passes, but its 100% threshold rejects the V8
        # measurement. Verify test results separately so a broken suite cannot pass.
        run(["node", root / "node_modules/jest/bin/jest.js", "--runInBand", "--coverage",
             "--coverageProvider=v8", "--coverageReporters=cobertura",
             "--coverageReporters=json-summary", f"--coverageDirectory={output}",
             "--json", f"--outputFile={output / 'tests.json'}"], cwd=root, env=env, expected=1)
        tests = read_json(output / "tests.json")
        assert tests["numPassedTests"] == 19, tests
        assert tests["numFailedTests"] == tests["numFailedTestSuites"] == 0, tests
        (output / "cobertura-coverage.xml").rename(report)
        native = read_json(output / "coverage-summary.json")
        total = counts(native.pop("total"), args.language)
        files = {file_key(path, root): counts(value, args.language)
                 for path, value in native.items()}
    else:
        env = {**os.environ, "COVERAGE_FILE": str(output / ".coverage")}
        coverage = [sys.executable, "-m", "coverage"]
        run([*coverage, "run", "--branch", "--source=itsdangerous", "-m", "pytest", "-q",
             f"--junitxml={output / 'tests.xml'}"], cwd=root, env=env)
        suites = ET.parse(output / "tests.xml").getroot().findall("testsuite")
        assert sum(int(suite.attrib["tests"]) for suite in suites) == 297
        assert all(int(suite.attrib["failures"]) == int(suite.attrib["errors"]) == 0 for suite in suites)
        run([*coverage, "json", "-o", output / "coverage-native.json"], cwd=root, env=env)
        run([*coverage, "xml", "-o", report], cwd=root, env=env)
        run([*coverage, "report", "--fail-under=100"], cwd=root, env=env, expected=2)
        native = read_json(output / "coverage-native.json")
        total = counts(native["totals"], args.language)
        files = {file_key(path, root): counts(value["summary"], args.language)
                 for path, value in native["files"].items()}

    # --probe-sources: the job reads the report where the sources are checked out, so each
    # relative filename resolves against the root that holds it (dotcov's default is lexical).
    dotcov = ["dotnet", tool]
    actual = json.loads(run([*dotcov, "report", report, "--format", "json", "--probe-sources"]))
    (output / "dotcov.json").write_text(json.dumps(actual, indent=2), encoding="utf-8")
    summary = actual["summary"]
    observed = tuple(summary[key] for key in ("coveredLines", "totalLines", "coveredBranches", "totalBranches"))
    assert observed == total, f"Native counts {total}; DotCov counts {observed}"
    actual_files = {
        file_key(file["path"], root): tuple(file[key] for key in
            ("linesHit", "linesTotal", "branchesHit", "branchesTotal"))
        for file in actual["files"]
    }
    assert actual_files == files, f"Per-file mismatch: native {files}; DotCov {actual_files}"
    # ItsDangerous's upstream reporting config also names tests as a source root. Every
    # measured file exists under exactly one root, so the disk probe attributes it without the
    # multi-root warning; the per-file comparison above proves that attribution.
    warnings = [warning["kind"] for warning in actual.get("warnings", [])]
    assert warnings == [], warnings
    # Both real suites have missing lines and branches. No report is edited to manufacture failure.
    line_hit, line_total, branch_hit, branch_total = total
    assert 0.9 <= line_hit / line_total < 1
    assert 0.9 <= branch_hit / branch_total < 1
    for name, line, branch, code in [("pass", 90, 90, 0), ("line-fail", 100, 90, 1), ("branch-fail", 90, 100, 1)]:
        text = run([*dotcov, "check", report, "--min-line", line, "--min-branch", branch, "--probe-sources",
                    "--badge-dir", output / f"cli-{name}"], expected=code)
        assert ("PASS:" if code == 0 else "FAIL:") in text
    expected = {"counts": total, "files": len(files), "language": args.language}
    (output / "expected.json").write_text(json.dumps(expected, indent=2), encoding="utf-8")
    print(f"MATCH: {args.language}: {len(files)} files, lines {line_hit}/{line_total}, branches {branch_hit}/{branch_total}")


def verify_badges(args):
    output = args.output.resolve()
    expected = read_json(output / "expected.json")
    line_hit, line_total, branch_hit, branch_total = expected["counts"]
    line_text = f"{100 * line_hit / line_total:.1f}%"
    assert os.environ["PASS_CODE"] == "0"
    assert os.environ["FAIL_CODE"] == "1"
    assert os.environ["FAIL_OUTCOME"] == "failure"
    for name, color, verdict in [("PASS", "brightgreen", "PASS"), ("FAIL", "red", "FAIL")]:
        branch_percent = 100 * branch_hit / branch_total if name == "PASS" else branch_hit * 1000 // branch_total / 10
        message = f"{line_text} / {branch_percent:.1f}%"
        directory = Path(os.environ[f"{name}_DIRECTORY"])
        badge = read_json(directory / "coverage-badge.json")
        assert badge["message"] == message, badge
        assert badge["color"] == color, badge
        svg = ET.parse(directory / "coverage-badge.svg").getroot()
        assert verdict in svg.attrib["aria-label"], svg.attrib
    origin = output / "origin.git"
    assert run(["git", "--git-dir", origin, "rev-list", "--count", "badges"]).strip() == "2"
    names = run(["git", "--git-dir", origin, "ls-tree", "--name-only", "badges"]).splitlines()
    assert names == ["coverage-badge.json", "coverage-badge.svg"], names
    for suffix in ["json", "svg"]:
        name = f"coverage-badge.{suffix}"
        published = subprocess.check_output(["git", "--git-dir", str(origin), "show", f"badges:{name}"])
        assert published == (Path(os.environ["FAIL_DIRECTORY"]) / name).read_bytes()
        (output / f"published-{name}").write_bytes(published)
    print(f"PASS: {expected['language']} badge action published {message}; branch failure produced a red badge and exit 1")


if __name__ == "__main__":
    # Jest emits Unicode on Windows even when Python inherits a legacy code page.
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    measurement = commands.add_parser("measure")
    measurement.add_argument("language", choices=["typescript", "python"])
    measurement.add_argument("--project", type=Path, required=True)
    measurement.add_argument("--tool", type=Path, required=True)
    measurement.add_argument("--output", type=Path, required=True)
    badges = commands.add_parser("badges")
    badges.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    (measure if arguments.command == "measure" else verify_badges)(arguments)
