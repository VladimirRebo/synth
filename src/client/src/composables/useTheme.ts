import { ref, watchEffect } from 'vue'

// Module-level singleton (ES module = shared instance across every import), matching Sonar's
// useTheme pattern. The inline script in index.html sets the initial data-theme attribute
// before Vue mounts (anti-FOUC); this ref takes over from there.
export type Theme = 'dark' | 'light'

const STORAGE_KEY = 'synth:theme'

function initialTheme(): Theme {
  const stored = localStorage.getItem(STORAGE_KEY)
  if (stored === 'dark' || stored === 'light') return stored
  return window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark'
}

const theme = ref<Theme>(initialTheme())

watchEffect(() => {
  document.documentElement.setAttribute('data-theme', theme.value)
  localStorage.setItem(STORAGE_KEY, theme.value)
})

export function useTheme() {
  return {
    theme,
    toggle: () => {
      theme.value = theme.value === 'dark' ? 'light' : 'dark'
    },
  }
}
