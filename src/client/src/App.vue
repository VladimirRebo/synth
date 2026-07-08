<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
import { useTheme } from './composables/useTheme'
import { useSearchFocus } from './composables/useSearchFocus'
import Icon from './components/Icon.vue'
import IndexPanel from './components/IndexPanel.vue'
import McpConnectPanel from './components/McpConnectPanel.vue'
import SearchPanel from './components/SearchPanel.vue'

const { theme, toggle } = useTheme()
const { focus } = useSearchFocus()

function onGlobalKeydown(e: KeyboardEvent) {
  if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
    e.preventDefault()
    focus()
  }
}

onMounted(() => document.addEventListener('keydown', onGlobalKeydown))
onUnmounted(() => document.removeEventListener('keydown', onGlobalKeydown))
</script>

<template>
  <main class="app">
    <header class="header">
      <div>
        <h1>Synth</h1>
        <p class="tagline">Personal RAG system for codebases and VCS automation.</p>
      </div>
      <button
        type="button"
        class="theme-toggle"
        :aria-label="theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'"
        @click="toggle"
      >
        <Icon :name="theme === 'dark' ? 'sun' : 'moon'" :size="18" />
      </button>
    </header>
    <IndexPanel />
    <McpConnectPanel />
    <SearchPanel />
  </main>
</template>

<style scoped>
.app {
  max-width: 720px;
  margin: 0 auto;
  padding: 0 20px 48px;
}

.header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.tagline {
  color: var(--text);
  margin-bottom: 8px;
}

.theme-toggle {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  margin-top: 32px;
  border-radius: 8px;
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
