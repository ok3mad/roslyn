---
name: sharper-triage
description: "Triage the Sharper backlog and keep a small, steady amount of work in flight: read the open issues, PRs and Claude sessions, decide what is ready, and start at most a few cloud sessions so that two discussion threads and three to five work items are being progressed. Bugs and regressions come first, then new syntax. Use when: asked to 'triage', 'triage the backlog', 'do a triage round', 'what should we work on next', or to start work or discussion sessions for Sharper issues."
---

# Triaging the Sharper Backlog

A triage round looks at everything in flight, decides what is ready, and tops the pipeline up to its targets.
Nothing more: it does not merge PRs, archive sessions, or do the work itself. Each cloud session costs the user's
token budget while it runs, so a round starts as few sessions as the targets allow, and never more than the limits
below.

## Targets and limits

| What | Target after the round | Hard limit |
| --- | --- | --- |
| Discussion threads (sessions the user drives to brainstorm or decide a design) | exactly 2 open | Start one only to get back to 2 |
| Work items being progressed (running work sessions plus work PRs waiting for review or merge) | 3 to 5 | — |
| Running work sessions | — | **At most 3, counting the ones already running.** A round never starts a work session that would make it 4 |

- **Running** means the session's `session_status` is `SESSION_STATUS_RUNNING` (it is using tokens now). An idle
  session waiting for the user, or for its PR to be reviewed, is not running, but its open PR still counts as a work
  item in progress.
- If work items are already at 5, start no work session even when fewer than 3 are running: the bottleneck is review,
  so say which PRs are waiting.
- If work items are below 3 and 3 sessions are already running, start nothing and say so. Never exceed the limit to
  hit the target.
- A discussion session runs a short research pass and then waits for the user, so it is cheap. Still, start at most
  one per gap, and only when fewer than 2 discussion sessions are open (not archived).

## Priorities for work sessions

Pick work items in this order, and fill free slots from the top:

1. **Regressions:** issues labelled `regression`, and anything that breaks valid C# with Sharper on or off (design rule
   2), the build, or a test suite on `main`. Also check what running or idle sessions report as failures.
2. **Other bugs:** issues labelled `bug`, and follow-ups that describe wrong behavior (a wrong diagnostic location, a
   crash, a public API break), not missing features.
