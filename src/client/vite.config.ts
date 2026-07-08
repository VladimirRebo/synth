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
  server: {
    // Proxy same-origin /api/* to the real backend instead of dealing with CORS (the API has
    // no CORS policy, and its port is dynamic under Aspire). Aspire's AppHost.cs already
    // references the `api` resource from `client`, which injects API_HTTP — falls back to
    // Synth.Api's fixed launchSettings.json port for standalone `npm run dev` without Aspire.
    proxy: {
      '/api': {
        target: process.env.API_HTTP || 'http://localhost:5042',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api/, ''),
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test-setup.ts'],
  },
})
