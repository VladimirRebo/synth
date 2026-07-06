---
id: SYNTH-15
summary: "Runtime base-path support for the client build"
status: open
acceptance_command: "npm --prefix src/client install --no-audit --no-fund && npm --prefix src/client run build --silent && npm --prefix src/client test --silent -- run && ! grep -qE 'src=\"/assets|href=\"/assets' src/client/dist/index.html"
acceptance_criterion: ""
boundaries: "Only change how src/client resolves asset/base URLs at build and (if a runtime override is added) at load time, so ONE build of dist/ can be deployed under any sub-path without rebuilding. Do not add Vue Router or new pages/routes — there's nothing to route yet. Do not touch Synth.Api/Synth.Core/MCP/backend code. Do not add the Aspire AddNpmApp AppHost wiring (separate, still-unscheduled follow-up noted on issue #6)."
limits: "max_iterations=25; max_minutes=120"
labels: [frontend, phase-5]
---

# SYNTH-15: Runtime base-path support for the client build

## Context
Second and final checklist item on issue #6 (Phase 5: Client), after
SYNTH-14 (PR #20) scaffolded the Vue 3 + Vite SPA at `src/client`. The
requirement — "single build deployable under any sub-path" — means the same
`dist/` output must work whether it's served at `/`, `/synth/`, or any other
prefix, without a separate build per deployment target. Vite's default
production build bakes an absolute `base` (`/`) into asset URLs
(`<script src="/assets/...">`), which breaks under a sub-path unless you
rebuild with `--base=/some/prefix/`. That per-deployment rebuild is exactly
what this task avoids.

There is no Vue Router or routing yet (SYNTH-14 is a static placeholder), so
this task is scoped to asset/base URL resolution only — client-side route
base-path handling is future scope for whenever routing is actually added.

## What to do
1. Make the Vite build emit relative asset references instead of
   root-absolute ones, so `dist/index.html` and its assets work when the
   `dist/` folder is copied to any sub-path and served from there. The
   standard mechanism is `base: './'` (relative base) in `vite.config.ts` —
   use that unless you find a more robust approach the SDK/Vite version here
   actually supports (explain your choice briefly in the final report if you
   deviate).
2. Verify this actually works: build once, then confirm (e.g. by inspecting
   `dist/index.html`) that script/link/icon references use relative paths
   (`./assets/...` or bare `assets/...`), not `/assets/...`.
3. Add a check that encodes this guarantee going forward — either a small
   test/script asserting the build output has no root-absolute asset paths,
   or (if simpler and equally solid) keep it verifiable purely through the
   acceptance_command's own grep. Prefer something that would fail loudly if
   a future change reintroduces an absolute base.
4. Document the convention briefly (a short comment in `vite.config.ts` or a
   line in `src/client/README.md` if one exists) so future contributors don't
   accidentally revert to an absolute base.
5. Keep `npm run build`/`npm test` and all existing `dotnet build`/`dotnet
   test` green.

## Acceptance
`npm --prefix src/client install/build/test` succeed, and the built
`dist/index.html` contains no root-absolute (`/assets/...`) references
(mirrors the frontmatter `acceptance_command`'s negative grep).

## Out of scope
- Vue Router / client-side route base-path handling (no routes exist yet).
- The Aspire `AddNpmApp` AppHost wiring noted as a follow-up on issue #6.
- Any real feature pages, backend, or MCP-layer changes.
