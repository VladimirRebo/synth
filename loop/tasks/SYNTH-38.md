---
id: SYNTH-38
summary: "Warn on unknown top-level keys in raw settings PUT"
status: open
acceptance_command: "dotnet build src/Synth.slnx --nologo -v q && dotnet test src/Synth.slnx --nologo -v q && grep -rq 'warnings' src/Synth.Api/Configuration/RawSettingsEndpoints.cs"
acceptance_criterion: ""
boundaries: "Touch only src/Synth.Api/Configuration/RawSettingsEndpoints.cs and its tests. Do not turn this into a hard validation failure (400) for unknown keys — see the Context section for why this must stay a warning, not a rejection. Do not build a JSON Schema or any generic schema-validation library dependency — a flat top-level-key check against VcsOptions.SectionName/EmbeddingOptions.SectionName is enough."
limits: "max_iterations=20; max_minutes=90"
labels: [backend, api, settings]
---

# SYNTH-38: Warn on unknown top-level keys in raw settings PUT

## Context
Part of issue #51. `PUT /settings/raw` (`src/Synth.Api/Configuration/RawSettingsEndpoints.cs`,
added in SYNTH-29) only checks that the request body parses as a JSON object —
`ConfigSectionUpdater.ReplaceDocumentAsync` validates well-formedness, nothing more. A typo in a
top-level field name (e.g. `"vcs"` lowercase instead of the canonical `"Vcs"` — config binding is
case-insensitive for `IOptions<T>` binding from `IConfiguration`, but the document's own top-level
key naming here is whatever string was written, and a genuinely different/misspelled key like
`"Vsc"` binds to nothing) is silently accepted and stored; the app just behaves as if that setting
were never configured, with no error anywhere to explain why.

This was an accepted, documented tradeoff when SYNTH-29 shipped ("trust the caller, validate only
that it's a valid JSON object" — see the doc comment at the top of `RawSettingsEndpoints.cs`,
which explicitly says there's deliberately no probe-before-persist here). This task does **not**
reverse that decision or add probing — it only adds a *non-blocking* warning for top-level keys
that don't match any section this app actually reads, so a typo is at least visible instead of
silently swallowed. The raw editor's whole point is flexibility (it's meant to allow saving keys
this version of the app doesn't know about yet, e.g. forward-compatibility, or scratch/experimental
values) — do not make an unrecognized key a hard failure.

Known top-level section names today: `VcsOptions.SectionName` (`"Vcs"`) and
`EmbeddingOptions.SectionName` (`"Embedding"`) — check both classes for the exact constants,
don't hardcode the strings separately from them.

## What to do
1. In the `PUT /settings/raw` handler, after `ReplaceDocumentAsync` succeeds (so the write still
   happens exactly as today), parse the persisted document's top-level keys and compare them
   (case-insensitively) against the known section names (`VcsOptions.SectionName`,
   `EmbeddingOptions.SectionName`).
2. If any top-level key doesn't match either known name, include a non-fatal warning in the
   response — e.g. add an optional `warnings: string[]` array alongside the existing response body
   (currently `Results.Content(persisted, "application/json")`, which returns the raw document
   text directly with no wrapper object). Decide the least disruptive way to add this: either wrap
   the response in a small JSON object `{ document: <raw json>, warnings: [...] }` (a breaking
   change to the response shape — check whether the client's raw-JSON editor in `SettingsPanel.vue`
   depends on the response being the bare document text, and update it if so) or use a response
   header (e.g. `X-Settings-Warnings`) to avoid changing the body shape at all — prefer whichever
   is less disruptive to the existing client integration once you've checked how it consumes this
   endpoint's response.
3. Still return the persisted document/success as today when there are no unknown keys — this is
   purely additive.
4. Tests: a `PUT /settings/raw` with only known section names (`Vcs`, `Embedding`) produces no
   warning; a body with an extra/misspelled top-level key (e.g. `{"Vcs": {...}, "Typo": {...}}`)
   still persists successfully (the write is NOT blocked) but surfaces a warning identifying
   `"Typo"`.

## Acceptance
`dotnet build`/`dotnet test` stay green. `PUT /settings/raw` still accepts and persists any
well-formed JSON object as before (no new hard-failure case), but now surfaces a non-blocking
warning when the document contains a top-level key that doesn't match any section this app
actually reads (`Vcs`, `Embedding`).

## Out of scope
- Any client-side display of the new warning (unless the response-shape change from step 2 forces
  a client update to keep the raw editor working — in that case, fix only what's needed to avoid
  breaking the existing editor, don't add a new warnings UI).
- A general JSON Schema for the config document.
