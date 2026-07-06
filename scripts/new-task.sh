#!/usr/bin/env bash
#
# new-task.sh — scaffold a new task-contract file under loop/tasks/.
#
# Usage:
#   ./scripts/new-task.sh "Short summary of the task"
#   ./scripts/new-task.sh "Add /health endpoint" SYNTH-3
#
set -euo pipefail

SUMMARY="${1:?usage: new-task.sh \"<summary>\" [task-id]}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TASKS="$ROOT/loop/tasks"

if [ -n "${2:-}" ]; then
  ID="$2"
else
  # SYNTH-<n>: next number after the highest existing SYNTH-* task.
  n=0
  for f in "$TASKS"/SYNTH-*.md; do
    [ -e "$f" ] || continue
    b="$(basename "$f" .md)"; num="${b##*-}"
    [[ "$num" =~ ^[0-9]+$ ]] && [ "$num" -gt "$n" ] && n="$num"
  done
  ID="SYNTH-$((n + 1))"
fi

FILE="$TASKS/${ID}.md"
[ -e "$FILE" ] && { echo "already exists: $FILE" >&2; exit 1; }

cat > "$FILE" <<EOF
---
id: ${ID}
summary: "${SUMMARY}"
status: open
# One of the two below defines the contract the validator enforces:
#   acceptance_command — a shell command that must exit 0 (preferred, strongest)
#   acceptance_criterion — a string that must appear somewhere in src/
acceptance_command: ""
acceptance_criterion: ""
boundaries: "Only touch files needed for this task. Do not change unrelated modules."
limits: "max_iterations=25; max_minutes=120"
labels: [scaffold]
---

# ${ID}: ${SUMMARY}

## Context
_Why this task exists and any relevant background._

## What to do
_Concrete, verifiable steps._

## Acceptance
_The measurable definition of done. Mirror this in the frontmatter contract above so \`validate.sh\` can enforce it (a passing \`dotnet test\`, a command exiting 0, etc.)._

## Out of scope
_What NOT to change._
EOF

echo "created: $FILE"
