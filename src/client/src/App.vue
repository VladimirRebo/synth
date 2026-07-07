<script setup lang="ts">
import IndexPanel from './components/IndexPanel.vue'
import SearchPanel from './components/SearchPanel.vue'
</script>

<template>
  <main class="app">
    <h1>Synth</h1>
    <p class="tagline">Personal RAG system for codebases and VCS automation.</p>
    <IndexPanel />
    <SearchPanel />
  </main>
</template>

<style scoped>
.app {
  max-width: 720px;
  margin: 0 auto;
  padding: 0 20px 48px;
}

.tagline {
  color: var(--text);
  margin-bottom: 8px;
}
</style>
