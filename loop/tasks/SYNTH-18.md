---
id: SYNTH-18
summary: "Git clone/fetch service for GitHub/GitLab repository URLs"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'class GitRepoService' src/Synth.Core/"
acceptance_criterion: ""
boundaries: "Only add the git clone/fetch service and URL-parsing/collection-naming helper as standalone, testable units in Synth.Core. Do not wire this into POST /index, do not add a repository registry, do not touch the Vue client — those are SYNTH-19/SYNTH-20. Do not add webhooks, AI review, or issue automation (backlog, issue #22)."
limits: "max_iterations=30; max_minutes=150"
labels: [backend, vcs, git]
---

# SYNTH-18: Git clone/fetch service for GitHub/GitLab repository URLs

## Context
Synth currently only indexes a local directory that's already on disk. This task adds the
ability to point Synth at a remote GitHub or GitLab repository URL: clone it into a local
workspace the first time, fetch+update it on subsequent re-indexes. This mirrors Sonar's
`GitRepoService` (`Sonar.Vcs`), trimmed down: no webhook triggering, no Bitbucket Server support,
no CI-specific auth plumbing — just "give me a URL, get me a local checkout." SYNTH-17 already
added a `collection` concept to the store/pipeline/search; this task derives a stable collection
name from the repo URL so re-indexing the same repo updates the same collection instead of
duplicating it.

Reference for the config-layering pattern to reuse for auth tokens: `src/Synth.Api/Configuration/`
(`IConfigStore`, `FileConfigStore`, `MongoConfigStore`) — Ollama/Qdrant already follow a
"read from layered config, no live external dependency required for tests" convention worth
mirroring here.

## What to do
1. Add `src/Synth.Core/Vcs/RepoUrlInfo.cs` (or similar) — parses a git remote URL (HTTPS form,
   e.g. `https://github.com/owner/repo.git` or `https://gitlab.com/group/subgroup/repo.git`) into
   its host, owner/path segments and repo name. Classify the provider as GitHub/GitLab/Other based
   on the host containing `"github"`/`"gitlab"` (case-insensitive) — this only needs to work for
   github.com/gitlab.com and self-hosted instances with those strings in the hostname; don't try
   to handle SSH-form URLs (`git@host:owner/repo.git`) for now. Derive a stable, Qdrant-safe
   collection name/slug from host + path (lowercase, non-alnum replaced with `-`, mirroring the
   sanitization added in SYNTH-17's `QdrantCodeChunkStore`) so the same URL always maps to the
   same collection.
2. Add `src/Synth.Core/Vcs/GitRepoService.cs` with something like
   `Task<string> EnsureRepoAsync(string repoUrl, string? branch, CancellationToken ct)` returning
   the local checkout path:
   - Workspace root: a configurable directory (e.g. `Synth:WorkspaceRoot` in config, default
     `~/.synth/workspaces`), one subdirectory per repo slug (from step 1).
   - If the subdirectory doesn't exist or isn't a git repo yet: `git clone` (optionally
     `--branch <branch>` when given) into it.
   - If it already exists: `git fetch` then `git reset --hard` to the requested branch (default
     the repo's default branch, e.g. via `origin/HEAD` if no branch given) — same idea as Sonar's
     re-clone/refresh flow, just simpler (no Bitbucket `/scm/` special-casing needed).
   - Shell out to the `git` CLI via `System.Diagnostics.Process` (no new NuGet dependency).
3. Auth: read an optional per-provider token from config (e.g. `Vcs:GitHub:Token`,
   `Vcs:GitLab:Token`, via the existing config-layering `IConfigStore`/`IOptionsMonitor`
   machinery — do not invent a second config system). When present, pass it to git via
   `-c http.extraHeader="Authorization: Bearer <token>"` for GitHub or
   `-c http.extraHeader="PRIVATE-TOKEN: <token>"` for GitLab, so the token never gets written to
   disk (no credential helper, no token embedded in the remote URL). Public repos must keep
   working with no token configured.
4. Tests: create a real local git repository as a fixture inside the test's temp directory
   (`git init --bare` + a working clone that commits a file, or equivalent), and clone/fetch from
   it via a `file://` URL — no live network access to github.com/gitlab.com required. Cover:
   first-time clone produces the expected file; a second `EnsureRepoAsync` call after a new commit
   in the fixture repo picks up the update (fetch+reset behavior); `RepoUrlInfo` parsing for a
   handful of representative GitHub/GitLab HTTPS URLs (including nested GitLab group paths) and the
   collection-name derivation being stable/deterministic for the same URL.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green, `GitRepoService` and `RepoUrlInfo`
exist in `Synth.Core`, and the fixture-based clone/fetch tests pass without any live network call.

## Out of scope
- Wiring this into `POST /index`, the repository registry, `GET /repositories` — `SYNTH-19`.
- Vue client changes — done directly after the backend lands.
- Webhooks, AI review, issue auto-resolution — backlog, issue #22.
- SSH-form git URLs, Bitbucket support.
