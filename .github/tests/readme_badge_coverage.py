"""Reproduce coverage behind pinned upstream README badges, using saved public evidence."""

import argparse
from decimal import Decimal
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
    percent = Decimal(str(published["covered_percent"]))
    assert percent == Decimal(100) * published["covered_lines"] / published["relevant_lines"]
    assert published["badge_url"] == "https://s3.amazonaws.com/assets.coveralls.io/badges/coveralls_100.svg"
    texts = list(ET.parse(EVIDENCE / "coveralls-100.svg").getroot().itertext())
    assert f"{percent.normalize():f}%" in texts, texts
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
    if args.target == "compare-versions":
        # Same c8/Mocha provider and locked dependencies as the published run.
        # Additional reporters expose the measurement; no source or test is changed.
        run(["node", root / "node_modules/c8/bin/c8.js", "--reporter=lcov",
             "--reporter=cobertura", "--reporter=json-summary", f"--reports-dir={output}",
             root / "node_modules/mocha/bin/mocha.js", "--reporter=json",
             f"--reporter-option=output={output / 'tests.json'}"], cwd=root)
        tests = read_json(output / "tests.json")["stats"]
        assert tests["passes"] == tests["tests"] == config["tests"], tests
        assert tests["failures"] == tests["pending"] == 0, tests
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
    assert summary["hasBranchData"] == (args.target == "compare-versions")
    if args.target == "compare-versions":
        assert total == tuple(published[key] for key in
                              ["covered_lines", "relevant_lines", "covered_branches", "relevant_branches"])
    # Compare the exact fraction, not a rounded display that could conceal misses.
    assert total[1] > 0 and Decimal(100) * total[0] / total[1] == percent
    assert Decimal(str(summary["lineRate"])) == percent
    text = run(["dotnet", tool, "check", report, "--min-line", percent,
                "--min-branch", 100 if total[3] else 0, "--badge-dir", output / "cli-badge"])
    assert "PASS:" in text
    expected = {"target": args.target, "commit": config["commit"], "publishedPercent": str(percent),
                "counts": total, "files": len(files), "publishedReport": config["build_url"]}
    (output / "expected.json").write_text(json.dumps(expected, indent=2), encoding="utf-8")
    print(f"README MATCH: {args.target}@{config['commit']}: published {percent}%, DotCov {summary['lineRate']}%; "
          f"lines {total[0]}/{total[1]}, branches {total[2]}/{total[3]}")


def badge(args):
    output = args.output.resolve()
    expected = read_json(output / "expected.json")
    directory = Path(os.environ["BADGE_DIRECTORY"])
    assert os.environ["BADGE_CODE"] == "0"
    value = read_json(directory / "coverage-badge.json")
    assert value["color"] == "brightgreen", value
    assert value["message"] == f"{Decimal(expected['publishedPercent']):.1f}%", value
    svg = ET.parse(directory / "coverage-badge.svg").getroot()
    assert "PASS" in svg.attrib["aria-label"]
    origin = output / "origin.git"
    assert run(["git", "--git-dir", origin, "rev-list", "--count", "badges"]).strip() == "1"
    assert run(["git", "--git-dir", origin, "ls-tree", "--name-only", "badges"]).splitlines() == [
        "coverage-badge.json", "coverage-badge.svg"]
    for suffix in ["json", "svg"]:
        name = f"coverage-badge.{suffix}"
        published = subprocess.check_output(["git", "--git-dir", str(origin), "show", f"badges:{name}"])
        assert published == (directory / name).read_bytes()
        (output / f"published-{name}").write_bytes(published)
    print(f"README BADGE MATCH: {expected['target']}: published {value['message']} to the local origin")


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
    args = parser.parse_args()
    (measure if args.command == "measure" else badge)(args)
