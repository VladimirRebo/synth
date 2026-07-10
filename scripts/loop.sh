#!/usr/bin/env bash
#
# loop.sh — one iteration of Synth's agent loop.
#   discover → isolate → make → verify → act
#
# Usage:
#   ./scripts/loop.sh [task-id|path]      # run one task (first open task if omitted)
#   DEMO_MODE=true ./scripts/loop.sh      # exercise the plumbing without calling `claude`
#
# Env:
#   DEMO_MODE=true         skip the maker LLM call; just wire worktree + validator
#   CLAUDE_MODEL=...        model for the maker (default: claude-opus-4-8)
#   PERMISSION_MODE=...     claude permission mode (default: bypassPermissions)
#   KEEP_WORKTREE=true      don't remove the worktree on FAIL (for inspection)
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEMO_MODE="${DEMO_MODE:-false}"
CLAUDE_MODEL="${CLAUDE_MODEL:-claude-opus-4-8}"
PERMISSION_MODE="${PERMISSION_MODE:-bypassPermissions}"
KEEP_WORKTREE="${KEEP_WORKTREE:-false}"
STATE="$ROOT/loop/state/loop-state.md"

c_blue='\033[1;34m'; c_yellow='\033[1;33m'; c_red='\033[1;31m'; c_green='\033[1;32m'; c_off='\033[0m'
log()  { printf "${c_blue}[loop]${c_off} %s\n" "$*"; }
ok()   { printf "${c_green}[loop]${c_off} %s\n" "$*"; }
warn() { printf "${c_yellow}[loop]${c_off} %s\n" "$*" >&2; }
die()  { printf "${c_red}[loop]${c_off} %s\n" "$*" >&2; exit 1; }

command -v git >/dev/null   || die "git not found"

# ---------------------------------------------------------------- discover ----
TASK_ARG="${1:-}"
if [ -n "$TASK_ARG" ]; then
  TASK_FILE="$TASK_ARG"; [ -f "$TASK_FILE" ] || TASK_FILE="$ROOT/loop/tasks/$TASK_ARG.md"
else
  TASK_FILE=""
  for f in "$ROOT"/loop/tasks/*.md; do
    [ -e "$f" ] || continue
    [ "$(basename "$f")" = "README.md" ] && continue
    id="$(basename "$f" .md)"
    grep -qiE "^status:[[:space:]]*open" "$f" || continue
    grep -qF "| $id |" "$STATE" 2>/dev/null && continue   # already recorded, skip
    TASK_FILE="$f"; break
  done
fi
[ -n "${TASK_FILE:-}" ] && [ -f "$TASK_FILE" ] || die "no open task found — add one under loop/tasks/ or pass a task id"
TASK_ID="$(basename "$TASK_FILE" .md)"
log "discovered task: ${TASK_ID}"

# ----------------------------------------------------------------- isolate ----
WT="$ROOT/../synth-wt-${TASK_ID}"
BRANCH="fix/${TASK_ID}"
if git -C "$ROOT" worktree list --porcelain | grep -qF "$(cd "$ROOT/.." && pwd)/synth-wt-${TASK_ID}"; then
  warn "worktree already exists, reusing: $WT"
else
  git -C "$ROOT" worktree add "$WT" -b "$BRANCH" >/dev/null 2>&1 \
    || git -C "$ROOT" worktree add "$WT" "$BRANCH" >/dev/null \
    || die "failed to create worktree"
  log "isolated in worktree: $WT (branch $BRANCH)"
fi

record_state() {  # $1=section  $2=note
  local ts="manual-run"  # timestamps are added by the human/CI; loop.sh stays deterministic
  printf -- '- | %s | %s | %s |\n' "$TASK_ID" "$1" "$2" >> "$STATE"
}

# -------------------------------------------------------------------- make ----
if [ "$DEMO_MODE" = "true" ]; then
  warn "DEMO_MODE: skipping maker LLM call. Worktree is ready for you to poke at:"
  warn "  $WT"
else
  command -v claude >/dev/null || die "claude CLI not found (or run with DEMO_MODE=true)"
  log "delegating to maker (${CLAUDE_MODEL})..."
  MAKER_PROMPT="Read AGENTS.md and the task file loop/tasks/${TASK_ID}.md, then implement the task in this worktree following your maker instructions. Do not open a PR, do not run scripts/validate.sh, do not edit loop/state. End with the CHANGED/TEST_RESULT/BUILD_RESULT/RULES_SATISFIED report."
  (
    cd "$WT"
    claude -p "$MAKER_PROMPT" \
      --append-system-prompt "$(cat "$ROOT/.claude/agents/maker.md")" \
      --model "$CLAUDE_MODEL" \
      --permission-mode "$PERMISSION_MODE"
  ) || warn "maker exited non-zero (continuing to verification — the validator decides)"
fi

# ------------------------------------------------------------------ verify ----
# The validator is the single source of truth for the rules. It runs against the
# worktree and is intentionally blind to how the maker reached its result.
log "verifying with deterministic validator..."
set +e
VALIDATOR_OUT="$(cd "$WT" && "$ROOT/scripts/validate.sh" "$TASK_ID" 2>&1)"
VALIDATOR_EXIT=$?
set -e
echo "$VALIDATOR_OUT"

# --------------------------------------------------------------------- act ----
if [ "$VALIDATOR_EXIT" -eq 0 ] && echo "$VALIDATOR_OUT" | grep -qE "^VALIDATION_RESULT:[[:space:]]*PASS"; then
  ok "PASS — committing on branch ${BRANCH}"
  git -C "$WT" add -A
  if git -C "$WT" diff --cached --quiet; then
    warn "nothing to commit (no changes produced)"
    record_state "NO-CHANGES" "validator passed but maker produced no diff"
  else
    git -C "$WT" commit -q -m "feat(${TASK_ID}): implement task" -m "Task: ${TASK_ID}"
    ok "committed. Branch ${BRANCH} is ready for review / PR (connector step, not yet enabled)."
    record_state "COMPLETED" "validator PASS, committed on ${BRANCH}"
  fi
else
  warn "FAIL — not committing"
  record_state "FAILED" "validator exit=${VALIDATOR_EXIT}"
  if [ "$KEEP_WORKTREE" != "true" ] && [ "$DEMO_MODE" != "true" ]; then
    if git -C "$ROOT" worktree remove --force "$WT" 2>/dev/null; then
      log "worktree cleaned up"
    else
      warn "failed to remove worktree: $WT"
    fi
  else
    warn "worktree kept for inspection: $WT"
  fi
fi

log "done. State: $STATE"
