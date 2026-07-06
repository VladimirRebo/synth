# Loop Engineering in Synth

Synth is built *by a loop*, not by hand-prompting an agent turn by turn. This
doc is the operational adaptation of the pattern for this repo. Background and
sources: Addy Osmani, "Loop Engineering" (2026-06); AI4DEV write-up and demo.

## The idea
You stop being the **operator** who prompts the agent and become the **engineer**
who designs the loop: it finds work, delegates it, verifies it, records progress,
and decides the next step.

- **Inner loop** (the agent does it): context → plan → act → check → fix.
- **Outer loop** (you design it): triggers, limits, success criteria, stop
  conditions, and external memory. That outer loop is this repo.

## The three roles
| Role | Where | Does | Must NOT |
|------|-------|------|----------|
| **Orchestrator** | `.claude/skills/loop/SKILL.md`, `scripts/loop.sh` | worktree, commit, state | decide pass/fail |
| **Maker** | `.claude/agents/maker.md` | write code + tests | open PR, run validator, edit state |
| **Checker** | `.claude/agents/checker.md` + `scripts/validate.sh` | verdict from validator | write/fix code, invent rules |

**Maker and checker never see each other's work.** Verification is independent.

## The five components (+ memory)
1. **Automations** — for now, manual (`./scripts/loop.sh`) or `/loop` in a session.
   Later: cron / GitLab webhooks (see `[[webhook-pipeline-pattern]]` in the wiki).
2. **Worktrees** — one per task (`../synth-wt-<id>`, branch `fix/<id>`).
3. **Skills** — `.claude/skills/loop/SKILL.md` encodes the orchestration.
4. **Connectors** — MCP (GitLab/GitHub, CI). Not wired yet; PR creation is the
   first connector to add.
5. **Sub-agents** — maker / checker.
6. **Memory** — `loop/state/loop-state.md`.

## Success criterion = contract, not a wish
Every task file declares a machine-checkable contract enforced by
`scripts/validate.sh`:
- **Measurable goal** (e.g. tests pass, coverage ≥ N).
- **Verifiable check** — `acceptance_command` (exit 0) or `acceptance_criterion`
  (string present in `src/`).
- **Boundaries** — what must not change.
- **Limits** — max iterations / minutes.

The validator is the **single source of truth**. An LLM "looks good to me" is not
proof — that's the gotcha the demo calls out. Green on an *empty* validator is not
proof either.

## Maturity ladder (raise autonomy gradually)
1. **Manual** — every step watched. ← Synth is here.
2. **Task prep** — loop drafts/triages tasks, human runs them.
3. **Worktree edits** — loop implements in a worktree, human reviews the diff.
4. **PR checker** — loop opens PRs, an independent checker gates them.
5. **Partial auto-merge** — safe task classes merge without a human.

Do **not** jump straight to level 5.

## Risks to watch
Token/budget burn · false safety from tests (solutions fitted to the letter of
the check) · architectural drift · **comprehension debt** (understanding rots as
unread code ships) · **cognitive surrender** (accepting output blindly).

> Build the loop — but stay the engineer, not the person who just presses go.
