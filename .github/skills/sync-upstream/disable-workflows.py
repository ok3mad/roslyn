#!/usr/bin/env python3
"""Turns off the automatic triggers of the workflows inherited from dotnet/roslyn.

In every .github/workflows/*.yml (and *.yaml) except the fork's own sharper-*.yml, each trigger under `on:` other than
`workflow_dispatch` is commented out with a `# sharper-off: ` prefix, and `workflow_dispatch:` is added when the
workflow has none, so the workflow still exists but only runs when started by hand.

The script is idempotent. Run it after every upstream merge:

    python3 .github/skills/sync-upstream/disable-workflows.py          # rewrite the files
    python3 .github/skills/sync-upstream/disable-workflows.py --check  # exit 1 if a file would change

To turn a workflow back on, delete the `# sharper-off: ` prefixes in it and add its name to KEEP below.
"""

import re
import sys
from pathlib import Path

WORKFLOWS = Path(__file__).resolve().parents[2] / "workflows"
# Workflows that keep their triggers: the fork's own.
KEEP = re.compile(r"^sharper-.*\.ya?ml$")
MARK = "# sharper-off: "
NOTE = "# Sharper: triggers other than workflow_dispatch are turned off (.github/skills/sync-upstream)."


def disable(text: str) -> str:
    lines = text.split("\n")
    start = next((i for i, line in enumerate(lines) if re.match(r"^(on|\"on\"|'on'):", line)), None)
    if start is None:
        raise ValueError("no top-level 'on:'")

    inline = lines[start].split(":", 1)[1].split("#", 1)[0].strip()
    if inline:
        if inline == "workflow_dispatch":
            return text
        # on: push / on: [push, pull_request]
        lines[start:start + 1] = [MARK + lines[start], "on:", "  " + NOTE, "  workflow_dispatch:"]
        return "\n".join(lines)

    # The block runs until the next line that starts at column 0 and is not a comment.
    end = start + 1
    while end < len(lines) and not re.match(r"^[^\s#]", lines[end]):
        end += 1
    block = lines[start + 1:end]

    keys = [line for line in block if line.strip() and not line.lstrip().startswith("#")]
    indent = min((len(line) - len(line.lstrip()) for line in keys), default=2)
    prefix = " " * indent

    result = []
    has_dispatch = False
    current = None
    for line in block:
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            result.append(line)
            continue
        if len(line) - len(line.lstrip()) == indent:
            current = stripped.split(":", 1)[0].strip()
            has_dispatch |= current == "workflow_dispatch"
        result.append(line if current == "workflow_dispatch" else prefix + MARK + line[indent:])

    header = []
    if not any(line.strip() == NOTE for line in block):
        header.append(prefix + NOTE)
    if not has_dispatch:
        header.append(prefix + "workflow_dispatch:")

    lines[start + 1:end] = header + result
    return "\n".join(lines)


def main() -> int:
    check = "--check" in sys.argv[1:]
    changed = []
    for path in sorted(WORKFLOWS.glob("*.y*ml")):
        if KEEP.match(path.name):
            continue
        text = path.read_text(encoding="utf-8")
        new = disable(text)
        if new != text:
            changed.append(path.name)
            if not check:
                path.write_text(new, encoding="utf-8")
    for name in changed:
        print(("would change " if check else "disabled triggers in ") + name)
    return 1 if check and changed else 0


if __name__ == "__main__":
    sys.exit(main())
