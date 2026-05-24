---
name: dev-workflow
description: "Use when Codex implements any feature, bug fix, refactor, code review follow-up, architecture-sensitive change, or other code modification in the PuddingAgent repository. Enforces the Pudding development lifecycle adapted from .github/skills/dev-workflow/SKILL.md: design, explore, plan, implement, debug, QA, and archive. Routes development implementation subagent tasks to gpt-5.3-codex when subagents are available."
---

# Dev Workflow

## Purpose

Use this skill for PuddingAgent development work before changing code. It is the Codex version of `.github/skills/dev-workflow/SKILL.md`, adapted to Codex tools and repository conventions.

Core rule:

```text
Design first. Test first when behavior changes. Investigate root cause before fixing bugs.
```

## Source Reference

The source skill is `.github/skills/dev-workflow/SKILL.md`.

Read that file when exact legacy workflow wording is needed. This Codex skill is the operative version for local Codex work.

Related deeper references:

- `.github/skills/git-workflow/SKILL.md` for branch, commit, merge, push, rollback, and worktree rules.
- `.github/skills/todo-api/SKILL.md` for task cards, claim/release, QA gates, and bulletins.
- `.github/skills/planning-with-files/SKILL.md` for long-running or multi-step plans.
- `.github/skills/code-review/SKILL.md` for QA and pre-merge reviews.
- `.github/skills/code-simplifier/SKILL.md` for post-green cleanup.
- `.github/skills/web-design-guidelines/SKILL.md` for UI work.

## Iron Laws

```text
NO CODE WITHOUT A DESIGN CHECK FIRST
NO BEHAVIORAL PRODUCTION CODE WITHOUT A FAILING TEST FIRST, WHEN A TEST IS PRACTICAL
NO FIX WITHOUT ROOT CAUSE INVESTIGATION FIRST
```

Apply the spirit, not just the wording. If a test is impractical, explain why and use the smallest meaningful verification path.

## Lifecycle

```text
Design Check -> Explore -> Plan -> Implement -> Verify -> QA/Review -> Archive
                         ^                         |
                         +------ Debug if failing --+
```

| Stage | Output | Gate |
| --- | --- | --- |
| Design Check | Brief goal, approach, constraints | User intent understood |
| Explore | Impacted files, patterns, constraints | Enough context to avoid guessing |
| Plan | Technical steps, tests, risks | Clear path for non-trivial work |
| Implement | Code plus focused tests | Verification passes |
| Debug | Root-cause evidence | Fix targets confirmed cause |
| QA/Review | Findings or pass statement | No known blocking issue |
| Archive | Docs/task notes when needed | Handoff context preserved |

## Stage 0: Design Check

Before editing code:

1. Restate the goal in concrete terms when the request is ambiguous or risky.
2. Identify likely affected modules and behavioral surface.
3. Confirm constraints from existing architecture and tests.
4. For substantial work, choose an approach and note tradeoffs before editing.

Ask the user only when local context cannot resolve a material ambiguity. For small, clear requests, proceed with a reasonable assumption and state it if it matters.

## Stage 1: Explore

Gather enough context before implementation:

```powershell
git status --short
rg "<relevant symbol or text>" Source Tests Docs
rg --files Source Tests Docs
```

Explore for:

- Existing implementation patterns.
- Relevant tests and test helpers.
- Architecture layer boundaries.
- Shared contracts, DTOs, schemas, and public interfaces.
- Recent related changes when useful.

Do not overwrite unrelated user changes shown by `git status`.

## Stage 2: Plan

Use a concise plan for work with multiple steps, cross-module impact, or uncertain risk.

Include:

- Files or modules likely to change.
- Test strategy and expected command.
- Risk and rollback note for high-impact changes.
- Documentation or task-card updates if needed.

For UI work, read `.github/skills/web-design-guidelines/SKILL.md` before implementation. For security or cryptography-sensitive work, include review criteria before coding.

## Development Subagents

For development implementation work, use a subagent when the task can be isolated and subagent tooling is available.

Default model for development subagents:

```text
gpt-5.3-codex
```

Use this routing for:

