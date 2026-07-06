#!/usr/bin/env bash
#
# validate.sh — the single source of truth for a task's acceptance rules.
#
# Deterministic. No LLM. The checker runs this and reports its verdict verbatim.
# Exit 0 + "VALIDATION_RESULT: PASS" means the task is done; anything else = FAIL.
#
# Usage: ./scripts/validate.sh <task-id>          # run from inside the worktree
#
set -uo pipefail

TASK_ID="${1:?usage: validate.sh <task-id>}"
# Resolve the repo root of the *current* checkout (worktree-aware).
ROOT="$(git rev-parse --show-toplevel 2>/dev/null || cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TASK_FILE="$ROOT/loop/tasks/${TASK_ID}.md"

pass() { echo "VALIDATION_RESULT: PASS"; exit 0; }
fail() { echo "VALIDATION_RESULT: FAIL"; echo "reason: $*"; exit 1; }

[ -f "$TASK_FILE" ] || fail "task file not found: $TASK_FILE"

# --- read a scalar from YAML frontmatter (first match, quotes stripped) --------
fm() {
  awk -v key="$1" '
    /^---[[:space:]]*$/ { n++; if (n==2) exit; next }
    n==1 {
      if ($0 ~ "^"key":") {
        sub("^"key":[[:space:]]*", ""); gsub(/^"|"$/, ""); print; exit
      }
    }' "$TASK_FILE"
}

CRITERION="$(fm acceptance_criterion)"
CRITERION_CMD="$(fm acceptance_command)"

echo "== validate ${TASK_ID} =="
echo "root: $ROOT"

# --- 1. build (only if a project actually exists) ------------------------------
CSPROJ="$(find "$ROOT/src" -name '*.csproj' 2>/dev/null | head -1)"
SLN="$(find "$ROOT/src" -name '*.sln' 2>/dev/null | head -1)"
if [ -n "$CSPROJ" ] || [ -n "$SLN" ]; then
  echo "-- dotnet build"
  (cd "$ROOT" && dotnet build --nologo -v q) || fail "dotnet build failed"
fi
if [ -f "$ROOT/src/client/package.json" ]; then
  echo "-- npm build"
  (cd "$ROOT/src/client" && npm run build --silent) || fail "npm build failed"
fi

# --- 2. tests (only if a test project exists) ----------------------------------
TESTPROJ="$(find "$ROOT/src" -name '*Tests*.csproj' -o -name '*.Tests.csproj' 2>/dev/null | head -1)"
if [ -n "$TESTPROJ" ]; then
  echo "-- dotnet test"
  (cd "$ROOT" && dotnet test --nologo -v q) || fail "dotnet test failed"
fi

# --- 3. acceptance criterion (the actual contract) -----------------------------
if [ -n "$CRITERION_CMD" ]; then
  echo "-- acceptance_command: $CRITERION_CMD"
  (cd "$ROOT" && bash -c "$CRITERION_CMD") || fail "acceptance_command exited non-zero"
elif [ -n "$CRITERION" ]; then
  echo "-- acceptance_criterion (must appear in src/): $CRITERION"
  if grep -RqF -- "$CRITERION" "$ROOT/src" 2>/dev/null; then
    echo "found."
  else
    fail "acceptance_criterion not found in src/: $CRITERION"
  fi
else
  # Fail closed: refuse to pass a task that defines nothing to prove.
  fail "task defines no acceptance_criterion/acceptance_command — nothing to verify"
fi

pass
