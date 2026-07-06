# Loop state

External memory of the agent loop — the repo remembers what the agent forgets.
The orchestrator appends here; subagents (maker/checker) never edit this file.

Row format: `| <task-id> | <status> | <note> |`
Statuses: `COMPLETED` · `FAILED` · `NO-CHANGES` · `IN-PROGRESS`

## Completed

## Failures

## In progress

_(nothing yet — run `./scripts/loop.sh` to start)_
- | SYNTH-1 | FAILED | validator exit=1 |
