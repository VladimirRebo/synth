# Tasks

One task = one file = one contract the loop will try to satisfy.

Create tasks with `./scripts/new-task.sh "summary"` (or copy `WALTER-1.md`).

## The contract (frontmatter)
Every task **must** define one of:
- `acceptance_command` — a shell command that must exit `0` (strongest; e.g. `dotnet test`).
- `acceptance_criterion` — a string that must appear in `src/` (weakest; use only when a command isn't practical).

A task with neither will **fail closed** — the loop refuses to pass something it can't verify.

Also set:
- `status: open` — the loop only picks up `open` tasks not already recorded in `loop/state/loop-state.md`.
- `boundaries` — what the maker must not touch.
- `limits` — max iterations / minutes, so a stuck loop stops.

## Lifecycle
`open` → picked by `loop.sh` → worktree `fix/<id>` → maker → `validate.sh` → PASS: committed + recorded *Completed* / FAIL: recorded *Failures*.
