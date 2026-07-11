<script setup lang="ts">
import { computed, ref } from 'vue'
import type { RepositoryEntry, SearchResult } from '../api'
import { highlightCode } from '../highlight'
import { useEditorLink } from '../composables/useEditorLink'
import Icon from './Icon.vue'

const props = defineProps<{
  result: SearchResult
  // The repository this result came from, when known (SearchPanel resolves it from the collection
  // picker / the result's own `collection`). Used to build a local editor deep-link for
  // local-path-indexed results.
  sourceType?: RepositoryEntry['sourceType']
  source?: string
}>()

const COLLAPSED_LINES = 6

const { buildUri } = useEditorLink()

// Only local-path-indexed results get an editor deep-link: repoUrl-indexed results already carry a
// GitHub/GitLab `sourceUrl` (SYNTH-40), and we never show both for one result. Needs the absolute
// root (`source`) to turn the relative path into something an editor can open.
const editorUri = computed(() =>
  props.sourceType === 'local' && props.source && !props.result.sourceUrl
    ? buildUri(props.source, props.result.relativePath, props.result.startLine)
    : null,
)

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
      <a
        v-if="editorUri"
        class="editor-link"
        :href="editorUri"
        :title="`Open in editor at line ${result.startLine}`"
        aria-label="Open in editor"
        @click.stop
      >
        <Icon name="file-text" :size="14" />
      </a>
      <span class="spacer" />
      <!-- Present only in all-collections search (result.collection populated), so single-collection
           results stay uncluttered — shows which repo this hit was found in. -->
      <span v-if="result.collection" class="badge collection-badge">{{ result.collection }}</span>
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

.editor-link {
  display: inline-flex;
  align-items: center;
  color: var(--text);
  flex-shrink: 0;
}

.editor-link:hover {
  color: var(--accent);
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

.collection-badge {
  color: var(--text);
  background: var(--code-bg);
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
