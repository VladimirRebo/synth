# Vue 3 + TypeScript + Vite

This template should help get you started developing with Vue 3 and TypeScript in Vite. The template uses Vue 3 `<script setup>` SFCs, check out the [script setup docs](https://v3.vuejs.org/api/sfc-script-setup.html#sfc-script-setup) to learn more.

Learn more about the recommended Project Setup and IDE Support in the [Vue Docs TypeScript Guide](https://vuejs.org/guide/typescript/overview.html#project-setup).

## Deployment: relative base path

`vite.config.ts` sets `base: './'` so a single `dist/` build is deployable
under **any** sub-path (`/`, `/synth/`, …) without rebuilding. Vite emits
relative asset references (`./assets/...`) instead of root-absolute ones
(`/assets/...`), which is what lets the same output be copied to any prefix and
served from there.

Do **not** change this to an absolute base — it would force a separate build per
deployment target. The guarantee is enforced by `src/base-path.test.ts` and by
the SYNTH-15 acceptance grep over `dist/index.html`.
