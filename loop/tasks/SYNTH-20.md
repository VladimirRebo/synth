---
id: SYNTH-20
summary: "Settings CRUD for VcsOptions (GET/PUT /api/settings/vcs)"
status: done
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -riq 'MapGet(\"/settings/vcs\"\|MapPut(\"/settings/vcs\"' src/Synth.Api/"
acceptance_criterion: ""
boundaries: "Only add Settings CRUD for the existing Vcs config section. Do not touch embeddings, Qdrant, or the Vue client — those are SYNTH-21/22 and a later direct client task. Do not add auth/RBAC (Synth has none, single local user)."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, settings, vcs]
---

# SYNTH-20: Settings CRUD for VcsOptions

## Context
Issue #26 (Phase: Settings management) adds runtime-editable configuration. `VcsOptions`
(`src/Synth.Core/Vcs/VcsOptions.cs` — `WorkspaceRoot`, `GitHub.Token`, `GitLab.Token`) already
binds live from the layered config store via `IOptionsMonitor<VcsOptions>`
(`src/Synth.Api/Vcs/VcsServiceExtensions.cs`), but there is currently **no way to actually set
these values** except editing `IConfigStore`'s JSON document by hand (Mongo/file) — this task adds
the missing read/write API. This is the simplest of the three Settings tasks: no provider
switching, no probe-before-persist (there's nothing to connect-test — a token's validity is only
provable by actually cloning, which is out of scope here), just CRUD over one config section.

Reference for the config-layering shape: `src/Synth.Api/Configuration/IConfigStore.cs` (the store
holds one opaque JSON document; `ConfigStoreConfigurationProvider` flattens it into
`section:subsection:key` config keys) and `MongoConfigStore`/`FileConfigStore` for how it's
persisted. Sonar's equivalent (documented in the Jarvis wiki, concept `config-layering-cicd`) does
a thread-safe read-merge-write of one section of the document plus an `IConfigurationRoot.Reload()`
call so `IOptionsMonitor` subscribers see the change immediately — mirror that shape here, adapted
to Synth's simpler single-document-JSON-string store (there is no existing generic "update one
section" helper in this repo yet; this task is expected to add the first one, in a way SYNTH-22 can
reuse for the `Embedding` section).

## What to do
1. Add a small helper (e.g. a static method or a `ConfigSectionUpdater` type in
   `Synth.Api/Configuration/`) that: loads the current `IConfigStore` JSON document (or starts from
   `{}` if none), parses it, merges/replaces one named top-level section (e.g. `"Vcs"`) with a new
   value, re-serializes, and calls `IConfigStore.SaveAsync`. Use a lock (or equivalent) around the
   read-modify-write so two concurrent PUTs can't race and clobber each other — same reasoning as
   Sonar's thread-safe section update. After saving, call `IConfigurationRoot.Reload()` (inject
   `IConfiguration` cast to `IConfigurationRoot`, or have the config store's own `Changed` event
   already trigger `ConfigStoreConfigurationProvider.OnReload()` — check whether that's already
   sufficient before adding a second reload path) so `IOptionsMonitor<VcsOptions>` picks up the
   change without a restart.
2. `GET /api/settings/vcs` — returns the current `VcsOptions` as JSON, but **mask the tokens**:
   don't echo the raw secret back. Return e.g. `{ workspaceRoot, github: { tokenSet: bool },
   gitlab: { tokenSet: bool } }` rather than the token value itself.
3. `PUT /api/settings/vcs` — accepts `{ workspaceRoot?, github?: { token? }, gitlab?: { token? } }`.
   A field that's present and non-null sets/replaces that value; a field that's absent (not just
   empty string) leaves the existing stored value unchanged — so a client can update just the
   GitHub token without needing to already know/resend the GitLab token. An explicit empty string
   `""` clears the token (back to anonymous access). Persist via the helper from step 1, then return
   the same masked shape as `GET`.
4. Map both endpoints (new `Synth.Api/Vcs/VcsSettingsEndpoints.cs` or alongside the existing
   `RepositoryEndpoints.cs` in `Synth.Api/Vcs/`), registered in `Program.cs` near the other
   `Map*Endpoints()` calls.
5. Tests: round-trip a PUT then GET and confirm the masked shape (no raw token in the response);
   confirm a partial PUT (only `github.token`) leaves a previously-set `gitlab.token` untouched;
   confirm the empty-string-clears-token behavior; confirm `IOptionsMonitor<VcsOptions>` actually
   observes the new value after a PUT (i.e. the reload path genuinely works, not just that the
   stored JSON changed) — this is the part most likely to be subtly wrong, test it directly rather
   than assuming.

## Acceptance
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green; `GET`/`PUT /api/settings/vcs` exist,
round-trip correctly, never echo raw token values, support partial updates, and a PUT is
observable through `IOptionsMonitor<VcsOptions>` without an app restart.

## Out of scope
- Embedding/Qdrant settings — `SYNTH-21`/`SYNTH-22`.
- Vue client — done directly after the backend lands.
- Probing that a token actually works (would require a live clone attempt).
