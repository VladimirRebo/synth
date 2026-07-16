import { describe, it, expect } from 'vitest'
import { useTheme } from './useTheme'

// theme is a module-level singleton initialized once at import time (see the composable's own
// comment) and its watchEffect runs asynchronously, so assertions capture the value *before*
// acting and assert the flip relative to that, rather than assuming a fixed starting theme.
describe('useTheme', () => {
  it('reflects the current theme onto <html data-theme> (async via watchEffect)', async () => {
    const { theme } = useTheme()
    await new Promise((resolve) => setTimeout(resolve, 0)) // let the initial watchEffect flush

    expect(document.documentElement.getAttribute('data-theme')).toBe(theme.value)
  })

  it('toggle() flips between dark and light', async () => {
    const { theme, toggle } = useTheme()
    const before = theme.value

    toggle()

    expect(theme.value).toBe(before === 'dark' ? 'light' : 'dark')
    expect(theme.value).not.toBe(before)
  })

  it('toggle() updates the <html data-theme> attribute and persists to localStorage', async () => {
    const { theme, toggle } = useTheme()
    toggle()
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(document.documentElement.getAttribute('data-theme')).toBe(theme.value)
    expect(localStorage.getItem('synth:theme')).toBe(theme.value)
  })

  it('is a module-level singleton: toggling through one call is visible through another', () => {
    const a = useTheme()
    const b = useTheme()
    const before = a.theme.value

    a.toggle()

    expect(b.theme.value).not.toBe(before)
  })
})