3. **New syntax:** `language-feature` issues, and the step issues of a large row, in the
   [suggested order](../../../docs/sharper/planning/implementation-plan.md#suggested-order).
4. **Everything else:** verification, diagnostics polish, API shape, IDE support, performance, docs.

Within one level, prefer the item that unblocks others (a step others list as "After X"), then the smaller one.

## Readiness: when an issue can go to a work session

All of these must hold. Otherwise the issue is either a discussion candidate or waits.

- **Decided.** No `needs-decision` or `needs-discussion` label, `certainty-med` or higher, and no open question in the
  issue or its comments that changes what gets built. An open point the issue itself says can be settled while
  building is fine.
- **Unblocked.** Everything it is "After" or "Builds on" is merged into `main` (check the issue body and the roadmap's
  Suggested order). Rows that wait for unions (row 19) or purity (row 24) are not ready.
- **Not already taken.** No open PR closes it, and no non-archived session works on it (match the issue number in
  session titles and in PR bodies).
- **No collision.** It does not change the same code as a running session in a way that makes both PRs conflict
  heavily (two parser changes to `ScanType`, two changes to the same binder method). Sessions that only both add a
  diagnostic code are fine: the later PR renumbers when it merges `main`.

## Discussion candidates

Pick what most blocks the roadmap, in this order:

1. A `needs-decision` issue that blocks the next ready syntax row, or a step issue of it.
2. The next roadmap row whose certainty is `certainty-low`, or whose spelling is still a "leading candidate".
3. Any other `needs-discussion` or `needs-decision` issue, oldest first.

Before starting a discussion session, check that the issue has a concrete proposal to react to. If it does not, the
session's first job is to write one (see the prompt template), not to ask the user open questions.

## The round

### 1. Read the current state (cheap calls first)

- **Sessions:** `list_sessions` (claude-code-remote MCP). Keep the ones that are not archived. Classify each as
  *work* or *discussion* from its title (discussion sessions end in `(discussion)`) and its tags (`triage:work`,
  `triage:discussion`), and note its status and the issue numbers in its title. Use `get_session` only for sessions
  whose state is unclear; do not read session transcripts unless a status summary reports a failure.
- **PRs:** open PRs (`list_pull_requests`, state open). For each: which issue it closes, which session made it
  (the `session_...` link in its footer), and whether it merges cleanly.
- **Issues:** open issues with labels only (`list_issues` with `fields: number, title, labels`). Read bodies and
  comments only for the candidates you are about to choose between, and for issues updated since the last round.
- **Roadmap:** the Suggested order and the work breakdowns in `docs/sharper/planning/implementation-plan.md` on
  `main`.

### 2. Count

Write down: open discussion sessions; running work sessions; work items in progress (running work sessions plus open
work PRs not yet merged, without counting a session and its own PR twice). From the table above, work out how many
work and discussion sessions this round may start. That number may be zero.

### 3. Choose

Rank the ready work items by the priorities above and take as many as the count allows. Pick discussion topics to
fill the gap to 2. If nothing is ready, say what blocks the top candidates.

### 4. Start sessions

Start each with `create_session` (claude-code-remote MCP): `source_url` `https://github.com/ok3mad/roslyn`, an
`outcome_branch` named `sharper/<short-topic>`, tags `triage:work` or `triage:discussion` plus `issue:<n>`, and a
title `Sharper #<n>: <topic>`, with ` (discussion)` at the end for a discussion session. One issue per work session;
two issues only when one fix closes both.

Work session prompt, filled in:

```
Implement Sharper issue https://github.com/ok3mad/roslyn/issues/<n>: <one line>.

Start by following AGENTS.md: read .github/copilot-instructions.md, .github/memory/INDEX.md, docs/sharper/README.md,
the implementation plan sections <which>, and the issue with its comments. Use the `sharper-feature` skill.

Scope: <from the issue: what is in, what is out, the decisions already made, and the open points the session decides
and records in the plan>.

Diagnostics: main uses up to CS<last>. Take the next free number; if another PR takes it first, renumber when you
merge main.

Be economical: read only the code you need, and run targeted tests while iterating. Run the full Sharper tests (also
with ROSLYN_TEST_IOPERATION=1), the regression projects for the areas you touched, and the analyzer build once, before
opening the PR.

Done when the tests pass, the docs (README, plan, feature page) and the skill are updated, and update-agent-docs has
been run. Then push branch sharper/<topic> and open a PR against main that says "Closes #<n>". Report test results
faithfully. Do not merge the PR.
```

Discussion session prompt, filled in:

```
This session is for Sharper issue https://github.com/ok3mad/roslyn/issues/<n>: <one line>. The user drives it: first
the design discussion, implementation only after they decide.

Orient: AGENTS.md, .github/copilot-instructions.md, .github/memory/INDEX.md, docs/sharper/README.md, the plan's
roadmap and Suggested order, the brainstorm sections <which>, and the issue with its comments.

Before reporting back, check the open questions against the code and cite it: <the concrete questions: how the parser
treats the syntax today with the flag off, which existing node or binder path it could reuse, interactions with the
merged and in-progress features>.

Then give the user a short summary: the decisions to make, your recommendation and evidence for each, and an
implementation sketch with a size estimate. Do not write compiler code, comment on GitHub, or open a PR until the
user decides. Keep the research pass short; the user will ask for more where they need it.
```

### 5. Report

Reply to the user with:

- The counts before and after the round, against the targets.
- The sessions started: id, issue, kind, branch, and why each was chosen over the next candidate.
- PRs waiting for review or merge, oldest first, with any that conflict with `main`.
- Sessions that look finished (all their PRs merged) or stuck (a failure in their status), as candidates for the
  user to archive or nudge. Do not archive or message them yourself unless asked.
- The top ready items that did not fit, so the next round can start them.
- Bugs or regressions found while reading that have no issue yet: propose the issue, and file it only if the user
  asks.
