---
id: SYNTH-14
summary: "Bootstrap Vue 3 + Vite SPA client scaffold"
status: open
acceptance_command: "npm --prefix src/client install --no-audit --no-fund && npm --prefix src/client run build --silent && npm --prefix src/client test --silent -- run && test -f src/client/package.json"
acceptance_criterion: ""
boundaries: "Only scaffold the Vue 3 + Vite SPA under src/client (per AGENTS.md's documented `npm --prefix src/client` convention) and, if straightforward, one AppHost resource entry so it starts alongside the API in dev. Do not implement real pages/features beyond a minimal placeholder, do not add runtime base-path support yet (separate follow-up task), do not touch Synth.Api, Synth.Core, the MCP layer, or the indexing pipeline."
limits: "max_iterations=30; max_minutes=150"
labels: [frontend, scaffold, phase-5]
---

# SYNTH-14: Bootstrap Vue 3 + Vite SPA client scaffold

## Context
Phase 5 (Client, GitHub issue #6) kickoff — the first frontend work in this
repo. `AGENTS.md` already documents the expected location and commands
(`src/client`, `npm --prefix src/client run build` / `test`), so this task
just makes that real. Mirrors how SYNTH-1 bootstrapped the .NET solution
before anything else could build on it.

## What to do
1. Scaffold a Vue 3 + Vite SPA at `src/client` (TypeScript variant preferred,
   e.g. via `npm create vite@latest client -- --template vue-ts` run from
   `src/`, or equivalent manual scaffold if the CLI is unavailable/offline —
   use judgment, the concrete tool matters less than the resulting project
   shape). Keep the default template's structure; you don't need custom
   pages/features yet.
2. Set the page title to "Synth" and replace the default placeholder content
   with something minimal indicating this is Synth's client (a couple of
   lines is enough — this is a scaffold, not a feature).
3. Add a unit test using the template's default test setup if it ships with
   one, otherwise add Vitest + `@vue/test-utils` and one small test for the
   root component (e.g. it renders the "Synth" heading). Wire `npm test` in
   `package.json` to run it non-interactively (e.g. `vitest run`).
4. Confirm `npm run build` produces a production build with no network
   access required beyond the initial `npm install` (no calls to the Synth
   API or any external service at build/test time).
5. If it's simple to do without deep Aspire NodeJs package research, add one
   resource to `src/Synth.AppHost/AppHost.cs` (e.g. via `Aspire.Hosting.NodeJs`'s
   `AddNpmApp`) so the client starts alongside `api` in `dotnet run` dev
   flow. If this turns out to need real investigation/design (which NuGet
   package, which port, proxy config to the API), skip it and leave a note
   in your final report — it is not required for acceptance, just a nice-to-have.
6. Keep all existing `dotnet build`/`dotnet test` results green (unrelated,
   but `scripts/validate.sh` runs both).

## Acceptance
`npm --prefix src/client install/build/test` all succeed (mirrors the
frontmatter `acceptance_command`), and `src/client/package.json` exists.
`dotnet build`/`dotnet test` on `src/Synth.slnx` stay green (unaffected by
this task, `validate.sh` checks them automatically).

## Out of scope
- Runtime base-path support (issue #6's second checklist item — separate
  follow-up task once this scaffold exists).
- Any real feature pages, API calls, routing, or state management.
- Auth, styling system, component library choices beyond Vite's defaults.
- Backend changes of any kind.
