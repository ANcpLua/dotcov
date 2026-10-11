"""Reproduce coverage behind pinned upstream README badges, using saved public evidence."""

import argparse
from decimal import Decimal, ROUND_DOWN, ROUND_HALF_UP
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

from consumer_coverage import file_key, read_json, run

EVIDENCE = Path(__file__).parent / "readme-badges"
TARGETS = read_json(EVIDENCE / "targets.json")


def reference(target, root):
    config = TARGETS[target]
    commit = run(["git", "rev-parse", "HEAD"], cwd=root).strip()
    assert commit == config["commit"], f"Wrong upstream commit: {commit}"
    readme = (root / config["readme"]).read_text(encoding="utf-8")
    assert config["badge_markup"] in readme, "Pinned README must actually embed the coverage badge"
    if target == "pydash":
        assert "|version| |build| |coveralls| |license|" in readme
    published = read_json(EVIDENCE / f"{target}-coveralls.json")
    assert published["commit_sha"] == commit
    assert published["repo_name"] == config["repository"]
    assert config["build_url"] == f"https://coveralls.io/builds/{published['id']}.json"
    assert published["relevant_lines"] > 0
    percent = Decimal(100) * published["covered_lines"] / published["relevant_lines"]
    published_percent = Decimal(str(published["covered_percent"]))
    if target == "peek-readable":
        # Coveralls includes branches in this project's headline percentage.
        # Counts remain exact; only its serialized floating-point ratio is rounded.
        combined = (Decimal(100) * (published["covered_lines"] + published["covered_branches"])
                    / (published["relevant_lines"] + published["relevant_branches"]))
        assert abs(published_percent - combined) < Decimal("1e-12")
        assert published["url"] == config["upstream_run_url"]
    else:
        assert percent == published_percent
    badge_percent = published_percent.quantize(Decimal(1), rounding=ROUND_HALF_UP)
    assert published["badge_url"] == f"https://s3.amazonaws.com/assets.coveralls.io/badges/coveralls_{badge_percent}.svg"
    texts = list(ET.parse(EVIDENCE / f"coveralls-{badge_percent}.svg").getroot().itertext())
    assert f"{badge_percent}%" in texts, texts
    if target == "pydash":
        statuses = read_json(EVIDENCE / "pydash-statuses.json")
        assert statuses["sha"] == commit
        assert any(status["state"] == "success" and status["target_url"] + ".json" == config["build_url"]
                   for status in statuses["statuses"]), "GitHub must link this commit to the published build"
        job = read_json(EVIDENCE / "pydash-coveralls-job.json")
        upstream = read_json(EVIDENCE / "pydash-upstream-job.json")
        assert job["repo_name"] == config["repository"]
        assert job["covered_percent"] == percent
        assert job["full_number"].split(".")[0] == str(upstream["run_id"])
        assert upstream["head_sha"] == commit and upstream["conclusion"] == "success"
        assert upstream["name"] == "Test (3.12)"
        assert config["job_url"] == f"https://coveralls.io/jobs/{job['id']}.json"
        assert any(status["context"] == "Coveralls - 3.12" and status["state"] == "success"
                   and status["target_url"] + ".json" == config["job_url"]
                   for status in statuses["statuses"])
    return config, published, percent


