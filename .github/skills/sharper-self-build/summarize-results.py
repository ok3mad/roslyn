#!/usr/bin/env python3
"""Summarizes the xUnit result files that RunTests writes, one per work item.

    python3 .github/skills/sharper-self-build/summarize-results.py [--all] artifacts/TestResults/<run> [<run2> ...]

Prints the totals, the assemblies with failures, every failing test with the first line of its message, and the
work items whose result file is empty or unreadable. Only the first 10 failures of an assembly are listed unless
--all is given. An empty file means the work item never finished (a killed or restarted run): rerun those assemblies
with RunTests --include. Exits 1 when a test failed or a work item is incomplete.

Pass several directories to merge a run with its reruns. A test that failed in one run and passed in a later one is
still listed; compare the runs before calling a failure fixed.
"""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def first_line(text, limit=160):
    text = (text or "").strip()
    for line in text.splitlines():
        line = line.strip()
        if line and line not in ("Expected:", "Actual:"):
            return line[:limit]
    return ""


def main(dirs, show_all):
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
                        failing.setdefault(name, []).append(
                            (test.get("name"), first_line(message.text if message is not None else "")))

    print(f"tests: {total}  failed: {failed}  skipped: {skipped}  incomplete work items: {len(incomplete)}")
    for name, tests in sorted(failing.items()):
        print(f"\n{name}: {len(tests)} failed")
        for test, message in tests if show_all else tests[:10]:
            print(f"  {test}")
            if message:
                print(f"      {message}")
        if not show_all and len(tests) > 10:
            print(f"  ... and {len(tests) - 10} more (--all lists them)")
    if incomplete:
        print("\nincomplete (rerun these work items):")
        for path in incomplete:
            print(f"  {path}")
    return 1 if failing or incomplete else 0


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if a != "--all"]
    if not args:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(args, "--all" in sys.argv[1:]))
