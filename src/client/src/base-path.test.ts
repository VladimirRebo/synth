import { describe, it, expect } from 'vitest'
import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import viteConfig from '../vite.config'

// SYNTH-15: a single `dist/` build must be deployable under any sub-path.
// These checks fail loudly if a future change reintroduces an absolute base
// (which would bake root-absolute `/assets/...` URLs into the build).
describe('runtime base-path support', () => {
  it('vite config uses a relative base', () => {
    // defineConfig returns the config object as-is.
    const config = viteConfig as { base?: string }
    expect(config.base).toBe('./')
  })

  it('built index.html has no root-absolute asset references', () => {
    // npm runs the test script with cwd = package root (src/client).
    const distIndex = resolve(process.cwd(), 'dist/index.html')
    if (!existsSync(distIndex)) {
      // Only assertable after `npm run build`; the acceptance flow builds first.
      return
    }
    const html = readFileSync(distIndex, 'utf8')
    expect(html).not.toMatch(/src="\/assets/)
    expect(html).not.toMatch(/href="\/assets/)
  })
})
