---
id: SYNTH-37
summary: "Probe VCS tokens before persisting (GitHub/GitLab)"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'IHttpClientFactory' src/Synth.Api/Vcs/VcsSettingsEndpoints.cs"
acceptance_criterion: ""
boundaries: "Touch only src/Synth.Api/Vcs/VcsSettingsEndpoints.cs, DI registration for IHttpClientFactory if not already present (Program.cs / a service-extensions file), and tests. Do not touch GitRepoService.cs or the git-clone auth path (http.extraHeader) — that already works and is unrelated to this validate-before-save check. Self-hosted GitLab instances are explicitly out of scope (see below) — only probe against the well-known public API hosts."
limits: "max_iterations=25; max_minutes=120"
labels: [backend, api, settings]
---

# SYNTH-37: Probe VCS tokens before persisting

## Context
Part of issue #50. `PUT /settings/embedding` already does probe-before-persist: a real test call
against the candidate config before saving, because "a saved broken provider poisons every
subsequent request" (see `EmbeddingSettingsEndpoints.ProbeAsync`). `PUT /settings/vcs`
(`src/Synth.Api/Vcs/VcsSettingsEndpoints.cs`) has no equivalent — an invalid or expired
GitHub/GitLab token is currently saved as-is via `ApplyTokenUpdate` and only surfaces as a failure
the next time `GitRepoService.EnsureRepoAsync` actually tries to use it, deep inside a background
indexing job — the same "silent poisoned config" problem the embedding probe already solves for
the other setting.

`VcsOptions.GitHub`/`VcsOptions.GitLab` each carry only a `Token` (`src/Synth.Core/Vcs/VcsOptions.cs`)
— no configurable API host, so GitLab probing here can only reliably target `gitlab.com`'s public
API. A self-hosted GitLab instance's token can't be validated this way (its host isn't known)
— that's an accepted, explicit limitation for this task, not something to work around.

## What to do
1. When the incoming `PUT /settings/vcs` request sets a *new, non-empty* GitHub or GitLab token
   (i.e. `ApplyTokenUpdate`'s existing detection of "this provider's token field is present and
   non-empty" — reuse that same detection, don't re-derive it), probe it before calling
   `updater.UpdateSectionAsync`:
   - GitHub: `GET https://api.github.com/user` with `Authorization: Bearer {token}` and a
     `User-Agent` header (GitHub's API rejects requests without one — use something like `"Synth"`).
   - GitLab: `GET https://gitlab.com/api/v4/user` with a `PRIVATE-TOKEN: {token}` header.
   Use `IHttpClientFactory` (register `builder.Services.AddHttpClient()` if not already present in
   `Program.cs`/service-extensions) with a short timeout (mirror `EmbeddingSettingsEndpoints`'s
   `ProbeTimeout` style/duration — a few seconds is enough for a simple auth check).
2. A non-2xx response (401/403 especially) or a network failure/timeout means the token is
   invalid or unreachable: return `Results.BadRequest(new { error = "..." })` with a clear message
   naming the provider (e.g. `"the GitHub token is invalid or lacks API access (received 401)."`),
   and do NOT call `updater.UpdateSectionAsync` — the existing config stays untouched, same
   contract as the embedding probe.
3. A cleared token (empty string, per `ApplyTokenUpdate`'s existing "empty string clears back to
   anonymous" semantics) or an absent field needs no probing — only newly-set, non-empty tokens
   are probed. `workspaceRoot` changes also don't need probing (it's just a local path).
4. Tests: extend whatever test coverage exists for `VcsSettingsEndpoints` (check `Synth.Api.Tests`
   for the existing file) to cover: a valid-looking token probe succeeding and being persisted
   (mock the HTTP call via a fake `HttpMessageHandler`/`IHttpClientFactory`, don't hit real GitHub/GitLab
   in tests), an invalid token probe failing with 400 and the config remaining unchanged afterward
   (a subsequent `GET /settings/vcs` still shows the old token-set state), and clearing a token
   (empty string) still working without any probe attempt.

## Acceptance
`dotnet build`/`dotnet test` stay green. `PUT /settings/vcs` probes a newly-set, non-empty
GitHub/GitLab token against the provider's public API before persisting; an invalid token is
rejected with 400 and the stored config is left untouched; clearing a token or leaving it unset
still works without a probe.

## Out of scope
- Self-hosted GitLab instances — no configurable host exists in `VcsOptions` to probe against;
  this only validates against `gitlab.com`'s public API.
- Token *scope*/permission validation (confirming it can actually read the specific repos the user
  cares about) — just confirming the token authenticates at all.
