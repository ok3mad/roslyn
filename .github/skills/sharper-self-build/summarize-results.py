#!/usr/bin/env python3
"""Summarizes the xUnit result files that RunTests writes, one per work item, and matches the failures against the
known failures recorded in GitHub issues.

    python3 .github/skills/sharper-self-build/summarize-results.py [--issues issues.json] [--build-log build.log]
        [--all] [<dir> ...]

<dir> is a RunTests --out directory (artifacts/TestResults/<run>). Pass several to merge a run with its reruns.
--build-log is the console output of the Sharper-mode build; its compiler warnings and errors are matched too.

--issues is a JSON file of the repository's `self-build` issues, open and closed, as written by

    gh issue list --repo ok3mad/roslyn --label self-build --state all --limit 200 --json number,title,state,body

(a GitHub MCP list_issues result saved to a file works too). Each such issue lists its failing tests in a fenced
block with the info string `self-build`, one fully qualified test name per line; a line ending in `*` is a prefix.
Every failure is then printed as `#<issue>` (known, open issue), `REGRESSION #<issue>` (matches a closed issue:
reopen it instead of filing a new one) or `NEW` (no issue: search, then file one). Open issues whose tests all passed
are listed as possibly fixed. Compiler diagnostics are matched the same way against blocks with the info string
`self-build-diagnostics`, one `<path> <code>` per line (`src/.../SharperTests.cs CS8602`): line numbers are left
out because they move.

Prints the totals and the work items whose result file is empty or unreadable (the work item never finished: rerun
those assemblies with RunTests --include). Without --issues, only the first 10 failures of an assembly are listed
unless --all is given. Exits 1 when a test failure is NEW or a REGRESSION, or a work item is incomplete.
"""

import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

BLOCK = re.compile(r"^```self-build[ \t]*\n(.*?)^```", re.MULTILINE | re.DOTALL)
DIAGNOSTICS_BLOCK = re.compile(r"^```self-build-diagnostics[ \t]*\n(.*?)^```", re.MULTILINE | re.DOTALL)
# /repo/src/A.cs(12,5): warning CS8602: message [/repo/src/A.csproj::TargetFramework=net10.0]
DIAGNOSTIC = re.compile(r"^\s*(?P<path>[^\s(][^(]*)\(\d+,\d+\): (?P<severity>warning|error) (?P<code>[A-Z]+\d+): ")


def first_line(text, limit=160):
    for line in (text or "").splitlines():
        line = line.strip()
        if line and line not in ("Expected:", "Actual:"):
            return line[:limit]
    return ""


def block_lines(regex, body):
    lines = []
    for block in regex.findall(body or ""):
        lines += [line.strip() for line in block.splitlines() if line.strip() and not line.startswith("#")]
    return lines


def load_issues(path, regex):
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    if isinstance(data, dict):
        data = data.get("issues") or data.get("items") or []
    issues = []
    for issue in data:
        patterns = block_lines(regex, issue.get("body"))
        if patterns:
            issues.append({"number": issue["number"], "title": issue.get("title", ""),
                           "open": str(issue.get("state", "open")).lower() == "open", "patterns": patterns})
    return issues


def read_diagnostics(build_log):
    """Returns the distinct `<path> <code>` keys of the log's compiler diagnostics, with one full line each."""
    found = {}
    for line in Path(build_log).read_text(encoding="utf-8", errors="replace").splitlines():
        match = DIAGNOSTIC.match(line)
        if match:
            path = match.group("path").strip()
            if "/src/" in path:
                path = "src/" + path.split("/src/", 1)[1]
            found.setdefault(f"{path} {match.group('code')}", line.strip().split(" [")[0])
    return found


def diagnostic_matches(pattern, key):
    path, _, code = pattern.rpartition(" ")
    key_path, _, key_code = key.rpartition(" ")
    return code == key_code and key_path.endswith(path)


def matches(pattern, test):
    return test.startswith(pattern[:-1]) if pattern.endswith("*") else test == pattern


def classify(test, issues, match=None):
    match = match or matches
    hits = [i for i in issues if any(match(p, test) for p in i["patterns"])]
    for issue in hits:
        if issue["open"]:
            return f"#{issue['number']}", False
    if hits:
        return f"REGRESSION #{hits[0]['number']}", True
    return "NEW", True


