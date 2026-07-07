<script setup lang="ts">
import type { SearchResult } from '../api'

defineProps<{ result: SearchResult }>()
</script>

<template>
  <article class="result">
    <header class="result-header">
      <span class="path">{{ result.relativePath }}</span>
      <span class="badge">{{ result.chunkType }}</span>
    </header>
    <p class="qualified-name">
      {{ result.qualifiedName }}
      <span class="lines">L{{ result.startLine }}–{{ result.endLine }}</span>
    </p>
    <pre class="snippet"><code>{{ result.snippet }}</code></pre>
  </article>
</template>

<style scoped>
.result {
  text-align: left;
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 16px;
}

.result-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.path {
  font-family: var(--mono);
  font-size: 14px;
  color: var(--text-h);
  word-break: break-all;
}

.badge {
  font-family: var(--mono);
  font-size: 12px;
  padding: 2px 8px;
  border-radius: 999px;
  color: var(--accent);
  background: var(--accent-bg);
  white-space: nowrap;
}

.qualified-name {
  margin: 8px 0;
  font-size: 14px;
}

.lines {
  color: var(--text);
  font-family: var(--mono);
  font-size: 12px;
}

.snippet {
  margin: 0;
  padding: 12px;
  overflow-x: auto;
  background: var(--code-bg);
  border-radius: 4px;
}

.snippet code {
  padding: 0;
  background: none;
  font-size: 13px;
  white-space: pre;
}
</style>
