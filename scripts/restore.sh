#!/usr/bin/env bash
#
# restore.sh — restore a backup produced by scripts/backup.sh. Requires the Aspire stack to be
# running (`make aspire`) for the Qdrant restore step. Restoring a Qdrant collection recreates it
# from the snapshot, replacing any existing collection of the same name. Restart the API afterward
# (it may hold the SQLite file open) so it picks up the restored local data cleanly.
#
# Usage: ./scripts/restore.sh <backup-dir>
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IN="${1:?usage: restore.sh <backup-dir> (e.g. ./backups/20260711-120000)}"
[ -d "$IN" ] || { echo "[restore] ERROR: not a directory: $IN" >&2; exit 1; }
SYNTH_HOME="${SYNTH_HOME:-$HOME/.synth}"

log() { echo "[restore] $*"; }
fail() { echo "[restore] ERROR: $*" >&2; exit 1; }

# --- local data: config.json + synth.db, copied straight back onto disk -------------------------
if [ -d "$IN/synth-data" ]; then
  mkdir -p "$SYNTH_HOME"
  for f in config.json synth.db; do
    if [ -f "$IN/synth-data/$f" ]; then
      cp "$IN/synth-data/$f" "$SYNTH_HOME/$f"
      log "restored $SYNTH_HOME/$f"
    fi
  done
else
  log "no $IN/synth-data found — skipping local data restore"
fi

# Same scoped-lookup discipline as backup.sh — never match containers by a bare keyword across
# every running container, only this project's Aspire-managed ones (see project history: a past
# broad docker-cleanup grep deleted an unrelated project's container by mistake).
find_container() {
  local prefix="$1"
  local name
  for name in $(docker ps --format '{{.Names}}' --filter "name=^${prefix}-"); do
    if docker inspect "$name" --format '{{index .Config.Labels "com.microsoft.developer.usvc-dev.mountsLabel"}}' 2>/dev/null \
        | grep -qi "synth.apphost"; then
      echo "$name"
      return 0
    fi
  done
  return 1
}

QDRANT_CONTAINER="$(find_container qdrant)" || fail "no running Synth Qdrant container found — is 'make aspire' running?"
log "qdrant container: $QDRANT_CONTAINER"

# --- Qdrant: upload + recover each snapshot ------------------------------------------------------
QDRANT_API_KEY="$(docker inspect "$QDRANT_CONTAINER" --format '{{range .Config.Env}}{{println .}}{{end}}' \
  | sed -n 's/^QDRANT__SERVICE__API_KEY=//p')"
QDRANT_PORT="$(docker port "$QDRANT_CONTAINER" 6333/tcp | head -1 | cut -d: -f2)"
[ -n "$QDRANT_PORT" ] || fail "could not resolve Qdrant's published port"

if [ -d "$IN/qdrant" ]; then
  for snapshot in "$IN"/qdrant/*.snapshot; do
    [ -e "$snapshot" ] || continue
    collection="$(basename "$snapshot" .snapshot)"
    log "restoring Qdrant collection '$collection' from $(basename "$snapshot")..."
    curl -sS -X POST \
      -H "api-key: ${QDRANT_API_KEY}" \
      -F "snapshot=@${snapshot}" \
      "http://localhost:${QDRANT_PORT}/collections/${collection}/snapshots/upload?priority=snapshot" \
      > /dev/null
    log "  -> restored"
  done
else
  log "no Qdrant snapshots found at $IN/qdrant — skipping"
fi

log "done. Restart the API (make aspire) if it was already running, so it picks up the restored data."