def measure(args):
    root, output, tool = args.project.resolve(), args.output.resolve(), args.tool.resolve()
    config, published, percent = reference(args.target, root)
    output.mkdir(parents=True, exist_ok=False)
    report = output / "coverage.xml"
    if args.target in ("compare-versions", "peek-readable"):
        # Same c8/Mocha provider and locked dependencies as the published run.
        # Additional reporters expose the measurement; no source or test is changed.
        env, hooks = os.environ.copy(), []
        if args.target == "peek-readable":
            for project in ("lib", "test"):
                run(["node", root / "node_modules/typescript/bin/tsc", "-p", project], cwd=root)
            env["DOTCOV_TIMER_AUDIT"] = str(output / "real-timers.json")
            hooks = ["--require", Path(__file__).resolve().parent / "peek_real_timers.cjs"]
        run(["node", root / "node_modules/c8/bin/c8.js", "--reporter=lcov",
             "--reporter=cobertura", "--reporter=json-summary", f"--reports-dir={output}",
             f"--temp-directory={output / 'v8'}",
             root / "node_modules/mocha/bin/mocha.js", *hooks, "--reporter=json",
             f"--reporter-option=output={output / 'tests.json'}"], cwd=root, env=env)
        run(["git", "diff", "--exit-code", "HEAD"], cwd=root)
        tests = read_json(output / "tests.json")["stats"]
        assert tests["passes"] == tests["tests"] == config["tests"], tests
        assert tests["failures"] == tests["pending"] == 0, tests
        if args.target == "peek-readable":
            audit = read_json(output / "real-timers.json")
            assert audit["testsChecked"] == config["tests"], audit
            assert audit["nativeTimers"] is True, audit
            assert [case["title"] for case in audit["timedTests"]] == [
                "Matrix Node.js StreamReader Handle delayed read handle delay",
                "Matrix Node.js StreamReader Handle delayed read abort async operation",
            ], audit
            assert all(case["scheduled"] > 0 for case in audit["timedTests"]), audit
            assert audit["timedTests"][0]["fired"] > 0, audit
        (output / "cobertura-coverage.xml").rename(report)
        native = read_json(output / "coverage-summary.json")
        native.pop("total")
        files = {file_key(path, root): (value["lines"]["covered"], value["lines"]["total"],
                                       value["branches"]["covered"], value["branches"]["total"])
                 for path, value in native.items()}
    else:
        # Keep upstream's tests, assertions, source selection and 100% gate. The
        # plugin supplies deterministic time only to its two debounce tests.
        plugin_path = str(Path(__file__).resolve().parent)
        env = {**os.environ, "COVERAGE_FILE": str(output / ".coverage"),
               "PYTHONPATH": os.pathsep.join(filter(None, [plugin_path, os.environ.get("PYTHONPATH")]))}
        run([sys.executable, "-m", "pytest", "-p", "pydash_clock", "src/pydash", "tests", "--cov=pydash",
             "--ignore=tests/pytest_mypy_testing", "-q",
             f"--cov-report=json:{output / 'coverage-native.json'}",
             f"--cov-report=xml:{report}", f"--junitxml={output / 'tests.xml'}"], cwd=root, env=env)
        run(["git", "diff", "--exit-code", "HEAD", "--", "src", "tests", "pyproject.toml"], cwd=root)
        suites = ET.parse(output / "tests.xml").getroot().findall("testsuite")
        assert sum(int(suite.attrib["tests"]) for suite in suites) == config["tests"]
        assert all(int(suite.attrib[key]) == 0 for suite in suites for key in ["failures", "errors", "skipped"])
        timed = [case.attrib["name"] for suite in suites for case in suite.findall("testcase")
                 if case.find("properties/property[@name='dotcov-clock'][@value='deterministic']") is not None]
        assert sorted(timed) == ["test_debounce", "test_debounce_max_wait"], timed
        native = read_json(output / "coverage-native.json")
        assert not native["meta"]["branch_coverage"], "Match upstream's line-only collection"
        files = {file_key(path, root): (value["summary"]["covered_lines"], value["summary"]["num_statements"], 0, 0)
                 for path, value in native["files"].items()}
        # Coveralls aggregates five installed Python environments (13,033 lines).
        # The linked Python 3.12 log supplies the independent per-file counts here.
        log = (EVIDENCE / "pydash-upstream-coverage.txt").read_text(encoding="utf-8")
        rows = re.findall(r"site-packages/(pydash/\S+\.py)\s+(\d+)\s+(\d+)\s+100%", log)
        historical = {file_key("src/" + path, root): (int(total) - int(missed), int(total), 0, 0)
                      for path, total, missed in rows}
        assert len(historical) == 14, "Missing upstream per-file evidence"
        assert files == historical, f"Fresh coverage differs from the published Python 3.12 run: {files}"

    actual = json.loads(run(["dotnet", tool, "report", report, "--format", "json"]))
    (output / "dotcov.json").write_text(json.dumps(actual, indent=2), encoding="utf-8")
    actual_files = {file_key(file["path"], root): tuple(file[key] for key in
                    ("linesHit", "linesTotal", "branchesHit", "branchesTotal")) for file in actual["files"]}
    assert actual_files == files, f"DotCov per-file mismatch: native {files}; DotCov {actual_files}"
    assert not actual.get("warnings"), actual.get("warnings")
    total = tuple(sum(value[index] for value in files.values()) for index in range(4))
    summary = actual["summary"]
    assert tuple(summary[key] for key in ["coveredLines", "totalLines", "coveredBranches", "totalBranches"]) == total
    assert summary["hasBranchData"] == (args.target != "pydash")
    if args.target != "pydash":
        assert total == tuple(published[key] for key in
                              ["covered_lines", "relevant_lines", "covered_branches", "relevant_branches"])
    # Compare the exact fraction, not a rounded display that could conceal misses.
    assert total[1] > 0 and Decimal(100) * total[0] / total[1] == percent
    if args.target == "peek-readable":
        assert Decimal(str(summary["lineRate"])) == percent.quantize(Decimal(".01"), rounding=ROUND_HALF_UP)
        branch_percent = Decimal(100) * total[2] / total[3]
        assert Decimal(str(summary["branchRate"])) == branch_percent.quantize(Decimal(".01"), rounding=ROUND_HALF_UP)
        # Exact native/published integer equality above is mandatory. These gates
        # additionally exercise the CLI at the baseline, to three decimal places.
        minimum_line = percent.quantize(Decimal(".001"), rounding=ROUND_DOWN)
        minimum_branch = branch_percent.quantize(Decimal(".001"), rounding=ROUND_DOWN)
        assert str(minimum_line) == config["min_line"]
        assert str(minimum_branch) == config["min_branch"]
        messages = {"pass": f"{percent:.1f}% / {branch_percent:.1f}%",
                    "line-fail": f"{percent.quantize(Decimal('.1'), rounding=ROUND_DOWN):.1f}% / {branch_percent:.1f}%",
                    "branch-fail": f"{percent:.1f}% / {branch_percent.quantize(Decimal('.1'), rounding=ROUND_DOWN):.1f}%"}
        for phase, line, branch, code in [("pass", minimum_line, minimum_branch, 0),
                                          ("line-fail", 100, minimum_branch, 1),
                                          ("branch-fail", minimum_line, 100, 1)]:
            text = run(["dotnet", tool, "check", report, "--min-line", line, "--min-branch", branch,
                        "--badge-dir", output / f"cli-{phase}"], expected=code)
            assert ("PASS:" if code == 0 else "FAIL:") in text
        badge_details = {"badgeMessages": messages}
    else:
        assert Decimal(str(summary["lineRate"])) == percent
        text = run(["dotnet", tool, "check", report, "--min-line", percent,
                    "--min-branch", 100 if total[3] else 0, "--badge-dir", output / "cli-badge"])
        assert "PASS:" in text
        badge_details = {"badgeMessages": {"pass": f"{percent:.1f}%"}}
    expected = {"target": args.target, "commit": config["commit"],
                "publishedPercent": str(published["covered_percent"]), "linePercent": str(percent),
                "counts": total, "files": len(files), "publishedReport": config["build_url"], **badge_details}
    (output / "expected.json").write_text(json.dumps(expected, indent=2), encoding="utf-8")
    print(f"README MATCH: {args.target}@{config['commit']}: published line {percent}%, DotCov {summary['lineRate']}%; "
          f"lines {total[0]}/{total[1]}, branches {total[2]}/{total[3]}")


