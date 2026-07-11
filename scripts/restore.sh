#!/usr/bin/env bash
#
# restore.sh — restore a backup produced by scripts/backup.sh. Requires the Aspire stack to be
# running (`make aspire`). Restoring a Qdrant collection recreates it from the snapshot, replacing
# any existing collection of the same name.
#
# Usage: ./scripts/restore.sh <backup-dir>
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IN="${1:?usage: restore.sh <backup-dir> (e.g. ./backups/20260711-120000)}"
[ -d "$IN" ] || { echo "[restore] ERROR: not a directory: $IN" >&2; exit 1; }

log() { echo "[restore] $*"; }
fail() { echo "[restore] ERROR: $*" >&2; exit 1; }

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

MONGO_CONTAINER="$(find_container mongo)" || fail "no running Synth Mongo container found — is 'make aspire' running?"
QDRANT_CONTAINER="$(find_container qdrant)" || fail "no running Synth Qdrant container found — is 'make aspire' running?"
log "mongo container: $MONGO_CONTAINER"
log "qdrant container: $QDRANT_CONTAINER"

# --- Mongo: copy the dump into the container, then mongorestore ---------------------------------
if [ -d "$IN/mongo/synthdata" ]; then
  # Same auth requirement as backup.sh — pull credentials from the container's own env, never echo them.
  MONGO_USER="$(docker exec "$MONGO_CONTAINER" printenv MONGO_INITDB_ROOT_USERNAME)"
  MONGO_PASS="$(docker exec "$MONGO_CONTAINER" printenv MONGO_INITDB_ROOT_PASSWORD)"

  log "restoring Mongo database 'synthdata'..."
  docker cp "$IN/mongo/synthdata" "$MONGO_CONTAINER:/tmp/synth-restore-dump"
  docker exec "$MONGO_CONTAINER" mongorestore \
    --db synthdata \
    --username "$MONGO_USER" --password "$MONGO_PASS" --authenticationDatabase admin \
    --drop /tmp/synth-restore-dump --quiet
  docker exec "$MONGO_CONTAINER" rm -rf /tmp/synth-restore-dump
  log "mongo restore complete"
else
  log "no Mongo dump found at $IN/mongo/synthdata — skipping"
fi

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
