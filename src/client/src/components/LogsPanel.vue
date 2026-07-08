<script setup lang="ts">
import { onUnmounted, ref, watch } from 'vue'
import { getLogs, type LogEntry } from '../api'
import Icon from './Icon.vue'

const POLL_INTERVAL_MS = 3000

const expanded = ref(false)
const entries = ref<LogEntry[]>([])
const error = ref('')
const levelFilter = ref('')
const searchFilter = ref('')
const autoRefresh = ref(true)

let pollTimer: ReturnType<typeof setInterval> | null = null

async function fetchLogs() {
  try {
    entries.value = await getLogs({ level: levelFilter.value || undefined, search: searchFilter.value || undefined })
    error.value = ''
  } catch (err) {
    error.value = err instanceof Error ? err.message : String(err)
  }
}

function startPolling() {
  stopPolling()
  pollTimer = setInterval(() => {
    if (autoRefresh.value) fetchLogs()
  }, POLL_INTERVAL_MS)
}

function stopPolling() {
  if (pollTimer !== null) {
    clearInterval(pollTimer)
    pollTimer = null
  }
}

async function toggle() {
  expanded.value = !expanded.value
  if (expanded.value) {
    await fetchLogs()
    startPolling()
  } else {
    stopPolling()
  }
}

watch([levelFilter, searchFilter], () => {
  if (expanded.value) fetchLogs()
})

onUnmounted(stopPolling)

const levelClass = (level: string) => `level-${level.toLowerCase()}`
</script>

<template>
  <section class="panel">
    <button type="button" class="panel-toggle" :aria-expanded="expanded" @click="toggle">
      <Icon name="list" :size="16" />
      <h2>Logs</h2>
      <Icon name="chevron-down" :size="16" class="chevron" :class="{ open: expanded }" />
    </button>

    <div v-if="expanded" class="body">
      <div class="toolbar">
        <select v-model="levelFilter" aria-label="Minimum log level">
          <option value="">All levels</option>
          <option value="Verbose">Verbose</option>
          <option value="Debug">Debug</option>
          <option value="Information">Information</option>
          <option value="Warning">Warning</option>
          <option value="Error">Error</option>
          <option value="Fatal">Fatal</option>
        </select>
        <input v-model="searchFilter" type="text" placeholder="Filter by message…" aria-label="Filter logs by message" />
        <label class="auto-refresh">
          <input v-model="autoRefresh" type="checkbox" />
          Auto-refresh
        </label>
        <button type="button" class="refresh-button" @click="fetchLogs">Refresh</button>
      </div>

      <p v-if="error" class="error" role="alert">{{ error }}</p>
      <p v-else-if="entries.length === 0" class="empty">No log entries yet.</p>

      <ul v-else class="log-list">
        <li v-for="(entry, index) in entries" :key="`${entry.timestamp}-${index}`" class="log-row" :class="levelClass(entry.level)">
          <span class="timestamp">{{ new Date(entry.timestamp).toLocaleTimeString() }}</span>
          <span class="level">{{ entry.level }}</span>
          <span class="message">{{ entry.message }}</span>
          <pre v-if="entry.exception" class="exception">{{ entry.exception }}</pre>
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.panel {
  text-align: left;
  padding: 24px 0;
  border-bottom: 1px solid var(--border);
}

.panel-toggle {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  border: none;
  background: none;
  padding: 0;
  cursor: pointer;
  color: var(--text-h);
}

.panel-toggle h2 {
  margin: 0;
}

.chevron {
  margin-left: auto;
  transition: transform 0.15s;
  color: var(--text);
}

.chevron.open {
  transform: rotate(180deg);
}

.body {
  margin-top: 16px;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 12px;
}

.toolbar select,
.toolbar input[type='text'] {
  font: inherit;
  padding: 8px 12px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--bg);
  color: var(--text-h);
}

.toolbar input[type='text'] {
  flex: 1;
}

.auto-refresh {
  display: flex;
  align-items: center;
  gap: 4px;
  font-size: 13px;
  color: var(--text);
  white-space: nowrap;
}

.refresh-button {
  font: inherit;
  padding: 8px 12px;
  border-radius: 6px;
  cursor: pointer;
  border: 1px solid var(--accent-border);
  color: var(--accent);
  background: var(--accent-bg);
}

.error {
  color: var(--status-red);
}

.empty {
  color: var(--text);
}

.log-list {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 320px;
  overflow-y: auto;
  border: 1px solid var(--border);
  border-radius: 8px;
  font-family: var(--mono);
  font-size: 12px;
}

.log-row {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 8px;
  padding: 6px 10px;
  border-bottom: 1px solid var(--border);
}

.log-row:last-child {
  border-bottom: none;
}

.timestamp {
  color: var(--text);
  flex-shrink: 0;
}

.level {
  flex-shrink: 0;
  font-weight: 600;
  width: 76px;
}

.message {
  flex: 1;
  color: var(--text-h);
  white-space: pre-wrap;
  word-break: break-word;
}

.exception {
  flex-basis: 100%;
  margin: 4px 0 0;
  color: var(--status-red);
  white-space: pre-wrap;
  word-break: break-word;
}

.level-verbose .level,
.level-debug .level {
  color: var(--text);
}

.level-information .level {
  color: var(--accent);
}

.level-warning .level {
  color: var(--status-yellow);
}

.level-error .level,
.level-fatal .level {
  color: var(--status-red);
}
</style>
