# Backup and restore

Everything Synth knows — config, the repository registry, the call graph, persisted logs, and
every embedded vector — lives entirely in two Docker-managed volumes (Mongo's `synthdata`
database, and Qdrant's collections). Neither is backed up automatically.

## What's included

- **Mongo (`synthdata`)**: config, the repository registry, call-graph edges, persisted logs.
- **Qdrant**: one snapshot per collection Synth currently knows about (via `GET /repositories`) —
  the actual embedded vectors and chunk content.

## What's NOT included

- **`~/.synth/workspaces`** (git clone caches for `repoUrl`-indexed repositories) — these are just
  local clones re-derived from each collection's remote URL (stored in the Mongo registry). If lost,
  they're regenerated automatically the next time that collection is re-indexed; not worth backing up.
- Any Qdrant collection that exists but isn't registered in Mongo (shouldn't normally happen, but if
  it does, `backup.sh` won't see it — only registered collections are snapshotted).

## Backup

Requires the Aspire stack running (`make aspire`, in another terminal):

```bash
make backup                       # writes to ./backups/<timestamp>/
make backup OUT=./backups/mine    # custom output directory
```

The backup directory is `.gitignore`d — **the Mongo dump includes VCS tokens and API keys in
plaintext** (Synth's raw settings are deliberately unmasked, see `SYNTH-29`), so never commit it
or share it outside a secure channel.

## Restore

Also requires `make aspire` running:

```bash
make restore DIR=./backups/20260711-120000
```

Order doesn't matter between Mongo and Qdrant — they're independent stores with no cross-references
that care about restore ordering. If the API was already running before the restore, restart it
(`make aspire`) afterward so it picks up the restored Mongo config/registry cleanly.

## How it works

Both scripts locate this project's own Mongo/Qdrant containers by checking each candidate
container's Aspire volume-mount label references `synth.apphost` — never by a bare name/keyword
match across every running container (a past incident in this project deleted an unrelated
project's Docker container via an overly broad `docker ps | grep <keyword>`; these scripts are
scoped defensively to avoid repeating that).

- **Mongo**: `mongodump`/`mongorestore` run *inside* the container (both ship in the standard
  `mongo` image), the dump is copied in/out via `docker cp` — no extra tooling needed on the host.
- **Qdrant**: uses its own snapshot HTTP API (`POST .../snapshots` to create, `POST
  .../snapshots/upload` to restore) — collection names come from Synth's own `GET /repositories`,
  not a raw Qdrant collection listing, so the backup only covers what Synth's registry actually
  knows about.
