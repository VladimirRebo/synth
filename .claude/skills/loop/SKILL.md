---
name: loop
description: Orchestrate one iteration of Walter's agent loop — pick a task, isolate it in a git worktree, delegate to the maker, verify with the checker, then act (commit + record) or record the failure. Use when the user says "run the loop", "/loop", or asks to autonomously work a task from loop/tasks/.
---

# Loop orchestrator

You are the **orchestrator** of Walter's agent loop. You manage the worktree, commits, and state — but you never decide yourself whether a task passed; the validator (via the checker) does.

Read `AGENTS.md` and `docs/loop-engineering.md` first.

## One iteration: discover → isolate → make → verify → act

1. **Discover** — Pick the task to work. If the user named a task file, use it. Otherwise take the first task under `loop/tasks/` whose status is `open` and is not already in `loop/state/loop-state.md` under *Completed*.

2. **Isolate** — Create an isolated worktree and branch for the task:
   ```
   git worktree add ../Walter-wt-<task-id> -b fix/<task-id>
   ```
   All maker work happens there. Never let two tasks share a worktree.

3. **Make** — Delegate to the `maker` subagent with the task file. The maker implements the change and tests in the worktree and returns the `CHANGED/TEST_RESULT/BUILD_RESULT/RULES_SATISFIED` report. You do not review its code for correctness — that is the checker's role via the validator.

4. **Verify** — Delegate to the `checker` subagent. It runs `./scripts/validate.sh <task-id>` in the worktree and returns `VERDICT: PASS|FAIL`. The maker and checker must stay independent — do not pass the maker's reasoning to the checker.

5. **Act**
   - **PASS** → commit in the worktree (`git -C ../Walter-wt-<task-id> add -A && commit` with a conventional message + `Task: <task-id>`). Leave the branch for review/PR (opening a PR is a connector step, not yet enabled). Append to *Completed* in `loop/state/loop-state.md`.
   - **FAIL** → do NOT commit. Append to *Failures* in `loop/state/loop-state.md` with the checker's evidence. Remove the worktree (`git worktree remove --force ../Walter-wt-<task-id>`) unless the user wants to inspect it.

6. **Record & report** — Update `loop/state/loop-state.md` (move the task out of *In Progress*). Report to the human in Russian: what was attempted, the verdict, and the next task in the queue.

## Guardrails
- Honor the task's limits (max iterations / time). Stop and report if exceeded — do not loop forever.
- One task per run unless the user explicitly asks to drain the queue.
- If `scripts/validate.sh` has no real checks yet (empty scaffold), say so — a green run on an empty validator is **not** proof of correctness.
- You may call `scripts/loop.sh` to run the whole thing as a script instead of orchestrating by hand.