def read_results(dirs):
    total = failed = skipped = 0
    failing = {}
    incomplete = []
    for directory in dirs:
        for path in sorted(Path(directory).glob("*.xml")):
            try:
                root = ET.parse(path).getroot()
            except ET.ParseError:
                incomplete.append(str(path))
                continue
            for assembly in root.iter("assembly"):
                name = Path(assembly.get("name", "")).name or path.stem
                total += int(assembly.get("total") or 0)
                failed += int(assembly.get("failed") or 0)
                skipped += int(assembly.get("skipped") or 0)
                for test in assembly.iter("test"):
                    if test.get("result") == "Fail":
                        message = test.find("failure/message")
                        test_name = re.sub(r"^\[HANG\]\s*", "", test.get("name") or "")
                        failing.setdefault(name, []).append(
                            (test_name, first_line(message.text if message is not None else "")))
    return total, failed, skipped, failing, incomplete


def main(dirs, issues_path, build_log, show_all):
    unexpected = False
    if build_log:
        diagnostics = read_diagnostics(build_log)
        diagnostic_issues = load_issues(issues_path, DIAGNOSTICS_BLOCK) if issues_path else []
        print(f"compiler diagnostics (distinct file and code): {len(diagnostics)}")
        for key, line in sorted(diagnostics.items()):
            label, bad = classify(key, diagnostic_issues, diagnostic_matches) if issues_path else ("", True)
            unexpected |= bad
            print(f"  {label} {line}" if label else f"  {line}")
        if issues_path:
            for issue in diagnostic_issues:
                if issue["open"] and not any(diagnostic_matches(p, k) for p in issue["patterns"] for k in diagnostics):
                    print(f"  open issue with none of its diagnostics in this build: "
                          f"#{issue['number']} {issue['title']}")
        if not dirs:
            return 1 if unexpected else 0
        print()

    total, failed, skipped, failing, incomplete = read_results(dirs)
    issues = load_issues(issues_path, BLOCK) if issues_path else None
    print(f"tests: {total}  failed: {failed}  skipped: {skipped}  incomplete work items: {len(incomplete)}")

    seen = set()
    for name, tests in sorted(failing.items()):
        print(f"\n{name}: {len(tests)} failed")
        if issues is None:
            for test, message in tests if show_all else tests[:10]:
                print(f"  {test}")
                if message:
                    print(f"      {message}")
            if not show_all and len(tests) > 10:
                print(f"  ... and {len(tests) - 10} more (--all lists them)")
            continue
        by_label = {}
        for test, message in tests:
            label, bad = classify(test, issues)
            unexpected |= bad
            seen.add(test)
            by_label.setdefault(label, []).append((test, message))
        for label, group in sorted(by_label.items(), key=lambda item: (item[0][0] == "#", item[0])):
            known = label.startswith("#")
            print(f"  {label}: {len(group)}")
            for test, message in group if (show_all or not known) else group[:3]:
                print(f"    {test}")
                if message and not known:
                    print(f"        {message}")
            if known and not show_all and len(group) > 3:
                print(f"    ... and {len(group) - 3} more")

    if issues is not None:
        fixed = [i for i in issues if i["open"] and not any(matches(p, t) for p in i["patterns"] for t in seen)]
        if fixed:
            print("\nopen issues with no failing test in this run (fixed, or their tests did not run):")
            for issue in fixed:
                print(f"  #{issue['number']} {issue['title']}")

    if incomplete:
        print("\nincomplete (rerun these work items):")
        for path in incomplete:
            print(f"  {path}")
    if issues is None:
        return 1 if failing or incomplete or unexpected else 0
    return 1 if unexpected or incomplete else 0


if __name__ == "__main__":
    args, issues_path, build_log, show_all = [], None, None, False
    argv = iter(sys.argv[1:])
    for arg in argv:
        if arg == "--all":
            show_all = True
        elif arg == "--issues":
            issues_path = next(argv, None)
        elif arg == "--build-log":
            build_log = next(argv, None)
        else:
            args.append(arg)
    if not args and not build_log:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(args, issues_path, build_log, show_all))
