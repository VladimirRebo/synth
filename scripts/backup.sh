#!/usr/bin/env bash
#
# backup.sh — snapshot Synth's Mongo database (config/registry/call-graph/logs) and every
# indexed Qdrant collection to a local directory. Requires the Aspire stack to be running
# (`make aspire`). Does NOT include ~/.synth/workspaces (git clone caches — regenerable by
# re-cloning, not worth backing up).
#
# Usage: ./scripts/backup.sh [output-dir]   (default: ./backups/<timestamp>)
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${1:-$ROOT/backups/$(date +%Y%m%d-%H%M%S)}"
API_PORT="${SYNTH_API_PORT:-5042}"

log() { echo "[backup] $*"; }
fail() { echo "[backup] ERROR: $*" >&2; exit 1; }

mkdir -p "$OUT"
log "writing to $OUT"

# --- find this project's Mongo/Qdrant containers, scoped by Aspire's own mount label ------------
# Never match by a bare name/keyword across ALL containers (see project history: a broad
# `docker ps | grep ollama` once deleted an unrelated project's container). Aspire container names
# are `{resource}-{random}` and not stable across runs, so find candidates by name PREFIX, then
# confirm each one's volume-mount label actually references this AppHost before touching it.
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

# --- Mongo: mongodump inside the container, then copy the dump out ------------------------------
# Aspire's Mongo resource requires auth (AddMongoDB with a fixed username/password — see
# AppHost.cs); pull the credentials from the container's own env so they're never typed/echoed
# anywhere, and pass them straight to mongodump's flags (never logged).
MONGO_USER="$(docker exec "$MONGO_CONTAINER" printenv MONGO_INITDB_ROOT_USERNAME)"
MONGO_PASS="$(docker exec "$MONGO_CONTAINER" printenv MONGO_INITDB_ROOT_PASSWORD)"

log "dumping Mongo database 'synthdata'..."
docker exec "$MONGO_CONTAINER" mongodump \
  --db synthdata \
  --username "$MONGO_USER" --password "$MONGO_PASS" --authenticationDatabase admin \
  --out /tmp/synth-backup-dump --quiet
docker cp "$MONGO_CONTAINER:/tmp/synth-backup-dump" "$OUT/mongo"
docker exec "$MONGO_CONTAINER" rm -rf /tmp/synth-backup-dump
log "mongo dump -> $OUT/mongo"

# --- Qdrant: snapshot every collection Synth actually knows about via its own registry -----------
QDRANT_API_KEY="$(docker inspect "$QDRANT_CONTAINER" --format '{{range .Config.Env}}{{println .}}{{end}}' \
  | sed -n 's/^QDRANT__SERVICE__API_KEY=//p')"
QDRANT_PORT="$(docker port "$QDRANT_CONTAINER" 6333/tcp | head -1 | cut -d: -f2)"
[ -n "$QDRANT_PORT" ] || fail "could not resolve Qdrant's published port"

collections="$(curl -sS "http://localhost:${API_PORT}/repositories" | python3 -c '
import json, sys
try:
    entries = json.load(sys.stdin)
    print("\n".join(e["collection"] for e in entries))
except Exception:
    pass
')"

if [ -z "$collections" ]; then
  log "no indexed collections found via GET /repositories — skipping Qdrant snapshots"
else
  mkdir -p "$OUT/qdrant"
  while IFS= read -r collection; do
    [ -n "$collection" ] || continue
    log "snapshotting Qdrant collection '$collection'..."
    snapshot_name="$(curl -sS -X POST \
      -H "api-key: ${QDRANT_API_KEY}" \
      "http://localhost:${QDRANT_PORT}/collections/${collection}/snapshots" \
      | python3 -c 'import json,sys; print(json.load(sys.stdin)["result"]["name"])')"
    curl -sS -H "api-key: ${QDRANT_API_KEY}" \
      "http://localhost:${QDRANT_PORT}/collections/${collection}/snapshots/${snapshot_name}" \
      -o "$OUT/qdrant/${collection}.snapshot"
    log "  -> $OUT/qdrant/${collection}.snapshot"
  done <<< "$collections"
fi

log "done. Restore with: ./scripts/restore.sh '$OUT'"
