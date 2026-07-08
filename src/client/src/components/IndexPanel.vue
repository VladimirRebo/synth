<script setup lang="ts">
import { computed, ref } from 'vue'
import { indexDirectory, type IndexSummary } from '../api'
import Icon from './Icon.vue'

type Status = 'idle' | 'indexing' | 'done' | 'error'

const path = ref('')
const loading = ref(false)
const error = ref('')
const summary = ref<IndexSummary | null>(null)

const status = computed<Status>(() => {
  if (loading.value) return 'indexing'
  if (error.value) return 'error'
  if (summary.value) return 'done'
  return 'idle'
})

const statusLabel: Record<Status, string> = {
  idle: 'Idle',
  indexing: 'Indexing…',
  done: 'Done',
  error: 'Error',
}

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
      <div class="input-wrap">
        <Icon name="folder" :size="16" class="folder-icon" />
        <input
          v-model="path"
          type="text"
          placeholder="/absolute/path/to/a/directory"
          aria-label="Directory path"
        />
      </div>
      <span class="status" :class="status">{{ statusLabel[status] }}</span>
      <button type="submit" :disabled="loading">Index</button>
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
  align-items: center;
  gap: 8px;
}

.input-wrap {
  flex: 1;
  display: flex;
  align-items: center;
  gap: 8px;
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 0 10px;
  background: var(--bg);
}

.input-wrap input {
  border: none;
  padding: 8px 0;
  flex: 1;
}

.input-wrap input:focus {
  outline: none;
}

.folder-icon {
  color: var(--text);
  flex-shrink: 0;
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

.status {
  font-size: 12px;
  font-family: var(--mono);
  color: var(--text);
  white-space: nowrap;
}

.status.indexing {
  color: var(--status-yellow);
}

.status.done {
  color: var(--status-green);
}

.status.error {
  color: var(--status-red);
}

.error {
  color: var(--status-red);
  margin: 12px 0 0;
}

.summary {
  color: var(--text);
  margin: 12px 0 0;
}
</style>
