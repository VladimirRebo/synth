<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { indexSource, type IndexSummary } from '../api'
import { useRepositories } from '../composables/useRepositories'
import Icon from './Icon.vue'

type Status = 'idle' | 'indexing' | 'done' | 'error'
type Mode = 'local' | 'remote'

const { repositories, refresh: refreshRepositories } = useRepositories()

onMounted(refreshRepositories)

const mode = ref<Mode>('local')
const path = ref('')
const repoUrl = ref('')
const branch = ref('')
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

const canSubmit = computed(() =>
  mode.value === 'local' ? path.value.trim().length > 0 : repoUrl.value.trim().length > 0,
)

async function onSubmit() {
  if (!canSubmit.value) return

  loading.value = true
  error.value = ''
  summary.value = null
  try {
    summary.value =
      mode.value === 'local'
        ? await indexSource({ path: path.value.trim() })
        : await indexSource({
            repoUrl: repoUrl.value.trim(),
            branch: branch.value.trim() || undefined,
          })
    await refreshRepositories()
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <section class="panel">
    <h2>Index a repository</h2>
    <div class="mode-toggle" role="tablist" aria-label="Indexing mode">
      <button
        type="button"
        role="tab"
        :aria-selected="mode === 'local'"
        :class="{ active: mode === 'local' }"
        @click="mode = 'local'"
      >
        <Icon name="folder" :size="14" /> Local path
      </button>
      <button
        type="button"
        role="tab"
        :aria-selected="mode === 'remote'"
        :class="{ active: mode === 'remote' }"
        @click="mode = 'remote'"
      >
        <Icon name="git-branch" :size="14" /> Repository URL
      </button>
    </div>

    <form class="index-form" @submit.prevent="onSubmit">
      <template v-if="mode === 'local'">
        <div class="input-wrap">
          <Icon name="folder" :size="16" class="mode-icon" />
          <input
            v-model="path"
            type="text"
            placeholder="/absolute/path/to/a/directory"
            aria-label="Directory path"
          />
        </div>
      </template>
      <template v-else>
        <div class="input-wrap">
          <Icon name="git-branch" :size="16" class="mode-icon" />
          <input
            v-model="repoUrl"
            type="text"
            placeholder="https://github.com/owner/repo"
            aria-label="Repository URL"
          />
        </div>
        <input
          v-model="branch"
          type="text"
          placeholder="branch (optional)"
          aria-label="Branch"
          class="branch"
        />
      </template>
      <span class="status" :class="status">{{ statusLabel[status] }}</span>
      <button type="submit" :disabled="loading || !canSubmit">Index</button>
    </form>

    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <p v-else-if="summary" class="summary">
      Indexed {{ summary.filesIndexed }} file{{ summary.filesIndexed === 1 ? '' : 's' }}
      ({{ summary.chunksIndexed }} chunk{{ summary.chunksIndexed === 1 ? '' : 's' }}<span
        v-if="summary.filesSkipped > 0"
      >, {{ summary.filesSkipped }} skipped</span
      >).
    </p>

    <h3 class="repos-heading">Indexed repositories</h3>
    <p v-if="repositories.length === 0" class="empty">Nothing indexed yet.</p>
    <ul v-else class="repo-list">
      <li v-for="repo in repositories" :key="repo.collection" class="repo-row">
        <span class="repo-collection">{{ repo.collection }}</span>
        <span class="repo-type" :class="`type-${repo.sourceType}`">{{ repo.sourceType }}</span>
        <span class="repo-source" :title="repo.source">{{ repo.source }}</span>
        <span v-if="repo.branch" class="repo-branch">{{ repo.branch }}</span>
        <span class="repo-chunks">{{ repo.chunkCount }} chunks</span>
        <span class="repo-time">{{ new Date(repo.lastIndexedAt).toLocaleString() }}</span>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.panel {
  text-align: left;
  padding: 24px 0;
}

.mode-toggle {
  display: flex;
  gap: 4px;
  margin-bottom: 10px;
}

.mode-toggle button {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  font-size: 12px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--bg);
  color: var(--text);
  cursor: pointer;
}

.mode-toggle button.active {
  color: var(--accent);
  border-color: var(--accent-border);
  background: var(--accent-bg);
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

.mode-icon {
  color: var(--text);
  flex-shrink: 0;
}

.branch {
  width: 140px;
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

.repos-heading {
  margin: 24px 0 12px;
  font-size: 14px;
  color: var(--text);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.empty {
  color: var(--text);
  font-size: 13px;
}

.repo-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.repo-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: 6px;
  font-size: 13px;
}

.repo-collection {
  font-family: var(--mono);
  font-weight: 600;
  color: var(--text-h);
}

.repo-type {
  font-size: 11px;
  text-transform: uppercase;
  padding: 2px 6px;
  border-radius: 4px;
  background: var(--code-bg);
  color: var(--text);
}

.type-github {
  color: var(--accent);
}

.type-gitlab {
  color: var(--status-yellow);
}

.repo-source {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text);
}

.repo-branch {
  font-family: var(--mono);
  font-size: 12px;
  color: var(--text);
}

.repo-chunks,
.repo-time {
  font-size: 12px;
  color: var(--text);
  white-space: nowrap;
}
</style>