def badge(args):
    output = args.output.resolve()
    expected = read_json(output / "expected.json")
    directory = Path(os.environ["BADGE_DIRECTORY"])
    passing = args.phase == "pass"
    assert os.environ["BADGE_CODE"] == ("0" if passing else "1")
    if not passing:
        assert expected["target"] == "peek-readable"
        assert os.environ["BADGE_OUTCOME"] == "failure"
    value = read_json(directory / "coverage-badge.json")
    assert value["color"] == ("brightgreen" if passing else "red"), value
    assert value["message"] == expected["badgeMessages"][args.phase], value
    svg = ET.parse(directory / "coverage-badge.svg").getroot()
    assert ("PASS" if passing else "FAIL") in svg.attrib["aria-label"]
    origin = output / "origin.git"
    count = {"pass": "1", "line-fail": "2", "branch-fail": "3"}[args.phase]
    assert run(["git", "--git-dir", origin, "rev-list", "--count", "badges"]).strip() == count
    assert run(["git", "--git-dir", origin, "ls-tree", "--name-only", "badges"]).splitlines() == [
        "coverage-badge.json", "coverage-badge.svg"]
    for suffix in ["json", "svg"]:
        name = f"coverage-badge.{suffix}"
        published = subprocess.check_output(["git", "--git-dir", str(origin), "show", f"badges:{name}"])
        assert published == (directory / name).read_bytes()
        (output / f"published-{args.phase}-{name}").write_bytes(published)
    print(f"README BADGE MATCH: {expected['target']} {args.phase}: published {value['message']} to the local origin")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    measurement = commands.add_parser("measure")
    measurement.add_argument("target", choices=TARGETS)
    measurement.add_argument("--project", type=Path, required=True)
    measurement.add_argument("--tool", type=Path, required=True)
    measurement.add_argument("--output", type=Path, required=True)
    verification = commands.add_parser("badge")
    verification.add_argument("--output", type=Path, required=True)
    verification.add_argument("--phase", choices=["pass", "line-fail", "branch-fail"], default="pass")
    args = parser.parse_args()
    (measure if args.command == "measure" else badge)(args)