- Feature implementation after the design check and plan are clear.
- Bug-fix implementation after root cause is established.
- Refactors with well-defined behavior preservation.
- Focused test creation or test repair tied to an implementation task.

Keep the lead Codex agent responsible for:

- Initial design check and scope control.
- Reviewing subagent output before applying or accepting it.
- Integration decisions across modules.
- Final verification and final user response.

Do not use the same implementation subagent as the independent QA reviewer. QA/review must remain a separate pass.

## Stage 3: Implement

Keep changes scoped:

- Follow existing project structure and naming.
- Preserve architecture direction: `UI -> Application -> Core/Domain -> Infrastructure`.
- Avoid mixing feature work with broad cleanup.
- Add key-path logging where diagnosis would otherwise be hard.
- Log swallowed exceptions; avoid duplicate logs for exceptions that continue upward unless local context is essential.
- Preserve or add class/method comments where the codebase expects them, especially for public APIs and non-obvious business constraints.
- Mark intentional follow-up debt with a task-linked `TODO(...)` or `FIXME(...)` only when a real follow-up exists.

Use `apply_patch` for manual edits. Do not revert unrelated files.

## TDD Rule

For behavior changes:

```text
RED: Write or adjust a focused failing test.
GREEN: Implement the smallest change that passes.
REFACTOR: Clean up while tests stay green.
```

If no practical automated test exists, use the closest meaningful verification: targeted build, manual browser check, API call, script run, or documented reasoning. State the gap.

## Stage 4: Debug

When a bug, test failure, or unexpected behavior appears, do not guess.

Use this sequence:

1. Read the full error and reproduction path.
2. Check recent changes and relevant existing code.
3. Compare with similar working paths.
4. Form one hypothesis.
5. Test with the smallest change or diagnostic.
6. Fix only after the cause is supported by evidence.

After three failed attempts, stop and reassess assumptions before continuing.

## Stage 5: Verify

Run the narrowest meaningful verification first, then broaden when the change affects shared behavior.

Common commands:

```powershell
dotnet build PuddingAgentNetwork.slnx
dotnet test PuddingAgentNetwork.slnx
.\dev-up.ps1 -Status
.\build-and-up.ps1 -Fast
```

For frontend changes, use the project’s existing package scripts where applicable and verify significant UI changes in a browser.

Report commands that could not be run and why.

## Stage 6: QA And Review

Before calling work complete:

- Review the diff yourself.
- Check changed behavior against the original request.
- Check architecture layering and public contracts.
- Check tests cover the risky path.
- Remove debug code and temporary artifacts.

For explicit review tasks, use code-review style: findings first, file/line references, severity, and actionable fixes. Do not focus on unrelated pre-existing issues.

## Stage 7: Archive

Update persistent project context only when useful:

- Update task cards through `.github/skills/todo-api/todo_api.py` when a task ID and credentials are available.
- Update docs when behavior, setup, commands, architecture, or workflow changed.
- Record important verification evidence in the final answer.

Task-card examples:

```powershell
python .github/skills/todo-api/todo_api.py get-task <task-id>
python .github/skills/todo-api/todo_api.py update <task-id> --stage in_progress --last-agent-summary "..."
python .github/skills/todo-api/todo_api.py finish <task-id> --summary "Implemented and verified" --stage ready_for_qa
```

If credentials are unavailable, do not block local implementation; state that task-card updates were skipped.

## Fast Path

For typo fixes, small comments, or low-risk single-file edits:

1. Check `git status --short`.
2. Inspect the target file.
3. Apply the minimal edit.
4. Run a focused verification if one is cheap and relevant.

Do not use the fast path for schema changes, public API changes, security-sensitive code, cross-module behavior, or UI workflows.

## Stop Signals

Return to the right stage if any of these happen:

- Coding starts before the goal and affected surface are understood.
- A fix is based on a hunch rather than evidence.
- Tests pass unexpectedly before the intended failing test is confirmed.
- A change expands beyond the requested scope.
- A shared file is being changed without checking downstream impact.
- Verification is skipped without a clear reason.

## Final Response Checklist

In the final response, include:

- What changed, with file links.
- What verification ran and the result.
- Any skipped verification or known residual risk.
- Any user-visible next step only when it materially helps.
