---
description: Run one iteration of Synth's agent loop over a task (discover → isolate → make → verify → act)
argument-hint: "[task-id or path; empty = first open task]"
allowed-tools: Read, Bash, Glob, Grep, Task
---

Run one iteration of the Synth agent loop using the `loop` skill.

Target task: **$ARGUMENTS** (if empty, pick the first `open` task under `loop/tasks/` that is not already Completed in `loop/state/loop-state.md`).

Follow the orchestration in `.claude/skills/loop/SKILL.md` exactly:
1. Discover the task and read its acceptance contract.
2. Isolate it in a fresh git worktree on branch `fix/<task-id>`.
3. Delegate implementation to the **maker** subagent.
4. Delegate verification to the **checker** subagent (deterministic validator only).
5. Act on the verdict: PASS → commit + record Completed; FAIL → record failure + clean worktree.

Keep maker and checker independent. Do not decide pass/fail yourself — the validator does. Report the outcome and the next queued task in Russian.
