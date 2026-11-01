#!/usr/bin/env python3
"""Replays one project's compile from a Sharper-mode build without /features:sharper, to tell whether a compiler
warning or error comes from Sharper or from the code itself.

    python3 .github/skills/sharper-self-build/replay-compile.py <project name> [<target framework>]
    python3 .github/skills/sharper-self-build/replay-compile.py Microsoft.CodeAnalysis.CSharp.Semantic.UnitTests net10.0

It finds the project's command line in artifacts/log/Debug/vbcscompiler.log (written by build.sh --binaryLog), drops
/features:sharper, sends the outputs to a temporary directory (deleted afterwards) and compiles with the SDK's own
csc, not the bootstrap compiler. It prints the diagnostics; compare them with the Sharper-mode build. artifacts/bin
and artifacts/obj are not touched, so the Sharper-mode binaries stay usable for tests.

The output assembly keeps its file name: InternalsVisibleTo matches assembly names, and a renamed test assembly
loses access to the compiler's internals.
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
LOG = REPO / "artifacts/log/Debug/vbcscompiler.log"


def find_response_file(project, framework):
    request = f"Compilation request {project} ({framework})"
    lines = LOG.read_text(encoding="utf-8", errors="replace").splitlines()
    found = None
    for i, line in enumerate(lines):
        if request in line:
            for following in lines[i:i + 10]:
                if "BuildResponseFile = '" in following:
                    found = following.split("BuildResponseFile = '", 1)[1].rstrip("'")
                    break
    return found  # the last compile of the project wins


def main(project, framework):
    with tempfile.TemporaryDirectory(prefix="replay-") as out:
        return replay(project, framework, Path(out))


def replay(project, framework, out):
    if not LOG.exists():
        sys.exit(f"{LOG} not found: build with ./build.sh --binaryLog")
    rsp = find_response_file(project, framework)
    if rsp is None:
        sys.exit(f"no compile of {project} ({framework}) in {LOG}")

    (out / "ref").mkdir()
    rsp = re.sub(r"\s/features:sharper(?=\s|$)", "", rsp)
    for switch in ("out", "doc", "pdb"):
        rsp = re.sub(rf"/{switch}:(\S+)", lambda m, s=switch: f"/{s}:{out / Path(m.group(1)).name}", rsp)
    rsp = re.sub(r"/refout:(\S+)", lambda m: f"/refout:{out / 'ref' / Path(m.group(1)).name}", rsp)
    rsp = re.sub(r"/errorlog:\S+", "", rsp)
    (out / "replay.rsp").write_text(rsp, encoding="utf-8")

    dotnet = REPO / ".dotnet/dotnet"
    csc = sorted((REPO / ".dotnet/sdk").glob("*/Roslyn/bincore/csc.dll"))
    if not dotnet.exists() or not csc:
        sys.exit("the repo-local SDK in .dotnet is missing: run ./build.sh --restore first")
    print(f"replaying {project} ({framework}) without /features:sharper with {csc[-1]}", flush=True)
    result = subprocess.run([str(dotnet), "exec", str(csc[-1]), "-noconfig", f"@{out / 'replay.rsp'}"],
                            cwd=REPO, capture_output=True, text=True)
    diagnostics = sorted({line.strip() for line in result.stdout.splitlines()
                          if re.search(r": (warning|error) ", line)})
    for line in diagnostics:
        print(line)
    print(f"{len(diagnostics)} diagnostics, csc exit code {result.returncode}")
    return result.returncode


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else "net10.0"))
