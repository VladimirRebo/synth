---
id: SYNTH-45
summary: "GC orphaned git checkouts under workspace root"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'ResolveCheckoutPath' src/Synth.Core/Vcs/GitRepoService.cs"
acceptance_criterion: ""
boundaries: "Touch: src/Synth.Api/Vcs/RepositoryEndpoints.cs (hook checkout cleanup into the existing DELETE handler), src/Synth.Core/Vcs/GitRepoService.cs (a public ResolveCheckoutPath helper if one doesn't already exist — check first, another task may have added it; reuse rather than duplicate if so), a new small startup sweep component, and tests. Do NOT delete checkouts for local-path-indexed collections (SourceType == \"local\") — those were never cloned by GitRepoService, there's nothing under the workspace root to clean up for them. Do NOT add age-based eviction — only remove checkouts with no corresponding registry entry, never guess at staleness by time."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, vcs, operability]
---

# SYNTH-45: GC orphaned git checkouts under workspace root

## Context
Part of issue #46. `GitRepoService` clones remote repos into `{WorkspaceRoot}/{slug}` (default
`~/.synth/workspaces`) and never removes them — every distinct repo ever indexed accumulates a full
checkout on disk forever, even after its collection is deleted via `DELETE /repositories/{collection}`
(SYNTH-34, already merged) or simply never touched again.

## What to do
1. **On collection deletion**: in `RepositoryEndpoints.cs`'s `DELETE /repositories/{collection}`
   handler, after the existing `chunkStore.DeleteCollectionAsync`/`graphStore.ReplaceEdgesAsync`/
   `registry.DeleteAsync` sequence, also delete the on-disk checkout — but only for a collection
   whose `RepositoryEntry.SourceType` is `"github"` or `"gitlab"` (i.e. it was actually cloned by
   `GitRepoService`; a `"local"` collection has no corresponding checkout to remove). You'll need
   the `RepositoryEntry` *before* calling `registry.DeleteAsync` removes it — fetch it via
   `registry.ListAsync()` (or add a `GetAsync(collection)` if that's cleaner, your call) before the
   delete sequence, to know the `SourceType` and resolve the checkout path.
2. Check whether `GitRepoService` already has a public checkout-path-resolution helper (e.g.
   `ResolveCheckoutPath(string slug)`) — another task (SYNTH-42, get_file) may have added one for
   the same reason (resolving a collection's on-disk root without re-cloning). If it exists, reuse
   it. If not, add it: a small public method reusing the existing private `ResolveWorkspaceRoot`
   logic (don't duplicate the default-path/env-expansion logic inline in two places).
3. Delete the resolved checkout directory (`Directory.Delete(path, recursive: true)`), guarded so a
   missing/already-gone directory doesn't throw (check `Directory.Exists` first, or catch
   `DirectoryNotFoundException`).
4. **Startup sweep for pre-existing orphans** (checkouts that existed before this fix, or from a
   collection deleted some other way before this landed): add a small one-shot startup component
   (an `IHostedService`/`BackgroundService` whose `StartAsync`/`ExecuteAsync` runs once and returns,
   not a recurring loop) that: lists subdirectories of the resolved workspace root, lists all
   `RepositoryEntry.Collection` values from `IRepositoryRegistry.ListAsync()`, and deletes any
   workspace-root subdirectory whose name doesn't match any known collection. Register it in
   `Program.cs` alongside the other hosted services (check for `LogEntryStoreWriter`'s registration
   pattern from SYNTH-28 as precedent for a startup `BackgroundService` in this codebase). Guard
   against the workspace root not existing yet (a fresh install with nothing indexed) — no-op, don't
   error.
5. Tests: the `DELETE` handler now removes the checkout for a `github`/`gitlab`-sourced fixture
   (temp directory standing in for a checkout) but leaves a `local`-sourced collection's checkout
   untouched (there isn't one — assert nothing errors, no directory operations attempted for that
   case); the startup sweep removes an orphaned directory (one with no matching registry entry)
   while leaving a directory that *does* match a current registry entry alone — use a temp directory
   fixture for the workspace root, not the real `~/.synth/workspaces`.

## Acceptance
`dotnet build`/`dotnet test` stay green. Deleting a `github`/`gitlab`-sourced collection also
removes its on-disk checkout (local-sourced collections are untouched, they never had one). A
startup sweep removes any workspace-root subdirectory with no matching registry entry, without
touching directories that do match, and without erroring when the workspace root doesn't exist yet.

## Out of scope
- Age-based eviction ("delete if not touched in N days") — only remove checkouts with no
  corresponding registry entry.
- A manually-triggerable sweep endpoint/MCP tool — startup-only sweep for this pass.
