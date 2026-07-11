<script setup lang="ts">
import { useTheme } from '../composables/useTheme'
import Icon from './Icon.vue'

const { theme, toggle } = useTheme()

const items = [
  { to: '/search', label: 'Search', icon: 'search' },
  { to: '/browse', label: 'Browse', icon: 'file-text' },
  { to: '/index', label: 'Index', icon: 'folder' },
  { to: '/mcp', label: 'MCP', icon: 'plug' },
  { to: '/settings', label: 'Settings', icon: 'sliders' },
  { to: '/logs', label: 'Logs', icon: 'list' },
] as const
</script>

<template>
  <aside class="sidebar">
    <h1 class="brand">Synth</h1>
    <nav class="nav" aria-label="Main navigation">
      <router-link v-for="item in items" :key="item.to" :to="item.to" class="nav-link" active-class="active">
        <Icon :name="item.icon" :size="18" />
        {{ item.label }}
      </router-link>
    </nav>
    <button
      type="button"
      class="theme-toggle"
      :aria-label="theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'"
      @click="toggle"
    >
      <Icon :name="theme === 'dark' ? 'sun' : 'moon'" :size="16" />
      {{ theme === 'dark' ? 'Light' : 'Dark' }}
    </button>
  </aside>
</template>

<style scoped>
.sidebar {
  width: 200px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  gap: 24px;
  padding: 24px 16px;
  border-right: 1px solid var(--border);
  height: 100vh;
  box-sizing: border-box;
  position: sticky;
  top: 0;
}

.brand {
  margin: 0;
  padding: 0 8px;
  font-size: 20px;
}

.nav {
  display: flex;
  flex-direction: column;
  gap: 2px;
  flex: 1;
}

.nav-link {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px;
  border-radius: 6px;
  color: var(--text);
  text-decoration: none;
  font-size: 14px;
}

.nav-link:hover {
  background: var(--code-bg);
  color: var(--text-h);
}

.nav-link.active {
  background: var(--accent-bg);
  color: var(--accent);
  font-weight: 600;
}

.theme-toggle {
  display: flex;
  align-items: center;
  gap: 8px;
  font: inherit;
  font-size: 13px;
  padding: 8px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--bg);
  color: var(--text);
  cursor: pointer;
}

.theme-toggle:hover {
  border-color: var(--accent-border);
  color: var(--accent);
}
</style>
