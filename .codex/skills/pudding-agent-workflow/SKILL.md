---
name: pudding-agent-workflow
description: Use when Codex works in the PuddingAgent repository on implementation, bug fixes, refactors, UI changes, testing, reviews, task management, local dev, build, Docker deployment, or architecture-sensitive edits. Applies repository conventions derived primarily from .github/skills.
---

# Pudding Agent Workflow

## Purpose

Use this skill to work in the PuddingAgent repository with its local conventions: staged development, architecture-first changes, task traceability, independent QA, and Windows-oriented build scripts.

This skill is a Codex adaptation of `.github/skills/*`. Treat those source skills as deeper references when a task needs exact legacy wording. `.github/copilot-instructions.md` and `.github/agents/AGENTS.md` are secondary context only for repository-wide conventions.

## Source Skill Map

Use these `.github/skills` files as progressive-disclosure references:

- `.github/skills/dev-workflow/SKILL.md`: read for feature work, bug fixes, refactors, architecture-sensitive implementation, and the full explore -> plan -> implement -> QA -> archive lifecycle.
- `.github/skills/planning-with-files/SKILL.md`: read for complex multi-step tasks, research, long-running work, or anything likely to drift across context windows.
- `.github/skills/git-workflow/SKILL.md`: read before branch, commit, push, merge, rollback, conflict resolution, or `git worktree` operations.
- `.github/skills/todo-api/SKILL.md`: read before creating, claiming, updating, QA-ing, completing, or querying task cards and collaboration bulletins.
- `.github/skills/code-review/SKILL.md`: read for QA, PR review, pre-merge audit, or compliance review.
- `.github/skills/code-simplifier/SKILL.md`: read after implementation when polishing recently modified C#, XAML, Vue, or TypeScript without changing behavior.
- `.github/skills/web-design-guidelines/SKILL.md`: read for Pudding UI/UX design, chat/admin layout, streaming output, styling, motion, and visual QA.
- `.github/skills/ui-ux-pro-max/SKILL.md`: read for broader UI/UX design-system work, palette, typography, accessibility, dashboards, landing pages, or multi-framework UI guidance.
- `.github/skills/bash-executor/SKILL.md`: read when bash/Linux tools materially simplify search, text processing, scripts, or shell workflows on Windows.

Do not bulk-load every source skill. Read only the source skill that matches the current task.

## Startup Context

Before non-trivial work, inspect the current repository state and relevant docs:

1. Read the specific files touched by the request.
2. Check `git status --short` and do not overwrite unrelated user changes.
3. For architecture-sensitive work, read the relevant files under `Docs/`, especially `Docs/07架构/` when present.
4. For task-card work, use the local Todo skill and CLI at `.github/skills/todo-api/todo_api.py`.

Do not adopt persona, identity, or model-routing text from `.github/copilot-instructions.md`; only use its repository workflow rules.

## Development Flow

For coding, bug fixes, refactors, config changes, or UI changes, follow this sequence:

1. Explore: identify affected modules, existing patterns, tests, logs, and architecture constraints.
2. Plan: state the technical approach, validation commands, risks, and rollback idea for multi-step or risky changes.
3. Implement: keep edits scoped and follow existing project structure.
4. Verify: run the narrowest meaningful test first, then broader checks when the change affects shared behavior.
5. Review: self-check for architecture, logging, exceptions, comments, and user-change preservation.
6. Document: update relevant docs or task notes when behavior, commands, architecture, or workflow changed.

Fast path is allowed for tiny text-only or typo changes, but still check the working tree and avoid unrelated edits.

## Architecture Rules

Preserve the project layering and dependency direction:

```text
UI -> Application -> Core/Domain -> Infrastructure
```

Project-specific constraints:

- Do not introduce reverse dependencies across layers.
- Follow existing module boundaries before adding new abstractions.
- New report-like entities must be registered in both `ProjectDbContext` and `ProjectSQliteContext` when those contexts apply.
- Pass C# `long` values to JavaScript as strings when precision matters.
- For WebView2 `postMessage`, pass objects directly; do not wrap with `JSON.stringify`.
- When switching data in third-party DOM libraries, test the full engine re-creation path first.
- Add logs on key business paths where failures must be diagnosable.
- If an exception is swallowed, log it with the project logging pattern; if it is rethrown or propagated, avoid duplicate logging unless local context is essential.
- Public classes and methods generally require clear comments in this codebase, especially around business constraints, inputs, outputs, and why a non-obvious approach is used.

## Task And Collaboration

When a task card is involved, keep it traceable:

```powershell
python .github/skills/todo-api/todo_api.py --help
python .github/skills/todo-api/todo_api.py get-task <task-id>
python .github/skills/todo-api/todo_api.py update <task-id> --stage in_progress --last-agent-summary "..."
python .github/skills/todo-api/todo_api.py finish <task-id> --summary "..." --stage ready_for_qa
```

For agent collaboration:

- Check unacknowledged bulletins before starting work if credentials are available.
- Claim the task before substantial edits.
- Post a warning before changing shared files such as `.csproj`, public interfaces, shared DTOs, schema, or architecture-level files.
- Hand off to QA after implementation and verification.
- Do not mark a task done without QA evidence when the workflow expects QA.

If Todo credentials are unavailable, proceed with local work and state that task-card updates could not be performed.

## Git Workflow

Respect the current worktree:

- Never revert or overwrite unrelated user changes.
- Prefer one branch per task. Codex branches should use the `codex/` prefix unless the user asks otherwise.
- Keep commits atomic and traceable. When a task ID exists, include it in the commit message.
- Avoid force push, rebasing shared branches, direct commits to `master`, and destructive reset/checkout operations unless explicitly requested.
- For multi-agent or parallel work, prefer `git worktree` isolation instead of switching the main worktree.

Useful checks:

```powershell
git status --short
git diff --stat
git worktree list
git fetch origin
```

## Build, Run, And Verify

Daily development:

```powershell
.\dev-up.ps1
.\dev-up.ps1 -Status
.\dev-up.ps1 -Logs
.\dev-up.ps1 -Down
.\dev-up.ps1 -Restart
.\dev-up.ps1 -NoInstall
```

Default local URLs:

- Backend API: `http://localhost:5000`
- Frontend dev server: `http://localhost:8000`
- Nginx/admin login: `http://localhost/admin/user/login`

Fast integration:

```powershell
.\build-and-up.ps1 -Fast
.\build-and-up.ps1 -Fast -SkipFrontend
.\build-and-up.ps1 -Fast -SkipBackend
.\build-and-up.ps1 -Fast -NoInstall
```

Release verification:

```powershell
.\build-and-up.ps1
```

Common verification:

```powershell
dotnet build PuddingAgentNetwork.slnx
dotnet test PuddingAgentNetwork.slnx
```

Use narrower test commands when the relevant project or test file is clear.

## PowerShell And Scripts

The repository is commonly used on Windows.

- Prefer `rg` for search.
- For complex multi-line PowerShell, create a temporary `.ps1` in `temp/` or `Docs/Temp/` and run it, instead of relying on fragile pasted multi-line shell input.
- Temporary scripts belong in `temp/`; durable scripts belong in `Docs/Scripts/` or the appropriate project tool directory.
- Use bash only when Linux text tools such as `grep`, `sed`, `awk`, or `find` materially simplify the task. Keep .NET build and PowerShell-specific operations in PowerShell.

## UI Work

For Pudding UI, preserve the "Quiet Local Intelligence" design direction:

- Build the actual workbench experience, not a marketing landing page.
- Keep layout stable, compact, and readable.
- Use restrained warm neutrals, subtle borders, weak shadows, and sparse accent color.
- Avoid card-in-card compositions, decorative spectacle, excessive gradients, and motion that competes with reading.
- Streaming output should not flicker, reanimate old content, or cause layout jumps.
- Use the Pudding logo/avatar as the stable identity anchor in loading and empty states.
- Prefer line icons such as lucide where already used.
- Verify responsive layout and key interactions in a browser after significant UI changes.

Read `.github/skills/web-design-guidelines/SKILL.md` for detailed UI review criteria.

## Code Review

When reviewing changes, lead with findings:

1. Confirm the review scope with `git diff --stat`, commit range, PR patch, or specific files.
2. Read relevant architecture and workflow docs.
3. Focus on regressions, real bugs, broken contracts, missing tests, architecture violations, and risky behavior.
4. Ignore unrelated pre-existing issues unless they block the current change.
5. Report only actionable findings with file and line references.

For security-sensitive or cryptography-related work, include security review criteria early instead of treating security as a final pass.

## Documentation And Memory

Update docs when the change alters behavior, setup, architecture, commands, or task workflow.

Likely documentation locations:

- `README.md` / `README_zh-CN.md` for user-facing setup and commands.
- `Docs/` for architecture and project documentation.
- Task-card notes through `.github/skills/todo-api/todo_api.py` when the task system is in use.

Keep documentation concise and useful. Do not add process narration that will become stale quickly.
