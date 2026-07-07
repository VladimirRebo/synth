<script setup lang="ts">
import { ref } from 'vue'
import { indexDirectory, type IndexSummary } from '../api'

const path = ref('')
const loading = ref(false)
const error = ref('')
const summary = ref<IndexSummary | null>(null)

async function onSubmit() {
  if (!path.value.trim()) return

  loading.value = true
  error.value = ''
  summary.value = null
  try {
    summary.value = await indexDirectory(path.value)
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <section class="panel">
    <h2>Index a directory</h2>
    <form class="index-form" @submit.prevent="onSubmit">
      <input
        v-model="path"
        type="text"
        placeholder="/absolute/path/to/a/directory"
        aria-label="Directory path"
      />
      <button type="submit" :disabled="loading">
        {{ loading ? 'Indexing…' : 'Index' }}
      </button>
    </form>

    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <p v-else-if="summary" class="summary">
      Indexed {{ summary.filesIndexed }} file{{ summary.filesIndexed === 1 ? '' : 's' }}
      ({{ summary.chunksIndexed }} chunk{{ summary.chunksIndexed === 1 ? '' : 's' }}<span
        v-if="summary.filesSkipped > 0"
      >, {{ summary.filesSkipped }} skipped</span
      >).
    </p>
  </section>
</template>

<style scoped>
.panel {
  text-align: left;
  padding: 24px 0;
  border-bottom: 1px solid var(--border);
}

.index-form {
  display: flex;
  gap: 8px;
}

.index-form input {
  flex: 1;
}

input,
button {
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
  color: #e5484d;
  margin: 12px 0 0;
}

.summary {
  color: var(--text);
  margin: 12px 0 0;
}
</style>
