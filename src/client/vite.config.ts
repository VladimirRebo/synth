/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// https://vite.dev/config/
export default defineConfig({
  // Relative base ('./') so a single `dist/` build is deployable under ANY
  // sub-path (`/`, `/synth/`, …) without rebuilding: Vite emits relative asset
  // references (`./assets/...`) instead of root-absolute ones (`/assets/...`).
  // Do NOT change this back to an absolute base — see SYNTH-15 / issue #6.
  base: './',
  plugins: [vue()],
  test: {
    environment: 'jsdom',
  },
})
