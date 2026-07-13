# Backup and restore

Everything Synth knows — config, the repository registry, the call graph, persisted logs, and
every embedded vector — lives in `~/.synth/` (local files, since issue #80 dropped Mongo) and
Qdrant's Docker-managed collections. Neither is backed up automatically.

## What's included

- **`~/.synth/config.json`**: raw configuration document (VCS tokens, embedding provider settings).
- **`~/.synth/synth.db`**: the repository registry, call-graph edges, and persisted logs — one
  shared SQLite file.
- **Qdrant**: one snapshot per collection Synth currently knows about (via `GET /repositories`) —
  the actual embedded vectors and chunk content.

## What's NOT included

- **`~/.synth/workspaces`** (git clone caches for `repoUrl`-indexed repositories) — these are just
  local clones re-derived from each collection's remote URL (stored in the registry). If lost,
  they're regenerated automatically the next time that collection is re-indexed; not worth backing up.
- Any Qdrant collection that exists but isn't registered in the local registry (shouldn't normally
  happen, but if it does, `backup.sh` won't see it — only registered collections are snapshotted).

## Backup

Requires the Aspire stack running (`make aspire`, in another terminal) for the Qdrant snapshot step:

```bash
make backup                       # writes to ./backups/<timestamp>/
make backup OUT=./backups/mine    # custom output directory
```

The backup directory is `.gitignore`d — **`config.json` includes VCS tokens and API keys in
plaintext** (Synth's raw settings are deliberately unmasked, see `SYNTH-29`), so never commit it
or share it outside a secure channel.

## Restore

Also requires `make aspire` running for the Qdrant restore step:

```bash
make restore DIR=./backups/20260711-120000
```

Order doesn't matter between the local data files and Qdrant — independent stores with no
cross-references that care about restore ordering. Restart the API (`make aspire`) afterward if it
was already running, so it picks up the restored `config.json`/`synth.db` cleanly (it may hold the
SQLite file open).

## How it works

- **Local data**: `Synth.Api` runs as a plain Aspire project resource (not a container), so
  `~/.synth/` is the real host directory — `backup.sh`/`restore.sh` just `cp` `config.json` and
  `synth.db` straight off/onto disk, no `docker exec`/`docker cp` needed.
- **Qdrant**: located by checking each candidate container's Aspire volume-mount label references
  `synth.apphost` — never by a bare name/keyword match across every running container (a past
  incident in this project deleted an unrelated project's Docker container via an overly broad
  `docker ps | grep <keyword>`; this stays scoped defensively to avoid repeating that). Uses
  Qdrant's own snapshot HTTP API (`POST .../snapshots` to create, `POST .../snapshots/upload` to
  restore) — collection names come from Synth's own `GET /repositories`, not a raw Qdrant collection
  listing, so the backup only covers what Synth's registry actually knows about.
