<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { getFileChunks, type FileChunk } from '../api'
import { useRepositories } from '../composables/useRepositories'
import { highlightCode } from '../highlight'

const { repositories, refresh: refreshRepositories } = useRepositories()

// Local-path indexing lands in a path-derived collection (LocalPathSlug), not a shared "default"
// bucket, so there's no fixed name to fall back to. When exactly one collection is indexed —
// the common case for this tool — auto-select it; otherwise the picker requires an explicit choice.
const collection = ref('')
const soleCollection = computed(() =>
  repositories.value.length === 1 ? repositories.value[0].collection : '',
)
const path = ref('')
const chunks = ref<FileChunk[]>([])
const loading = ref(false)
const error = ref('')
const hasBrowsed = ref(false)
// The path that produced the current `chunks`, so the empty state can name it back to the user.
const browsedPath = ref('')

async function onSubmit() {
  const relativePath = path.value.trim()
  if (!relativePath) return

  const target = collection.value || soleCollection.value
  if (!target) {
    error.value = 'Pick a collection first — more than one is indexed.'
    return
  }

  loading.value = true
  error.value = ''

  try {
    chunks.value = await getFileChunks(target, relativePath)
    hasBrowsed.value = true
    browsedPath.value = relativePath
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  refreshRepositories()
})
</script>

<template>
  <section class="panel">
    <h2>Browse</h2>
    <p class="hint">
      Inspect how Synth chunked a file — chunk boundaries, types, and the exact text embedded for
      each chunk. Pick a collection and type a repository-relative path (e.g. <code>src/App.vue</code>).
    </p>

    <form class="browse-form" @submit.prevent="onSubmit">
      <select v-model="collection" aria-label="Collection to browse" class="collection">
        <option value="">{{ soleCollection ? 'Auto' : 'Select a collection' }}</option>
        <option v-for="repo in repositories" :key="repo.collection" :value="repo.collection">
          {{ repo.collection }} ({{ repo.sourceType }})
        </option>
      </select>
      <input
        v-model="path"
        type="text"
        placeholder="Relative path, e.g. src/Program.cs"
        aria-label="Relative file path"
        class="path-input"
      />
      <button type="submit" :disabled="loading">
        {{ loading ? 'Loading…' : 'Browse' }}
      </button>
    </form>

    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <p v-else-if="hasBrowsed && !loading && chunks.length === 0" class="empty">
      No chunks for <code>{{ browsedPath }}</code>. It may not be indexed in this collection, or the
      file has no chunkable content.
    </p>

    <template v-if="chunks.length > 0">
      <p class="count">{{ chunks.length }} chunk{{ chunks.length === 1 ? '' : 's' }}</p>
      <ul class="chunks">
        <li v-for="(chunk, index) in chunks" :key="`${chunk.startLine}-${chunk.endLine}-${index}`" class="chunk">
          <header class="chunk-header">
            <span class="badge">{{ chunk.chunkType }}</span>
            <span class="qualified-name">{{ chunk.qualifiedName || '(file)' }}</span>
            <span class="spacer" />
            <span class="lines">L{{ chunk.startLine }}–{{ chunk.endLine }}</span>
          </header>
          <p v-if="chunk.summary" class="summary">{{ chunk.summary }}</p>
          <p class="section-label">Embedding text</p>
          <pre class="embedding"><code v-html="highlightCode(chunk.embeddingText, browsedPath)" /></pre>
        </li>
      </ul>
    </template>
  </section>
</template>

<style scoped>
.panel {
  text-align: left;
  padding: 24px 0;
}

.hint {
  color: var(--text);
  font-size: 14px;
  margin: 0 0 16px;
}

.hint code,
.empty code {
  font-family: var(--mono);
  font-size: 13px;
}

.browse-form {
  display: flex;
  gap: 8px;
  margin-bottom: 16px;
}

.path-input {
  flex: 1;
}

.collection {
  max-width: 200px;
}

input,
button,
select {
  font: inherit;
  padding: 8px 12px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--bg);
  color: var(--text-h);
}

button {
  cursor: pointer;
  border-color: var(--accent-border);
  color: var(--accent);
  background: var(--accent-bg);
}

button:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}

.error {
  color: var(--status-red);
}

.empty {
  color: var(--text);
}

.count {
  font-size: 13px;
  color: var(--text);
  margin: 0 0 12px;
}

.chunks {
  list-style: none;
  padding: 0;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.chunk {
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 16px;
}

.chunk-header {
  display: flex;
  align-items: center;
  gap: 8px;
}

.spacer {
  flex: 1;
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
  font-size: 14px;
  color: var(--text-h);
  word-break: break-all;
}

.lines {
  color: var(--text);
  font-family: var(--mono);
  font-size: 12px;
  white-space: nowrap;
}

.summary {
  margin: 8px 0 0;
  font-size: 13px;
  color: var(--text);
}

.section-label {
  margin: 12px 0 4px;
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--text);
}

.embedding {
  margin: 0;
  padding: 12px;
  overflow-x: auto;
  background: var(--code-bg);
  border-radius: 4px;
}

.embedding code {
  padding: 0;
  background: none;
  font-size: 13px;
  white-space: pre;
}
</style>
