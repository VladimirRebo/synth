<script setup lang="ts">
import { computed, ref } from 'vue'
import type { SearchResult } from '../api'
import { highlightCode } from '../highlight'
import Icon from './Icon.vue'

const props = defineProps<{ result: SearchResult }>()

const COLLAPSED_LINES = 6

const expanded = ref(false)

const lines = computed(() => props.result.snippet.split('\n'))
const isTruncatable = computed(() => lines.value.length > COLLAPSED_LINES)
const displayedSnippet = computed(() =>
  expanded.value || !isTruncatable.value
    ? props.result.snippet
    : lines.value.slice(0, COLLAPSED_LINES).join('\n'),
)
const highlighted = computed(() => highlightCode(displayedSnippet.value, props.result.relativePath))

function toggle() {
  if (isTruncatable.value) expanded.value = !expanded.value
}
</script>

<template>
  <article class="result">
    <header class="result-header" :class="{ clickable: isTruncatable }" @click="toggle">
      <a
        v-if="result.sourceUrl"
        class="path path-link"
        :href="result.sourceUrl"
        target="_blank"
        rel="noopener noreferrer"
        @click.stop
        >{{ result.relativePath }}</a
      >
      <span v-else class="path">{{ result.relativePath }}</span>
      <span class="spacer" />
      <span class="score" :title="`Rerank score: ${result.score.toFixed(3)}`">{{
        result.score.toFixed(2)
      }}</span>
      <span class="badge">{{ result.chunkType }}</span>
      <Icon
        v-if="isTruncatable"
        name="chevron-down"
        :size="14"
        class="chevron"
        :class="{ expanded }"
      />
    </header>
    <p class="qualified-name">
      {{ result.qualifiedName }}
      <span class="lines">L{{ result.startLine }}–{{ result.endLine }}</span>
    </p>
    <pre class="snippet"><code v-html="highlighted" /></pre>
    <button v-if="isTruncatable && !expanded" type="button" class="expand-hint" @click="toggle">
      Show {{ lines.length - COLLAPSED_LINES }} more line{{
        lines.length - COLLAPSED_LINES === 1 ? '' : 's'
      }}…
    </button>
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
  gap: 8px;
}

.result-header.clickable {
  cursor: pointer;
}

.spacer {
  flex: 1;
}

.path {
  font-family: var(--mono);
  font-size: 14px;
  color: var(--text-h);
  word-break: break-all;
}

.path-link {
  color: var(--accent);
  text-decoration: none;
}

.path-link:hover {
  text-decoration: underline;
}

.score {
  font-family: var(--mono);
  font-size: 12px;
  color: var(--text);
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

.chevron {
  color: var(--text);
  transition: transform 0.15s;
}

.chevron.expanded {
  transform: rotate(180deg);
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

.expand-hint {
  margin-top: 6px;
  font: inherit;
  font-size: 12px;
  color: var(--accent);
  background: none;
  border: none;
  cursor: pointer;
  padding: 0;
}
</style>
