---
id: SYNTH-49
summary: "Deep-link search results to open in a local editor"
status: open
acceptance_command: "cd src/client && npm run build --silent && npm test -- --run"
acceptance_criterion: ""
boundaries: "Client-only task — no backend changes. Touch: src/client/src/components/SearchResultItem.vue, src/client/src/components/SearchPanel.vue (to pass collection/repository context down), a new small composable for the editor-preference + URI-building logic, and tests. Store the editor preference in localStorage (client-only), not a new backend Settings field — this is a single personal preference, not worth a server round-trip. Only show the editor link for local-path-indexed collections (SourceType == 'local') — repoUrl-indexed collections already got a GitHub/GitLab link in SYNTH-40 (issue #56), don't show both for the same result."
limits: "max_iterations=25; max_minutes=120"
labels: [client, search]
---

# SYNTH-49: Deep-link search results to open in a local editor

## Context
Part of issue #57. For repositories indexed by local `path` (the common personal-use case), a
search result shows the relative path and line range but there's no way to click through and open
the file at that line in an editor — SYNTH-40 (issue #56) already added this for `repoUrl`-indexed
results (a GitHub/GitLab blob link via `result.sourceUrl`), but local-indexed results have no
`sourceUrl` (by design — nothing to link to on the web) and currently show only plain text.

Editor deep-link URI shapes (need the absolute file path, not the relative one — combine a
collection's `RepositoryEntry.source`, which is the absolute local root for a `local`-sourced
collection, with the result's `relativePath`):
- JetBrains (Rider/IntelliJ): `jetbrains://rider/navigate/reference?project={name}&path={absolutePath}&line={line}`
- VS Code: `vscode://file/{absolutePath}:{line}`
- Cursor: `cursor://file/{absolutePath}:{line}`

`SearchPanel.vue` already knows which collection was searched (its own collection-picker state) and
already has `useRepositories()` available (or can add it) to look up that collection's
`RepositoryEntry` (for `SourceType`/`source`). `SearchResultItem.vue` itself doesn't currently
receive collection/repository context — it needs it passed down as a prop to build the link.

## What to do
1. Add a small composable (e.g. `src/client/src/composables/useEditorLink.ts`) that: reads/writes
   an editor preference (`'jetbrains' | 'vscode' | 'cursor'`, default your choice) to
   `localStorage['synth:editor']`, and exposes a function building the right URI given an absolute
   path + line number for the currently-selected editor. For the JetBrains shape's `project` query
   param, a reasonable default is the last path segment of the repository root (its directory name)
   — doesn't need to be perfectly accurate, IDEs generally still navigate correctly even if the
   project name guess is slightly off.
2. In `SearchPanel.vue`, resolve the currently-searched collection's `RepositoryEntry` (via
   `useRepositories()`, matching on the picker's selected collection) and pass its `sourceType`/
   `source` down to each `SearchResultItem` as props (alongside whatever it already passes).
3. In `SearchResultItem.vue`: when the repository is `local`-sourced, render an "open in editor"
   link/icon next to the result (using the new composable to build the URI from `source` +
   `result.relativePath` + `result.startLine`) instead of (or alongside) the plain path text. When
   it's `repoUrl`-sourced (has `result.sourceUrl` from SYNTH-40), keep the existing GitHub/GitLab
   link behavior unchanged — don't show an editor link for those.
4. Add a small editor-preference control somewhere reasonable (e.g. next to the theme toggle in
   `App.vue`'s header, or inline in `SearchPanel.vue` near the collection picker — your call on
   placement) — a simple `<select>` with the three options, backed by the composable's
   read/write-localStorage function.
5. Tests: a unit test for the composable's URI-building for each editor option; a
   `SearchResultItem.test.ts` case confirming the editor link renders (with the right href) for a
   local-sourced result and does NOT render for a `repoUrl`-sourced result (which shows the existing
   GitHub/GitLab link instead).

## Acceptance
`npm run build`/`npm test` stay green. A local-path-indexed search result shows a working editor
deep-link (JetBrains/VS Code/Cursor, user-selectable, persisted in localStorage) built from the
collection's local root + the result's relative path + line number. A `repoUrl`-indexed result still
shows its existing GitHub/GitLab link, not an editor link.

## Out of scope
- Auto-detecting which editor is installed — a simple stored preference is enough.
- Any backend change — this is entirely client-side, using data (`RepositoryEntry.source`,
  `result.relativePath`/`startLine`) that already exists in API responses.
