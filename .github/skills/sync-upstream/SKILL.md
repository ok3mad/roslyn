---
name: sync-upstream
description: "Merge dotnet/roslyn main into the Sharper fork: fetch upstream, merge it on a branch, resolve conflicts with the Sharper changes, turn off the triggers of any new or changed upstream GitHub workflow, validate, and open the PR. Use when: asked to 'sync with upstream', 'merge upstream', 'update from dotnet/roslyn', or when upstream changes are needed. Also use when a workflow under .github/workflows runs on its own although it should not."
---

# Syncing the Sharper Fork with Upstream Roslyn

The fork's `main` is upstream `dotnet/roslyn` `main` plus the Sharper changes (`docs/sharper/README.md`). Syncing
merges upstream into it. It never rebases: upstream history stays as it is, and every sync is one merge commit.

## Workflows inherited from upstream

Upstream's GitHub workflows (labelers, backport, agentic workflows, PR validation) target `dotnet/roslyn`: its
labels, its Azure pipelines, its org. In the fork they only burn Actions runs, so their triggers are turned off.
Each trigger under `on:` other than `workflow_dispatch` is commented out with a `# sharper-off: ` prefix, and
`workflow_dispatch:` is added where missing, so the workflow can still be started by hand from the Actions tab.
The fork's own workflows are named `sharper-*.yml` and keep their triggers.

[`disable-workflows.py`](disable-workflows.py) does this, idempotently:

```bash
python3 .github/skills/sync-upstream/disable-workflows.py          # rewrite
python3 .github/skills/sync-upstream/disable-workflows.py --check  # exit 1 if anything would change
```

Every sync must end with `--check` passing, since upstream adds and edits workflows often.

## Steps

1. **Start clean.** The working tree must be clean; otherwise stop and ask.
   ```bash
   git remote get-url upstream || git remote add upstream https://github.com/dotnet/roslyn.git
   git fetch upstream main
   git fetch origin main
   git checkout -b sharper/sync-upstream-$(date -u +%Y%m%d) origin/main
   ```
2. **See what is coming.** Note the size of the merge and the new workflows, for the PR description:
   ```bash
   base=$(git merge-base HEAD upstream/main)
   git log --oneline $base..upstream/main | wc -l
   git diff --name-status $base upstream/main -- .github/workflows
   ```
3. **Merge.** `git merge --no-ff upstream/main -m "Merge upstream dotnet/roslyn main"`.
4. **Resolve conflicts.**
   - **Workflows:** take upstream's version (`git checkout --theirs -- <file>`), then run `disable-workflows.py`. Never
     hand-merge the commented triggers. A workflow upstream deleted stays deleted.
   - **`.xlf`, `.resx`, generated compiler code:** follow the [`merge-into-branch`](../merge-into-branch/SKILL.md) skill.
     Regenerate generated files with the repo's tooling, never by hand.
   - **Sharper code** (binder, parser, lowering, `ErrorCode.cs`, `MessageID.cs`, `SyntaxKind.cs`, `Syntax.xml`,
     `PublicAPI.Unshipped.txt`): keep both sides. Sharper diagnostics stay in their own block from CS20001 and Sharper
     syntax kinds from 9900 (see the [`sharper-feature`](../sharper-feature/SKILL.md) skill), so a conflict there is
     usually two appended blocks. When upstream rewrote code a Sharper change hooks into, re-apply the Sharper change to
     the new code and re-run its tests; do not keep the old upstream code.
   - **Agent docs** (`.github/copilot-instructions.md`, `.github/memory/`, `AGENTS.md`): keep upstream's changes and
     the Sharper additions.
5. **Turn off workflows.** Run `disable-workflows.py` even when nothing conflicted (upstream may have added or
   changed a workflow cleanly), then `--check`. Also look for other new automation under `.github/` (for example
   `dependabot.yml`) and list it in the PR for the owner to decide.
6. **Validate.**
   - `./build.sh --restore --build`.
   - The Sharper tests, with and without `ROSLYN_TEST_IOPERATION=1` (command in `docs/sharper/README.md`).
   - The analyzer build of the C# compiler (`-p:RunAnalyzersDuringBuild=true`; see the `sharper-feature` skill). It
     catches public API files that the merge left inconsistent (RS0016, RS0017).
   - A build can rewrite checked-in generated files, such as
     `src/Tools/SemanticSearch/ReferenceAssemblies/Apis/*.txt`. Commit what the build regenerates, so `main` stays
     in sync with its sources.
   - A failure that also happens on upstream `main` is not the merge's: note it in the PR, do not fix upstream code.
7. **Open the PR** titled "Merge upstream dotnet/roslyn (<date>)". In the description: the upstream commit merged, the
   number of commits, the conflicts and how each was resolved, the workflows that were turned off (new ones by name),
   and the test results.
8. **Merge it with a merge commit**, never squash or rebase: squashing would flatten upstream's history into one
   commit and make the next sync conflict on everything.

After merging, run the `update-agent-docs` skill when the sync changed something the agent docs describe.
