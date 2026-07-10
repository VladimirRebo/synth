<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { getIndexStatus, indexSource, type IndexJobStatus } from '../api'
import { useRepositories } from '../composables/useRepositories'
import Icon from './Icon.vue'

type Mode = 'local' | 'remote'

const POLL_INTERVAL_MS = 1000

const { repositories, refresh: refreshRepositories } = useRepositories()

const mode = ref<Mode>('local')
const path = ref('')
const repoUrl = ref('')
const branch = ref('')
const submitError = ref('') // from the POST itself (400/409) — distinct from a Failed job's own error
const submitting = ref(false) // true only for the brief window between POST and the first poll
const job = ref<IndexJobStatus | null>(null)

let pollTimer: ReturnType<typeof setInterval> | null = null

// Server-side job state (SYNTH-31/#39), not client-held — polling it on mount is what makes
// progress survive a page reload: the job keeps running on the server regardless of the tab.
async function pollJob() {
  try {
    job.value = await getIndexStatus()
  } catch {
    return // transient poll failure — keep the last known state, try again next tick
  }
  if (job.value.state === 'Running') {
    if (pollTimer === null) startPolling()
  } else {
    stopPolling()
    if (job.value.state === 'Done') refreshRepositories()
  }
}

function startPolling() {
  stopPolling()
  pollTimer = setInterval(pollJob, POLL_INTERVAL_MS)
}

function stopPolling() {
  if (pollTimer !== null) {
    clearInterval(pollTimer)
    pollTimer = null
  }
}

onMounted(() => {
  refreshRepositories()
  pollJob() // resumes an in-flight or just-finished job even after a reload
})

onUnmounted(stopPolling)

const statusLabel = computed(() => {
  if (submitError.value) return 'Error'
  if (submitting.value) return 'Starting…'
  const state = job.value?.state
  if (state === 'Running') {
    const indexed = job.value?.filesIndexed ?? 0
    const total = job.value?.totalFiles
    return total != null ? `Indexing… ${indexed}/${total} files` : `Indexing… ${indexed} files`
  }
  if (state === 'Done') return 'Done'
  if (state === 'Failed') return 'Error'
  return 'Idle'
})

const statusClass = computed(() => {
  if (submitError.value || job.value?.state === 'Failed') return 'error'
  if (submitting.value || job.value?.state === 'Running') return 'indexing'
  if (job.value?.state === 'Done') return 'done'
  return 'idle'
})

const displayError = computed(() => submitError.value || (job.value?.state === 'Failed' ? job.value.error : ''))

const canSubmit = computed(() => {
  const hasInput = mode.value === 'local' ? path.value.trim().length > 0 : repoUrl.value.trim().length > 0
  return hasInput && !submitting.value && job.value?.state !== 'Running'
})

async function onSubmit() {
  if (!canSubmit.value) return

  submitError.value = ''
  submitting.value = true
  try {
    await indexSource(
      mode.value === 'local'
        ? { path: path.value.trim() }
        : { repoUrl: repoUrl.value.trim(), branch: branch.value.trim() || undefined },
    )
    await pollJob()
  } catch (err) {
    submitError.value = err instanceof Error ? err.message : String(err)
  } finally {
    submitting.value = false
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
      <span class="status" :class="statusClass">{{ statusLabel }}</span>
      <button type="submit" :disabled="!canSubmit">Index</button>
    </form>

    <p v-if="displayError" class="error" role="alert">{{ displayError }}</p>
    <p v-else-if="job?.state === 'Done'" class="summary">
      Indexed {{ job.filesIndexed }} file{{ job.filesIndexed === 1 ? '' : 's' }}
      ({{ job.chunksIndexed }} chunk{{ job.chunksIndexed === 1 ? '' : 's' }}<span
        v-if="job.filesSkipped > 0"
      >, {{ job.filesSkipped }} skipped</span
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
